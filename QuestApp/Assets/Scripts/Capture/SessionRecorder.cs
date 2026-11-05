using System;
using System.Collections;
using System.IO;
using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Capture
{
    /// <summary>
    /// RECORD -> perform -> SAVE. Writes a session folder to
    /// persistentDataPath/Library/{id}/ with timeline.json + metadata,
    /// plus the real passthrough-camera original_video.mp4 produced by
    /// QuestCameraCapture. This recorder is the authoritative capture
    /// state machine; the UI derives its controls from State.
    ///
    /// Flow: Idle -> RequestingPermission -> Starting -> Recording
    ///       -> Finalizing -> Saved | Failed.
    /// Recording is announced only after permission is granted, the
    /// encoder has started AND a first valid camera frame has arrived.
    /// </summary>
    public class SessionRecorder : MonoBehaviour
    {
        public HandPoseRecorder handRecorder;
        public Transform headTransform;
        public Core.AutoProcessor autoProcessor;
        /// <summary>Quest passthrough camera capture for the original video.</summary>
        public QuestCameraCapture cameraCapture;
        /// <summary>Free-form title the user gives this capture (optional, set in Capture mode).</summary>
        public string captureTitle = "";

        /// <summary>Finds/creates the camera capture component (also used to pre-warm).</summary>
        public QuestCameraCapture EnsureCameraCapture()
        {
            cameraCapture ??= GetComponent<QuestCameraCapture>()
                              ?? gameObject.AddComponent<QuestCameraCapture>();
            return cameraCapture;
        }

        public bool IsRecording { get; private set; }
        public string CurrentSessionId { get; private set; }
        public bool LastSaveSucceeded { get; private set; }

        public enum CaptureState { Idle, RequestingPermission, Starting, Recording, Finalizing, Saved, Failed }
        public CaptureState State { get; private set; } = CaptureState.Idle;
        public string LastError { get; private set; }
        public event Action<CaptureState> OnStateChanged;

        /// <summary>Bounded waits so a dead camera can never hang startup.</summary>
        public float PermissionTimeoutSeconds = 15f;
        public float FirstFrameTimeoutSeconds = 6f;

        private SessionTimeline _timeline;
        private double _startRealtime; // monotonic capture clock (seconds)
        private Coroutine _loop;
        private Coroutine _startupWatchdog;
        private int _attempt; // generation: late callbacks from old attempts are rejected

        /// <summary>Seconds since capture timing began (Recording elapsed).</summary>
        public double RecordingElapsedSeconds =>
            State == CaptureState.Recording || State == CaptureState.Finalizing
                ? Math.Max(0.0, Time.realtimeSinceStartupAsDouble - _startRealtime) : 0.0;

        private void SetState(CaptureState state)
        {
            if (State == state) return;
            State = state;
            Debug.Log($"[ReMotion] Capture state -> {state} (session={CurrentSessionId})");
            OnStateChanged?.Invoke(state);
        }

        /// <summary>
        /// Begins one capture attempt. Duplicate starts during
        /// RequestingPermission/Starting/Recording/Finalizing are
        /// rejected. IsRecording stays false until the first frame.
        /// </summary>
        public void StartRecording()
        {
            if (State == CaptureState.RequestingPermission || State == CaptureState.Starting
                || State == CaptureState.Recording || State == CaptureState.Finalizing)
            {
                Debug.Log($"[ReMotion] StartRecording rejected: already {State}.");
                return;
            }
            LastError = null;
            LastSaveSucceeded = false;
            _attempt++;
            CurrentSessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            Debug.Log($"[ReMotion] Capture attempt #{_attempt} session={CurrentSessionId}");
            try
            {
                SetState(CaptureState.RequestingPermission);
                QuestCameraCapture.RequestCameraPermission();
                if (QuestCameraCapture.HasCameraPermission())
                {
                    BeginCameraCapture();
                }
                else
                {
                    _startupWatchdog = StartCoroutine(PermissionWatchdog());
                }
            }
            catch (Exception e)
            {
                FailStartup("Camera startup failed: " + e.Message);
            }
        }

        private IEnumerator PermissionWatchdog()
        {
            float deadline = Time.realtimeSinceStartup + PermissionTimeoutSeconds;
            while (State == CaptureState.RequestingPermission)
            {
                if (QuestCameraCapture.HasCameraPermission())
                {
                    BeginCameraCapture();
                    yield break;
                }
                if (Time.realtimeSinceStartup > deadline)
                {
                    FailStartup("Camera permission was not granted. " +
                                "Allow headset camera access and try Record again.");
                    yield break;
                }
                yield return null;
            }
        }

        private void BeginCameraCapture()
        {
            int attempt = _attempt;
            try
            {
                SetState(CaptureState.Starting);
                string dir = SessionFolder(CurrentSessionId);
                Directory.CreateDirectory(dir);
                _timeline = new SessionTimeline { action_sequence = "recorded_action" };
                _startRealtime = Time.realtimeSinceStartupAsDouble;
                var capture = EnsureCameraCapture();
                capture.OnFirstFrame -= NotifyCameraReady;
                capture.OnFirstFrame += NotifyCameraReady;
                Debug.Log($"[ReMotion] Camera begin: attempt={attempt} instance={capture.GetInstanceID()}");
                if (!capture.BeginCapture(dir))
                {
                    FailStartup(capture.LastError ?? "Camera/encoder failed to start.");
                    return;
                }
                if (_attempt != attempt) return; // superseded mid-start
                if (capture.FramesCaptured > 0) { NotifyCameraReady(); return; }
                _startupWatchdog = StartCoroutine(FirstFrameWatchdog(attempt));
            }
            catch (Exception e)
            {
                FailStartup("Camera startup failed: " + e.Message);
            }
        }

        private IEnumerator FirstFrameWatchdog(int attempt)
        {
            float deadline = Time.realtimeSinceStartup + FirstFrameTimeoutSeconds;
            while (State == CaptureState.Starting && _attempt == attempt)
            {
                if (cameraCapture != null && cameraCapture.FramesCaptured > 0)
                {
                    NotifyCameraReady();
                    yield break;
                }
                if (Time.realtimeSinceStartup > deadline)
                {
                    FailStartup("Camera started but no frames arrived " +
                                "(passthrough unavailable or permission denied).");
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>First valid frame arrived: capture is really running.</summary>
        public void NotifyCameraReady()
        {
            if (State != CaptureState.Starting) return;
            if (_startupWatchdog != null) StopCoroutine(_startupWatchdog);
            IsRecording = true;
            SetState(CaptureState.Recording);
            _loop = StartCoroutine(RecordLoop());
            Debug.Log($"[ReMotion] Recording started: {CurrentSessionId}");
        }

        private void FailStartup(string reason)
        {
            if (_startupWatchdog != null) StopCoroutine(_startupWatchdog);
            IsRecording = false;
            LastError = reason;
            if (cameraCapture != null && cameraCapture.IsCapturing) cameraCapture.EndCapture();
            SetState(CaptureState.Failed);
            Debug.LogWarning("[ReMotion] Capture failed to start: " + reason);
        }

        /// <summary>
        /// Stops capture once and finalizes. Success requires a playable
        /// MP4 on disk; only then are metadata committed as Processing
        /// and the upload queued. Failure writes Failed metadata, keeps
        /// diagnostic frames, and never uploads.
        /// </summary>
        /// <summary>
        /// Asynchronous stop (production path): sampling stops now,
        /// codec/muxer finalization completes off the UI path, and the
        /// Saved/Failed result arrives through OnStateChanged. The UI
        /// stays responsive in Finalizing; nothing is reported Saved
        /// before the encoder has actually committed output.
        /// </summary>
        public void StopAndSaveAsync()
        {
            if (State == CaptureState.Finalizing || !IsRecording) return;
            if (!Application.isPlaying) { StopAndSave(); return; } // EditMode/tests: no frame pump
            int attempt = _attempt;
            IsRecording = false;
            SetState(CaptureState.Finalizing);
            if (_loop != null) StopCoroutine(_loop);
            if (cameraCapture != null) cameraCapture.OnFirstFrame -= NotifyCameraReady;
            _timeline.duration_seconds = (float)RecordingElapsedSeconds;
            StartCoroutine(FinalizeRoutine(attempt));
        }

        private IEnumerator FinalizeRoutine(int attempt)
        {
            string videoPath = null;
            bool done = false;
            try
            {
                if (cameraCapture != null)
                    cameraCapture.FinishCaptureAsync(p => { videoPath = p; done = true; });
                else done = true;
            }
            catch (Exception e)
            {
                LastError = "Encoder finalize failed: " + e.Message;
                done = true;
            }
            while (!done) yield return null;
            if (_attempt != attempt || State != CaptureState.Finalizing) yield break;
            try { CommitFinalizedVideo(videoPath); }
            catch (Exception e)
            {
                LastError = "Save failed: " + e.Message;
                LastSaveSucceeded = false;
                SetState(CaptureState.Failed);
            }
        }

        public string StopAndSave()
        {
            if (State == CaptureState.Finalizing) return CurrentSessionId;
            if (!IsRecording)
            {
                Debug.Log($"[ReMotion] StopAndSave rejected: state={State}.");
                return CurrentSessionId;
            }
            IsRecording = false;
            SetState(CaptureState.Finalizing);
            if (_loop != null) StopCoroutine(_loop);
            if (cameraCapture != null) cameraCapture.OnFirstFrame -= NotifyCameraReady;
            _timeline.duration_seconds = (float)(Time.realtimeSinceStartupAsDouble - _startRealtime);

            string dir = SessionFolder(CurrentSessionId);
            Directory.CreateDirectory(dir);
            string videoPath = null;
            try { videoPath = cameraCapture != null ? cameraCapture.EndCapture() : null; }
            catch (Exception e) { LastError = "Encoder finalize failed: " + e.Message; }
            CommitFinalizedVideo(videoPath);
            return CurrentSessionId;
        }

        private string CommitFinalizedVideo(string videoPath)
        {
            string dir = SessionFolder(CurrentSessionId);
            Directory.CreateDirectory(dir);
            // Playable = encoder committed output AND a nonempty file.
            bool playable = videoPath != null && File.Exists(videoPath)
                            && new FileInfo(videoPath).Length > 0
                            && (cameraCapture == null || cameraCapture.HasPlayableVideo);
            if (!playable)
            {
                string reason = cameraCapture != null && cameraCapture.FramesCaptured > 0
                    ? "Camera frames were saved under video_frames/, but no playable MP4 was produced."
                    : "No camera frames captured (permission denied or no passthrough camera in this run).";
                File.WriteAllText(Path.Combine(dir, "original_video.txt"), reason);
                LastError = reason;
            }
            File.WriteAllText(Path.Combine(dir, "timeline.json"), JsonUtility.ToJson(_timeline, true));
            string title = string.IsNullOrWhiteSpace(captureTitle)
                ? $"Capture · {DateTime.Now:MMM d, HH:mm}" : captureTitle.Trim();
            var meta = new SessionMetadata
            {
                sessionId = CurrentSessionId,
                displayName = title,
                actionSequence = _timeline.action_sequence,
                durationSeconds = _timeline.duration_seconds,
                status = playable ? "Processing" : "Failed",
            };
            File.WriteAllText(Path.Combine(dir, "metadata.json"), JsonUtility.ToJson(meta, true));
            LastSaveSucceeded = playable;
            SetState(playable ? CaptureState.Saved : CaptureState.Failed);
            Debug.Log($"[ReMotion] Capture finalized: state={State} session={CurrentSessionId} dir={dir}");

            if (playable && autoProcessor != null) autoProcessor.UploadSession(CurrentSessionId);
            return CurrentSessionId;
        }

        public static string SessionFolder(string sessionId) =>
            Path.Combine(Application.persistentDataPath, "Library", sessionId);

        private IEnumerator RecordLoop()
        {
            float interval = handRecorder != null ? handRecorder.sampleInterval : 0.1f;
            while (IsRecording)
            {
                if (_timeline.camera_calibration == null && cameraCapture != null
                    && cameraCapture.TryGetCalibration(out var calibration))
                    _timeline.camera_calibration = calibration;
                float t = (float)(Time.realtimeSinceStartupAsDouble - _startRealtime);
                if (handRecorder != null)
                {
                    var frame = handRecorder.SampleNow(t, headTransform);
                    _timeline.frames.Add(frame);
                    // Pinch is a UI click, not evidence of a physical
                    // grab: no grab events are authored here. Grabs are
                    // recorded only via RecordGrabEvent (explicit action
                    // or contact evidence).
                }
                yield return new WaitForSeconds(interval);
            }
        }

        /// <summary>Records a grab/release only from an explicit action or
        /// subject/contact evidence — never inferred from a UI pinch.</summary>
        public void RecordGrabEvent(string hand, string objectId, string type)
        {
            if (_timeline == null || string.IsNullOrEmpty(objectId)) return;
            _timeline.grab_events.Add(new GrabEvent
            {
                timestamp = (float)(Time.realtimeSinceStartupAsDouble - _startRealtime),
                hand = hand, objectId = objectId, type = type,
            });
        }
    }
}
