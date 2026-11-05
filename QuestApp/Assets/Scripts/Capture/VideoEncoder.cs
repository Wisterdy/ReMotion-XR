using System.IO;
using UnityEngine;

namespace ReMotionXR.Capture
{
    /// <summary>Writes sampled camera frames into sessionFolder/original_video.*</summary>
    public interface IVideoEncoder
    {
        bool Start(string sessionFolder, int width, int height, int fps);
        void AddFrame(Texture2D frame);
        /// <summary>Adds one RGBA32 frame without requiring a Texture2D readback.</summary>
        void AddFrame(byte[] rgba, int width, int height);
        /// <summary>Adds one frame with its capture-acquisition PTS (microseconds).</summary>
        void AddFrame(byte[] rgba, int width, int height, long ptsUs);
        /// <summary>Finalizes; returns the playable video path, or null.</summary>
        string Finish();
    }

    /// <summary>
    /// Device encoder: Android MediaCodec (H.264) + MediaMuxer through the
    /// bundled Java plugin, producing a real original_video.mp4 that the
    /// Library's Watch Video can play. Only supported in an Android build.
    /// </summary>
    public class AndroidMp4VideoEncoder : IVideoEncoder
    {
        private string _outputPath;

        public static bool IsSupported =>
#if UNITY_ANDROID && !UNITY_EDITOR
            true;
#else
            false;
#endif

        public bool Start(string sessionFolder, int width, int height, int fps)
        {
            _outputPath = Path.Combine(sessionFolder, "original_video.mp4");
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                _encoderClass ??= new AndroidJavaClass("com.remotionxr.encoder.RemotionMp4Encoder");
                return _encoderClass.CallStatic<bool>("start", _outputPath, width, height, fps);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[ReMotion] MP4 encoder failed to start: " + e.Message);
                return false;
            }
#else
            return false;
#endif
        }

        public void AddFrame(Texture2D frame)
        {
            if (frame != null) AddFrame(frame.GetRawTextureData(), frame.width, frame.height);
        }

        public void AddFrame(byte[] rgba, int width, int height) =>
            AddFrame(rgba, width, height, 0L);

        public void AddFrame(byte[] rgba, int width, int height, long ptsUs)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (_encoderClass != null && rgba != null)
                    _encoderClass.CallStatic("addFrame", rgba, width, height, ptsUs);
            }
            catch (System.Exception e) { Debug.LogWarning("[ReMotion] MP4 frame failed: " + e.Message); }
#endif
        }

        public string Finish()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (_encoderClass == null || !_encoderClass.CallStatic<bool>("finish")) return null;
            }
            catch (System.Exception e) { Debug.LogWarning("[ReMotion] MP4 finish failed: " + e.Message); return null; }
            finally
            {
                _encoderClass?.Dispose();
                _encoderClass = null;
            }
#endif
            return _outputPath != null && File.Exists(_outputPath) &&
                   new FileInfo(_outputPath).Length > 0 ? _outputPath : null;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaClass _encoderClass;
#endif
    }

    /// <summary>
    /// Fallback when no MP4 encoder can run (Editor, or camera permission
    /// denied): stores real sampled frames as JPGs under video_frames/ so a
    /// capture is never silently empty, and notes why there is no .mp4.
    /// </summary>
    public class FrameSequenceVideoEncoder : IVideoEncoder
    {
        private string _framesDir;
        private int _count;

        public bool Start(string sessionFolder, int width, int height, int fps)
        {
            _framesDir = Path.Combine(sessionFolder, "video_frames");
            Directory.CreateDirectory(_framesDir);
            _count = 0;
            return true;
        }

        public void AddFrame(Texture2D frame)
        {
            if (frame != null) AddFrame(frame.GetRawTextureData(), frame.width, frame.height);
        }

        public void AddFrame(byte[] rgba, int width, int height)
        {
            if (_framesDir == null || rgba == null) return;
            if (_frameTexture == null || _frameTexture.width != width || _frameTexture.height != height)
            {
                if (_frameTexture != null) Object.Destroy(_frameTexture);
                _frameTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }
            _frameTexture.LoadRawTextureData(rgba);
            _frameTexture.Apply(false, false);
            File.WriteAllBytes(Path.Combine(_framesDir, $"frame_{_count:D4}.jpg"),
                               _frameTexture.EncodeToJPG(75));
            _count++;
        }

        public void AddFrame(byte[] rgba, int width, int height, long ptsUs) =>
            AddFrame(rgba, width, height); // frame sequence keeps arrival order

        private Texture2D _frameTexture;

        public string Finish()
        {
            // No playable mp4 from this encoder; frames remain available.
            return null;
        }
    }
}
