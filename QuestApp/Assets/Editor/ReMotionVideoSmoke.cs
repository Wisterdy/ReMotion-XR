#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using ReMotionXR.Library;

namespace ReMotionXR.Editor
{
    /// <summary>
    /// Play-mode smoke test for Watch Video: builds the real panel, plays
    /// a real MP4 through VideoPlayer -> RenderTexture -> RawImage, then
    /// samples the texture. Logs VIDEO SMOKE PASS/FAIL with the average
    /// pixel colour (black = the headset's "playing but see nothing").
    /// Batch: -executeMethod ReMotionXR.Editor.ReMotionVideoSmoke.RunBatch
    /// Expects the folder in REMOTION_VIDEO_TEST_DIR (default system temp).
    /// </summary>
    public static class ReMotionVideoSmoke
    {
        private static VideoPlaybackController _controller;
        private static double _startedAt;
        private static bool _entered;

        public static void RunBatch()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.EnterPlaymode();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || _entered) return;
            _entered = true;
            string folder = System.Environment.GetEnvironmentVariable("REMOTION_VIDEO_TEST_DIR");
            if (string.IsNullOrEmpty(folder))
                folder = Path.Combine(Path.GetTempPath(), "remotion_video_test");
            var go = new GameObject("VideoSmoke");
            _controller = go.AddComponent<VideoPlaybackController>();
            _controller.EnsureBuilt(go.transform);
            _controller.Show("smoke", folder, "Smoke Test");
            _startedAt = EditorApplication.timeSinceStartup;
            EditorApplication.update += Tick;
            Debug.Log("[ReMotion] VIDEO SMOKE started folder=" + folder);
        }

        private static void Tick()
        {
            if (_controller == null) return;
            double elapsed = EditorApplication.timeSinceStartup - _startedAt;
            var player = _controller.videoPlayer;
            bool prepared = player != null && player.isPrepared;
            long frame = player != null ? player.frame : -1;
            if ((prepared && frame > 10 && elapsed > 1.0) || elapsed > 12.0)
            {
                EditorApplication.update -= Tick;
                var texture = _controller.videoImage != null ? _controller.videoImage.texture : null;
                float average = -1f;
                if (texture is RenderTexture rt)
                {
                    var previous = RenderTexture.active;
                    var rtSmall = RenderTexture.GetTemporary(8, 8);
                    Graphics.Blit(rt, rtSmall);
                    RenderTexture.active = rtSmall;
                    var small = new Texture2D(8, 8, TextureFormat.RGB24, false);
                    small.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                    small.Apply();
                    float sum = 0f;
                    foreach (var pixel in small.GetPixels()) sum += (pixel.r + pixel.g + pixel.b) / 3f;
                    average = sum / small.GetPixels().Length;
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(rtSmall);
                    Object.DestroyImmediate(small);
                }
                bool pass = average > 0.03f;
                Debug.Log($"[ReMotion] VIDEO SMOKE {(pass ? "PASS" : "FAIL")} " +
                          $"prepared={prepared} frame={frame} avgPixel={average:F3} state={_controller.State}");
                EditorApplication.ExitPlaymode();
                EditorApplication.delayCall += () => EditorApplication.Exit(pass ? 0 : 1);
            }
        }
    }
}
#endif
