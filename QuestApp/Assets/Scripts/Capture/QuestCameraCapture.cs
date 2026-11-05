using System;
using System.Collections;
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace ReMotionXR.Capture
{
    /// <summary>
    /// Real Quest camera capture for a recording session, via Meta's
    /// Passthrough Camera API (MRUK PassthroughCameraAccess, left camera):
    /// while recording, frames are sampled at SampleFps, downscaled to
    /// 640x360, and encoded to original_video.mp4 in the session folder
    /// (Android MediaCodec; a JPG frame sequence is the Editor fallback).
    /// Requires, on device: passthrough + camera access enabled in
    /// OculusProjectConfig, HEADSET_CAMERA permission in the manifest,
    /// and the user granting that permission. Meta types via reflection:
    /// no hard SDK reference, Editor runs without the SDK.
    /// </summary>
    public class QuestCameraCapture : MonoBehaviour
    {
        public const string CameraPermissionName = "horizonos.permission.HEADSET_CAMERA";

        public int encoderWidth = 480;
        public int encoderHeight = 270;
        public int sampleFps = 8;

        /// <summary>Hard caps keep camera work from stealing frames from hand/UI interaction.</summary>
        public int EffectiveWidth => Mathf.Clamp(encoderWidth, 160, 480);
        public int EffectiveHeight => Mathf.Clamp(encoderHeight, 90, 270);
        public int EffectiveFps => Mathf.Clamp(sampleFps, 1, 8);
        public bool HasPendingReadback => _readbackPending;

        /// <summary>Test seams: inject a frame source / encoder.</summary>
        public Texture TextureOverride;
        public IVideoEncoder EncoderOverride;

        public bool IsCapturing { get; private set; }
        public int FramesCaptured { get; private set; }

        /// <summary>Fired once, when the first valid frame is captured.</summary>
        public event System.Action OnFirstFrame;

        private void NoteFrameCaptured()
        {
            FramesCaptured++;
            if (FramesCaptured == 1) OnFirstFrame?.Invoke();
        }
        public string LastError { get; private set; }
        public bool HasPlayableVideo { get; private set; }

        private IVideoEncoder _encoder;
        private Coroutine _loop;
        private Component _cameraAccess;
        private Texture2D _readTexture;
        private RenderTexture _blitTarget;
        private string _sessionFolder;
        private int _captureGeneration;
        private bool _readbackPending;
        private AsyncGPUReadbackRequest? _pendingRequest;
        private int _pendingGeneration;
        private byte[] _managedFrame;
        private double _captureStartRealtime;
        private long CapturePtsUs() => (long)((Time.realtimeSinceStartupAsDouble - _captureStartRealtime) * 1_000_000.0);

        /// <summary>Read the actual lens and crop once the camera is running.</summary>
        public bool TryGetCalibration(out ReMotionXR.Core.CameraCalibration calibration)
        {
            calibration = null;
            if (TextureOverride != null || _cameraAccess == null) return false;
            try
            {
                var type = _cameraAccess.GetType();
                if (!(type.GetProperty("IsPlaying")?.GetValue(_cameraAccess) is bool playing) || !playing) return false;
                object intrinsics = type.GetProperty("Intrinsics")?.GetValue(_cameraAccess);
                if (intrinsics == null) return false;
                var fields = intrinsics.GetType();
                calibration = EncodeCalibration(
                    (Vector2)fields.GetField("FocalLength").GetValue(intrinsics),
                    (Vector2)fields.GetField("PrincipalPoint").GetValue(intrinsics),
                    (Vector2Int)fields.GetField("SensorResolution").GetValue(intrinsics),
                    (Vector2Int)type.GetProperty("CurrentResolution").GetValue(_cameraAccess),
                    (Pose)fields.GetField("LensOffset").GetValue(intrinsics),
                    EffectiveWidth, EffectiveHeight);
                return calibration != null;
            }
            catch (Exception) { return false; } // Missing SDK data stays explicitly unavailable.
        }

        public static ReMotionXR.Core.CameraCalibration EncodeCalibration(Vector2 focal,
            Vector2 principal, Vector2Int sensor, Vector2Int stream, Pose lens, int width, int height)
        {
            if (sensor.x <= 0 || sensor.y <= 0 || stream.x <= 0 || stream.y <= 0
                || focal.x <= 0 || focal.y <= 0 || width <= 0 || height <= 0) return null;
            // Same centered sensor crop as MRUK, then Graphics.Blit's encoded resize.
            var factor = new Vector2((float)stream.x / sensor.x, (float)stream.y / sensor.y);
            factor /= Mathf.Max(factor.x, factor.y);
            float cropW = sensor.x * factor.x, cropH = sensor.y * factor.y;
            float cropX = (sensor.x - cropW) * .5f, cropY = (sensor.y - cropH) * .5f;
            return new ReMotionXR.Core.CameraCalibration
            {
                encoded_width = width, encoded_height = height,
                fx = focal.x * width / cropW, fy = focal.y * height / cropH,
                cx = (principal.x - cropX) * width / cropW,
                cy = height - (principal.y - cropY) * height / cropH,
                lens_position = lens.position, lens_rotation = lens.rotation,
                metric_depth_recorded = false
            };
        }

        /// <summary>Asks the user for the headset-camera permission (device only).</summary>
        public static void RequestCameraPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(CameraPermissionName))
                Permission.RequestUserPermission(CameraPermissionName);
#endif
        }

        public static bool HasCameraPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(CameraPermissionName);
