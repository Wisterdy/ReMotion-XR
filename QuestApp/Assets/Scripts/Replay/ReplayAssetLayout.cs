using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Replay
{
    /// <summary>Metric placements win. Legacy monocular packages use an explicitly estimated layout.</summary>
    public static class ReplayAssetLayout
    {
        public static float EstimatedWidth(ScenePackage.SceneAssetInfo info)
        {
            if (info == null) return .30f;
            if (info.width_metres > 0f) return Mathf.Clamp(info.width_metres, .02f, 5f);
            string label = (info.label ?? "").ToLowerInvariant();
            if (label.Contains("keyboard")) return .45f;
            if (label.Contains("monitor")) return .60f;
            if (label.Contains("mouse")) return .10f;
            return .30f;
        }

        public static void Place(Transform root, Mesh mesh, ScenePackage.SceneAssetInfo info, SessionTimeline timeline, int index)
        {
            float width = EstimatedWidth(info);
            if (info.has_metric_placement)
            {
                root.localPosition = info.world_position;
                root.localRotation = info.world_rotation;
                return;
            }
            FrameData frame = timeline.frames[0];
            foreach (var candidate in timeline.frames)
                if (Mathf.Abs(candidate.timestamp - info.best_frame_timestamp) < Mathf.Abs(frame.timestamp - info.best_frame_timestamp)) frame = candidate;
            Vector3 forward = frame.headRotation * Vector3.forward;
            Vector3 flatForward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
            if (flatForward.sqrMagnitude < .1f) flatForward = Vector3.forward;
            root.localRotation = Quaternion.LookRotation(flatForward, Vector3.up);
            Vector3 offset = new Vector3((index - .5f) * .45f, -.15f, .85f);
            var bbox = info.mask_bbox_xywh;
            if (bbox != null && bbox.Length == 4 && info.image_width > 0 && info.image_height > 0 && bbox[2] > 0)
            {
                // Legacy capture has no calibrated camera intrinsics or metric object pose.
                // Use the observed mask centre with a conservative 60-degree horizontal FOV.
                float focalPixels = info.image_width / (2f * Mathf.Tan(30f * Mathf.Deg2Rad));
                float depth = Mathf.Clamp(width * focalPixels / bbox[2], .30f, 2f);
                offset = new Vector3((bbox[0] + bbox[2] * .5f - info.image_width * .5f) * depth / focalPixels,
                    (info.image_height * .5f - bbox[1] - bbox[3] * .5f) * depth / focalPixels, depth);
            }
            root.localPosition = frame.headPosition + frame.headRotation * offset;
            string label = (info.label ?? "").ToLowerInvariant();
            if (label.Contains("keyboard") || label.Contains("mouse"))
            {
                float height = 0f; int count = 0;
                if (frame.leftTracked && frame.leftHandJoints != null && frame.leftHandJoints.Length > 0) { height += frame.leftHandJoints[0].y; count++; }
                if (frame.rightTracked && frame.rightHandJoints != null && frame.rightHandJoints.Length > 0) { height += frame.rightHandJoints[0].y; count++; }
                if (count > 0) { var p = root.localPosition; p.y = height / count - .025f; root.localPosition = p; }
            }
            Debug.Log($"[ReMotion] Estimated layout: {info.label}, width={width:F2}m; metric camera/object pose absent.");
        }

        public static GameObject AddModel(Transform parent, Mesh mesh, Color color, Texture2D texture, ScenePackage.SceneAssetInfo info)
        {
            var go = new GameObject("SamObjectMesh"); go.transform.SetParent(parent, false);
            float scale = EstimatedWidth(info) / Mathf.Max(mesh.bounds.size.x, .001f);
            go.transform.localScale = Vector3.one * scale;
            go.transform.localPosition = -mesh.bounds.center * scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var shader = Resources.Load<Shader>("ReMotionTextured");
            if (shader == null) throw new System.InvalidOperationException("Missing bundled replay texture shader");
            var material = new Material(shader) { color = color, mainTexture = texture != null ? texture : Texture2D.whiteTexture };
            renderer.sharedMaterial = material;
            var owner = go.AddComponent<ReplayAssetResources>(); owner.mesh = mesh; owner.texture = texture; owner.material = material;
            Debug.Log($"[ReMotion] Replay model {info.label}: width={renderer.bounds.size.x:F3}m texture={(texture != null ? texture.width : 0)}px");
            return go;
        }
    }

    public sealed class ReplayAssetResources : MonoBehaviour
    {
        public Mesh mesh; public Texture2D texture; public Material material;
        private void OnDestroy() { Release(mesh); Release(texture); Release(material); }
        private static void Release(Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
    }
}
