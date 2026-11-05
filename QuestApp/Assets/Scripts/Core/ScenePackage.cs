using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;

namespace ReMotionXR.Core
{
    /// <summary>
    /// The single .remotion package contract, shared with the server
    /// (Server/app/services/packaging.py): a ZIP containing scene.json and
    /// timeline.json, where timeline.json is a SessionTimeline JSON.
    /// A plain timeline JSON payload is also accepted (offline authoring).
    /// Anything else — including the old "stub scene package" placeholder
    /// text — is rejected, so READY can never mean a placeholder file.
    /// </summary>
    public static class ScenePackage
    {
        public const string FormatId = "remotion-2";
        public const string LegacyFormatId = "remotion-1";
        public const string TimelineEntry = "timeline.json";
        public const string SceneEntry = "scene.json";

        public static bool TryParseTimeline(byte[] bytes, out SessionTimeline timeline, out string error)
        {
            timeline = null;
            error = null;
            if (bytes == null || bytes.Length == 0)
            {
                error = "Package is empty.";
                return false;
            }

            string json = null;
            if (LooksLikeJson(bytes))
            {
                json = Encoding.UTF8.GetString(bytes);
            }
            else
            {
                try
                {
                    using var stream = new MemoryStream(bytes);
                    using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                    if (zip.Entries.Count > 64) { error = "Package has too many entries."; return false; }
                    foreach (var e in zip.Entries) { if (e.Length > 64 * 1024 * 1024) { error = "Package entry too large: " + e.FullName; return false; } if (e.FullName.Contains("..")) { error = "Package entry path is unsafe: " + e.FullName; return false; } }
                    var entry = zip.GetEntry(TimelineEntry);
                    if (entry == null)
                    {
                        error = $"Package has no {TimelineEntry} entry.";
                        return false;
                    }
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    json = reader.ReadToEnd();
                }
                catch (Exception e)
                {
                    error = "Package is not a valid .remotion zip: " + e.Message;
                    return false;
                }
            }

            SessionTimeline parsed;
            try { parsed = JsonUtility.FromJson<SessionTimeline>(json); }
            catch (Exception e)
            {
                error = "Package timeline is not valid JSON: " + e.Message;
                return false;
            }
            if (parsed == null || parsed.frames == null || parsed.frames.Count == 0)
            {
                error = "Package timeline has no frames.";
                return false;
            }
            timeline = parsed;
            return true;
        }

        [Serializable] public class ObjectPose { public float timestamp; public Vector3 position; public string source; }

        /// <summary>One server-built asset entry from scene.json.</summary>
        [Serializable]
        public class SceneAssetInfo
        {
            public string track_id;
            public string kind; // "person" | "object"
            public string label; // identified concept, e.g. "Computer Monitor"
            public string model; // zip entry path, e.g. assets/person_01.glb
            public string sam_backend;
            public string reconstruction_backend;
            public float[] mask_bbox_xywh;
            public int image_width;
            public int image_height;
            public float best_frame_timestamp;
            public Vector3 world_position;
            public Quaternion world_rotation;
            public float width_metres;
            public bool has_metric_placement;
            public ObjectPose[] object_poses;
        }

        [Serializable]
        private class SceneDocument
        {
            public string format;
            public List<SceneAssetInfo> assets = new List<SceneAssetInfo>();
        }

        /// <summary>A parsed asset: metadata plus its raw GLB bytes.</summary>
        public class SceneAsset
        {
            public SceneAssetInfo Info;
            public byte[] GlbBytes;
        }

        /// <summary>
        /// Read scene.json assets (with GLB payloads) from a .remotion zip.
        /// Returns an empty list for plain-timeline packages; false only
        /// when the package is not a readable zip at all.
        /// </summary>
        public static bool TryParseAssets(byte[] bytes, out List<SceneAsset> assets, out string error)
        {
            assets = new List<SceneAsset>();
            error = null;
            if (bytes == null || bytes.Length == 0 || LooksLikeJson(bytes))
                return true; // plain timeline JSON carries no models
            try
            {
                using var stream = new MemoryStream(bytes);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var sceneEntry = zip.GetEntry(SceneEntry);
                if (sceneEntry == null) return true;
                string sceneJson;
                using (var reader = new StreamReader(sceneEntry.Open(), Encoding.UTF8))
                    sceneJson = reader.ReadToEnd();
                var doc = JsonUtility.FromJson<SceneDocument>(sceneJson);
                if (doc == null || doc.assets == null) return true;
                foreach (var info in doc.assets)
                {
                    if (info == null || string.IsNullOrEmpty(info.model)) continue;
                    var modelEntry = zip.GetEntry(info.model);
                    if (modelEntry == null) continue;
                    using var ms = new MemoryStream();
                    using (var es = modelEntry.Open()) es.CopyTo(ms);
                    assets.Add(new SceneAsset { Info = info, GlbBytes = ms.ToArray() });
                }
                return true;
            }
            catch (Exception e)
            {
                error = "Package assets unreadable: " + e.Message;
                return false;
            }
        }

        public static bool TryCreatePackage(SessionTimeline timeline, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            if (timeline == null || timeline.frames == null || timeline.frames.Count == 0)
            {
                error = "Cannot package an empty timeline.";
                return false;
            }
            try
            {
                using var stream = new MemoryStream();
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    WriteEntry(zip, SceneEntry,
                        "{\"format\":\"" + FormatId + "\",\"action_sequence\":\"" +
                        (timeline.action_sequence ?? "unknown") + "\"}");
                    WriteEntry(zip, TimelineEntry, JsonUtility.ToJson(timeline, true));
                }
                bytes = stream.ToArray();
                return true;
            }
            catch (Exception e)
            {
                error = "Failed to build package: " + e.Message;
                return false;
            }
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }

        private static bool LooksLikeJson(byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                if (b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n' ||
                    b == 0xEF || b == 0xBB || b == 0xBF) continue;
                return b == (byte)'{';
            }
            return false;
        }
    }
}