#elif UNITY_EDITOR
            return true; // no headset permission system in the Editor
#else
            return false;
#endif
        }

        /// <summary>
        /// Starts the passthrough camera early (when the Capture screen
        /// opens) so pressing Record does not wait on camera startup.
        /// The camera only runs while the app is in Capture flow; nothing
        /// is recorded until BeginCapture.
        /// </summary>
        public void PreWarm()
        {
            RequestCameraPermission();
            EnsureCameraAccess();
        }

        /// <summary>Starts sampling the passthrough camera into sessionFolder.</summary>
        public bool BeginCapture(string sessionFolder)
        {
            if (IsCapturing) return true;
            _sessionFolder = sessionFolder;
            Directory.CreateDirectory(sessionFolder);
            FramesCaptured = 0;
            HasPlayableVideo = false;
            LastError = null;
            _captureGeneration++;
            _readbackPending = false;
            _pendingRequest = null;

            RequestCameraPermission();
            EnsureCameraAccess();

            _encoder = EncoderOverride ?? (AndroidMp4VideoEncoder.IsSupported
                ? (IVideoEncoder)new AndroidMp4VideoEncoder()
                : new FrameSequenceVideoEncoder());
            if (!_encoder.Start(sessionFolder, EffectiveWidth, EffectiveHeight, EffectiveFps))
            {
                // MP4 unavailable (e.g. encoder busy): keep real frames instead.
                _encoder = new FrameSequenceVideoEncoder();
                _encoder.Start(sessionFolder, EffectiveWidth, EffectiveHeight, EffectiveFps);
                LastError = "MP4 encoder unavailable; saving frame sequence.";
            }
            _captureStartRealtime = Time.realtimeSinceStartupAsDouble;
            IsCapturing = true;
            _loop = StartCoroutine(SampleLoop());
            Debug.Log($"[ReMotion] Camera capture started ({_encoder.GetType().Name}) -> {sessionFolder}");
            return true;
        }

        /// <summary>
        /// Stops sampling now (main thread) and finalizes the encoder
        /// off the UI path; completion arrives through the callback on
        /// a later frame poll by the caller. Pending readbacks are
        /// discarded by generation, never awaited on the UI thread.
        /// </summary>
        public void FinishCaptureAsync(System.Action<string> onComplete)
        {
            if (!IsCapturing) { onComplete?.Invoke(null); return; }
            IsCapturing = false;
            _captureGeneration++;
            _readbackPending = false;
            _pendingRequest = null;
            if (_loop != null) StopCoroutine(_loop);
            var encoder = _encoder;
            if (encoder == null) { onComplete?.Invoke(null); return; }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string path = null;
                try
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    if (AndroidJNI.AttachCurrentThread() != 0)
                        throw new System.InvalidOperationException("Cannot attach encoder finalizer to Android VM.");
                    try { path = encoder.Finish(); }
                    finally { AndroidJNI.DetachCurrentThread(); }
#else
                    path = encoder.Finish();
#endif
                }
                catch (System.Exception e)
                {
                    LastError = "Encoder finalize failed: " + e.Message;
                }
                HasPlayableVideo = path != null;
                if (!HasPlayableVideo)
                    LastError ??= "No playable video produced (camera frames: " + FramesCaptured + ").";
                Debug.Log($"[ReMotion] Camera finalize complete: {FramesCaptured} frames, video: {path ?? "none"}");
                onComplete?.Invoke(path);
            });
        }

        /// <summary>Stops sampling and finalizes the video. Returns the mp4 path or null.</summary>
        public string EndCapture()
        {
            if (!IsCapturing) return null;
            IsCapturing = false;
            _captureGeneration++;
            _readbackPending = false;
            _pendingRequest = null;
            if (_loop != null) StopCoroutine(_loop);
            string path = _encoder != null ? _encoder.Finish() : null;
            HasPlayableVideo = path != null;
            if (!HasPlayableVideo)
                LastError ??= "No playable video produced (camera frames: " + FramesCaptured + ").";
            Debug.Log($"[ReMotion] Camera capture ended: {FramesCaptured} frames, video: {path ?? "none"}");
            return path;
        }

        /// <summary>
        /// Queues one frame; false when no camera texture is ready or the
        /// previous asynchronous readback is still in flight. Queuing a frame
        /// intentionally does not increment FramesCaptured until its GPU
        /// readback completes, so CaptureFrameNow never stalls rendering.
        /// </summary>
        public bool CaptureFrameNow()
        {
            if (_readbackPending) return false;
            Texture source = TextureOverride ?? GetCameraTexture();
            if (source == null || _encoder == null) return false;
            try
            {
                int width = EffectiveWidth;
                int height = EffectiveHeight;
                if (_blitTarget == null || _blitTarget.width != width || _blitTarget.height != height)
                {
                    if (_blitTarget != null) RenderTexture.ReleaseTemporary(_blitTarget);
                    _blitTarget = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                }
                Graphics.Blit(source, _blitTarget);

                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    int generation = _captureGeneration;
                    _readbackPending = true;
                    _pendingGeneration = generation;
                    _pendingRequest = AsyncGPUReadback.Request(
                        _blitTarget, 0, TextureFormat.RGBA32,
                        request => OnReadbackCompleted(request, generation));
                    return true;
                }

                // Rare fallback for devices without async readback. The frame
                // is much smaller than before, and this path still lets Stop
                // respond immediately between samples.
                if (_readTexture == null || _readTexture.width != width || _readTexture.height != height)
                {
                    if (_readTexture != null) ReleaseTexture(_readTexture);
                    _readTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                }
                var previous = RenderTexture.active;
                RenderTexture.active = _blitTarget;
                _readTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                _readTexture.Apply(false, false);
                RenderTexture.active = previous;
                _encoder.AddFrame(_readTexture.GetRawTextureData(), width, height, CapturePtsUs());
                NoteFrameCaptured();
                return true;
            }
            catch (Exception e)
            {
                _readbackPending = false;
                LastError = "Frame capture failed: " + e.Message;
                return false;
            }
        }

        private void OnReadbackCompleted(AsyncGPUReadbackRequest request, int generation)
        {
            if (generation != _pendingGeneration || !_readbackPending) return;
            _readbackPending = false;
            _pendingRequest = null;
            if (generation != _captureGeneration || !IsCapturing || _encoder == null) return;
            if (request.hasError)
            {
                LastError = "Camera GPU readback failed.";
                return;
            }

            // Reused managed buffer: no per-frame allocation on the render
            // path (allocation churn is a classic VR judder source).
            int byteCount = EffectiveWidth * EffectiveHeight * 4;
            if (_managedFrame == null || _managedFrame.Length != byteCount)
                _managedFrame = new byte[byteCount];
            NativeArray<byte> data = request.GetData<byte>();
            data.CopyTo(_managedFrame);
            SavePoster(_managedFrame, EffectiveWidth, EffectiveHeight);
            _encoder.AddFrame(_managedFrame, EffectiveWidth, EffectiveHeight, CapturePtsUs());
            NoteFrameCaptured();
        }

        private void SavePoster(byte[] rgba, int width, int height)
        {
            if (FramesCaptured != 0 || string.IsNullOrEmpty(_sessionFolder)) return;
            Texture2D poster = null;
            try
            {
                poster = new Texture2D(width, height, TextureFormat.RGBA32, false);
                poster.LoadRawTextureData(rgba);
                poster.Apply();
                File.WriteAllBytes(Path.Combine(_sessionFolder, "thumbnail.jpg"), poster.EncodeToJPG(80));
            }
            catch (Exception e) { Debug.LogWarning("[ReMotion] Poster save failed: " + e.Message); }
            finally { if (poster != null) ReleaseTexture(poster); }
        }

        /// <summary>Blocks only in tests/tools until the queued readback settles.</summary>
        public void FlushPendingFrame()
        {
            if (!_pendingRequest.HasValue) return;
            AsyncGPUReadbackRequest request = _pendingRequest.Value;
            request.WaitForCompletion();
            if (_readbackPending) OnReadbackCompleted(request, _pendingGeneration);
        }

        private IEnumerator SampleLoop()
        {
            var wait = new WaitForSecondsRealtime(1f / EffectiveFps);
            while (IsCapturing)
            {
                CaptureFrameNow();
                yield return wait;
            }
        }

        /// <summary>Creates/finds PassthroughCameraAccess (Left camera) by reflection.</summary>
        public Component EnsureCameraAccess()
        {
            if (_cameraAccess != null) return _cameraAccess;
            var type = FindMetaType("PassthroughCameraAccess");
            if (type == null)
            {
                LastError = "PassthroughCameraAccess (MRUK) not available in this build.";
                return null;
            }
            _cameraAccess = GetComponent(type) as Component;
            if (_cameraAccess == null) _cameraAccess = gameObject.AddComponent(type) as Component;
            // Configure before play: left camera, modest resolution.
            var positionField = type.GetField("CameraPosition");
            var positionType = type.GetNestedType("CameraPositionType");
            if (positionField != null && positionType != null)
                positionField.SetValue(_cameraAccess, Enum.Parse(positionType, "Left"));
            type.GetField("RequestedResolution")?.SetValue(_cameraAccess, new Vector2Int(640, 480));
            if (_cameraAccess is Behaviour behaviour) behaviour.enabled = true;
            return _cameraAccess;
        }

        /// <summary>The live camera texture, or null until permission + frames flow.</summary>
        public Texture GetCameraTexture()
        {
            var access = EnsureCameraAccess();
            if (access == null) return null;
            var isPlaying = access.GetType().GetProperty("IsPlaying")?.GetValue(access) as bool?;
            if (isPlaying != true) return null;
            return access.GetType().GetMethod("GetTexture")?.Invoke(access, null) as Texture;
        }

        private static Type FindMetaType(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (var t in asm.GetTypes())
                    if (t.Name == simpleName) return t;
            return null;
        }

        private void OnDestroy()
        {
            _captureGeneration++;
            _readbackPending = false;
            _pendingRequest = null;
            if (_blitTarget != null) RenderTexture.ReleaseTemporary(_blitTarget);
            if (_readTexture != null) ReleaseTexture(_readTexture);
        }

        private static void ReleaseTexture(Texture2D texture)
        {
            if (Application.isPlaying) Destroy(texture); else DestroyImmediate(texture);
        }
    }
}
