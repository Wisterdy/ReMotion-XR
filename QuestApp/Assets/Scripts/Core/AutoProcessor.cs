using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using ReMotionXR.Capture;
using ReMotionXR.Library;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Automatic processing via the FastAPI gateway (/v1): the headset never calls RunPod directly.
    /// POST {endpoint}/run  -> poll GET {endpoint}/status/{job_id}
    /// RunPod states: IN_QUEUE (no GPU free yet), IN_PROGRESS, COMPLETED,
    /// FAILED. The app never assumes a fixed pod and never stops polling
    /// on IN_QUEUE. Endpoint URL + REMOTION_API_KEY come from
    /// ServerSettings (Settings UI), never hardcoded.
    /// A local FastAPI fallback (upload/status/download) is used when no
    /// Serverless endpoint is configured.
    /// </summary>
    public class AutoProcessor : MonoBehaviour
    {
        public ServerSettings settings;
        public float pollIntervalSeconds = 2f;

        public event Action<string, string, int> OnStatusChanged; // id, state, progress
        public event Action<string> OnSceneReady;
        public event Action<string, string> OnFailed;

        [Serializable] private class RunResponse { public string id; public string status; }
        [Serializable] private class JobOutput { public string session_id; public string state; public int progress; public string error; public string download_url; }
        [Serializable] private class StatusResponse { public string id; public string status; public JobOutput output; }
        [Serializable] private class UploadResponse { public string session_id; public string state; }
        [Serializable] private class LocalStatus { public string session_id; public string state; public int progress; public string error; }

        // The Quest talks only to the FastAPI gateway (/v1). RunPod is
        // reached server-side by the gateway; no provider credential
        // or provider URL is ever used from the headset.
        private string GatewayBase => settings != null && settings.HasGateway
            ? settings.GatewayUrl
            : (settings != null ? settings.localServerUrl.TrimEnd('/') : "http://127.0.0.1:8000");
        private string ApiKey => settings != null ? settings.remotionApiKey : "";
        public float maxPollSeconds = 3600f;
        private readonly System.Collections.Generic.HashSet<string> _activeSessions = new();

        private void ApplyAppKey(UnityWebRequest req)
        {
            if (!string.IsNullOrEmpty(ApiKey)) req.SetRequestHeader("X-API-Key", ApiKey);
        }

        private void PersistServerIdentity(string localSessionId, string serverSessionId, int attempt)
        {
            try
            {
                string metaPath = Path.Combine(SessionRecorder.SessionFolder(localSessionId), "metadata.json");
                if (!File.Exists(metaPath)) return;
                var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath));
                if (meta == null) return;
                meta.serverSessionId = serverSessionId;
                meta.jobId = serverSessionId;
                meta.attempt = attempt;
                File.WriteAllText(metaPath, JsonUtility.ToJson(meta, true));
            }
            catch (Exception e) { Debug.LogWarning("[ReMotion] Server identity write failed: " + e.Message); }
        }

        private void ReportFailure(string id, string message)
        {
            UpdateSamStatus(id, "Failed", 0);
            OnFailed?.Invoke(id, message);
        }

        private void Awake() => settings?.LoadRuntimeOverrides();

        private void Start()
        {
            // Resume real remote jobs only. Never manufacture a local scene on restart.
            if (settings == null || !settings.HasGateway) return;
            string root = Path.Combine(Application.persistentDataPath, "Library");
            if (!Directory.Exists(root)) return;
            foreach (string folder in Directory.GetDirectories(root))
            {
                string path = Path.Combine(folder, "metadata.json");
                if (!File.Exists(path)) continue;
                SessionMetadata meta;
                try { meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(path)); }
                catch (Exception) { continue; }
                if (meta == null || meta.status != "Processing" || string.IsNullOrEmpty(meta.sessionId)) continue;
                if (string.IsNullOrEmpty(meta.serverSessionId)) UploadSession(meta.sessionId);
                else if (_activeSessions.Add(meta.sessionId))
                    StartCoroutine(RunCloudJob(meta.sessionId, PollGateway(meta.sessionId, meta.serverSessionId)));
            }
        }

        private IEnumerator RunCloudJob(string id, IEnumerator operation)
        {
            try { yield return operation; }
            finally { _activeSessions.Remove(id); }
        }

        public void UploadSession(string localSessionId)
        {
            if (!_activeSessions.Add(localSessionId)) return;
            UpdateSamStatus(localSessionId, "Processing", 0);
            StartCoroutine(RunCloudJob(localSessionId, GatewayUpload(localSessionId)));
        }

        /// <summary>Writes SAM job status into the session metadata (Library reads it from there).</summary>
        public void UpdateSamStatus(string localSessionId, string samStatus, int progress)
        {
            try
            {
                string metaPath = Path.Combine(SessionRecorder.SessionFolder(localSessionId), "metadata.json");
                if (!File.Exists(metaPath)) return;
                var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath));
                if (meta == null) return;
                meta.samStatus = samStatus;
                if (samStatus == "Failed" || samStatus == "Processing") meta.status = samStatus;
                meta.samProgress = progress;
                File.WriteAllText(metaPath, JsonUtility.ToJson(meta, true));
            }
            catch (Exception e) { Debug.LogWarning("[ReMotion] SAM status write failed: " + e.Message); }
        }

        /// <summary>
        /// On-device processing: builds the ScenePackage straight from the
        /// recorded timeline.json. This is what makes a standalone headset
        /// work with no PC/server at all — the server path only adds AI
        /// reconstruction quality when it is actually reachable.
        /// </summary>
        public bool ProcessLocally(string localSessionId, out string error)
        {
            error = null;
            string folder = SessionRecorder.SessionFolder(localSessionId);
            string timelinePath = Path.Combine(folder, "timeline.json");
            if (!File.Exists(timelinePath)) { error = "No recorded timeline to process."; return false; }
            var timeline = JsonUtility.FromJson<SessionTimeline>(File.ReadAllText(timelinePath));
            if (timeline == null || timeline.frames == null || timeline.frames.Count == 0)
            { error = "Recorded timeline is empty."; return false; }
            if (!ScenePackage.TryCreatePackage(timeline, out byte[] packageBytes, out error))
                return false;
            // On-device package is a timeline scene, NOT a SAM model:
            // immersive stays locked until the pod job reports Ready.
            return TryMarkReady(localSessionId, packageBytes, out error, samStatus: "Unavailable");
        }

        /// <summary>
        /// Rescues captures left in Processing by an unreachable server
        /// (e.g. recorded standalone before local processing existed).
        /// Returns how many sessions became Ready.
        /// </summary>
        public int RecoverProcessingSessions()
        {
            string root = Path.Combine(Application.persistentDataPath, "Library");
            if (!Directory.Exists(root)) return 0;
            int recovered = 0;
            foreach (string dir in Directory.GetDirectories(root))
            {
                string metaPath = Path.Combine(dir, "metadata.json");
                if (!File.Exists(metaPath)) continue;
                if (File.Exists(Path.Combine(dir, "scene.remotion"))) continue;
                var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath));
                if (meta == null || meta.status != "Processing") continue;
                if (ProcessLocally(meta.sessionId, out _)) recovered++;
            }
            return recovered;
        }


        // ---------- Gateway (/v1) ----------

        private IEnumerator GatewayUpload(string localSessionId)
        {
            string folder = SessionRecorder.SessionFolder(localSessionId);
            string timelinePath = Path.Combine(folder, "timeline.json");
            string videoPath = Path.Combine(folder, "original_video.mp4");
            if (!File.Exists(videoPath) || new FileInfo(videoPath).Length == 0)
            {
                ReportFailure(localSessionId,
                    "No playable original video to upload; nothing was sent.");
                yield break;
            }
            string metadataPath = Path.Combine(folder, "metadata.json");
            var metadata = File.Exists(metadataPath)
                ? JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metadataPath)) : null;
            bool rebuild = !string.IsNullOrEmpty(metadata?.serverSessionId);
            var form = new WWWForm();
            if (!rebuild)
            {
                string timelineJson = File.Exists(timelinePath) ? File.ReadAllText(timelinePath) : "{}";
                form.AddBinaryData("video", File.ReadAllBytes(videoPath), "original_video.mp4", "video/mp4");
                form.AddField("timeline", timelineJson);
            }
            string route = rebuild ? "/v1/sessions/" + metadata.serverSessionId + "/rebuild" : "/v1/sessions";
            using var req = UnityWebRequest.Post(GatewayBase + route, form);
            req.timeout = 60;
            ApplyAppKey(req);
            req.SetRequestHeader("Idempotency-Key", localSessionId);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                // Auth/network failure is reported, never masked by a
                // silent on-device preview. Preview is an explicit action.
                ReportFailure(localSessionId,
                    $"Gateway upload failed ({req.responseCode}): {req.error}");
                yield break;
            }
            var upload = JsonUtility.FromJson<UploadResponse>(req.downloadHandler.text);
            if (upload == null || string.IsNullOrEmpty(upload.session_id))
            {
                ReportFailure(localSessionId, "Gateway returned no session id.");
                yield break;
            }
            PersistServerIdentity(localSessionId, upload.session_id, rebuild ? metadata.attempt + 1 : 1);
            OnStatusChanged?.Invoke(localSessionId, upload.state ?? "QUEUED", 0);
            yield return PollGateway(localSessionId, upload.session_id);
        }

        /// <summary>Polls the gateway (not RunPod) until a terminal state or the poll deadline.</summary>
        public IEnumerator PollServerless(string localSessionId, string jobId) =>
            PollGateway(localSessionId, jobId);

        private IEnumerator PollGateway(string localSessionId, string serverSessionId)
        {
            float deadline = Time.realtimeSinceStartup + maxPollSeconds;
            while (true)
            {
                using var req = UnityWebRequest.Get($"{GatewayBase}/v1/sessions/{serverSessionId}");
                req.timeout = 30;
                ApplyAppKey(req);
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    ReportFailure(localSessionId, req.error);
                    yield break;
                }
                var s = JsonUtility.FromJson<LocalStatus>(req.downloadHandler.text);
                OnStatusChanged?.Invoke(localSessionId, s.state, s.progress);
                UpdateSamStatus(localSessionId, "Processing", s.progress);
                if (s.state == "READY")
                {
                    yield return DownloadAndMarkReady(localSessionId,
                        $"{GatewayBase}/v1/sessions/{serverSessionId}/package");
                    yield break;
                }
                if (s.state == "FAILED" || s.state == "CANCELLED")
                {
                    ReportFailure(localSessionId, s.error ?? "Pipeline failed");
                    yield break;
                }
                if (Time.realtimeSinceStartup > deadline)
                {
                    ReportFailure(localSessionId, "Gateway processing timed out.");
                    yield break;
                }
                yield return new WaitForSeconds(pollIntervalSeconds);
            }
        }

        // ---------- Server-side delete (best effort, local FastAPI) ----------

        /// <summary>kind: "all" | "original" | "virtual". No-op offline.</summary>
        public IEnumerator DeleteOnServer(string sessionId, string kind)
        {
            // Scoped gateway delete (original|virtual|all).
            
            string scope = kind;
            using var req = UnityWebRequest.Delete($"{GatewayBase}/v1/sessions/{sessionId}?scope={scope}");
            ApplyAppKey(req); yield return req.SendWebRequest(); // best effort; local delete already done
        }

        private IEnumerator DownloadAndMarkReady(string localSessionId, string downloadUrl)
        {
            using var req = UnityWebRequest.Get(downloadUrl);
            req.timeout = 120;
            if (downloadUrl.StartsWith(GatewayBase))
                ApplyAppKey(req); // gateway URLs only; never a provider Bearer
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                ReportFailure(localSessionId, "Package download failed: " + req.error);
                yield break;
            }
            if (TryMarkReady(localSessionId, req.downloadHandler.data, out string error))
                OnSceneReady?.Invoke(localSessionId);
            else
                ReportFailure(localSessionId, error);
        }

        /// <summary>
        /// The only path to Ready: the downloaded bytes must parse as a
        /// real ScenePackage, then they are written as scene.remotion and
        /// metadata flips to Ready. Invalid or failed downloads leave the
        /// session Processing and write nothing.
        /// </summary>
        public bool TryMarkReady(string localSessionId, byte[] packageBytes, out string error,
                                 string samStatus = "Ready")
        {
            if (!ScenePackage.TryParseTimeline(packageBytes, out _, out error))
                return false;
            string folder = SessionRecorder.SessionFolder(localSessionId);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "scene.remotion"), packageBytes);
            string metaPath = Path.Combine(folder, "metadata.json");
            var meta = File.Exists(metaPath)
                ? JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath))
                : new SessionMetadata { sessionId = localSessionId };
            meta.status = "Ready";
            meta.samStatus = samStatus;
            meta.samProgress = samStatus == "Ready" ? 100 : meta.samProgress;
            File.WriteAllText(metaPath, JsonUtility.ToJson(meta, true));
            return true;
        }
    }
}
