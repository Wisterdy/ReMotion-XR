using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

namespace ReMotionXR.Library
{
    /// <summary>
    /// Plays a capture's original video (Library/{id}/original_video.mp4)
    /// on a panel inside the app, so "Watch Video" is a real choice next to
    /// "Enter Virtual Scene" instead of a log line. Captures that only have
    /// the placeholder .txt (camera capture not plugged in yet, and the
    /// offline demo) open the same panel in Placeholder state explaining
    /// there is no camera footage and pointing at the virtual scene.
    /// </summary>
    public class VideoPlaybackController : MonoBehaviour
    {
        public enum PlaybackState { None, Playing, Placeholder, Missing }

        public GameObject panel;
        public Text titleText;
        public Text statusText;
        public RawImage videoImage;
        public Button playPauseButton;
        public Button closeButton;
        public VideoPlayer videoPlayer;

        public PlaybackState State { get; private set; } = PlaybackState.None;
        public bool IsOpen => panel != null && panel.activeSelf;
        public string CurrentSessionId { get; private set; }
        public event Action OnClosed;

        private RenderTexture _target;
        private bool _wired;

        /// <summary>Builds the panel under parent if it was never assigned (idempotent).</summary>
        public void EnsureBuilt(Transform parent)
        {
            if (panel != null) { Wire(); return; }
            var panelGo = ReMotionUiTheme.MakePanel(parent, "VideoPlayerPanel", ReMotionUiTheme.Background);
            panel = panelGo.gameObject;
            ReMotionUiTheme.SetSize(panel.transform, new Vector2(880f, 560f));
            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true; layout.childControlHeight = false;
            layout.spacing = 10f; layout.padding = new RectOffset(20, 20, 16, 16);

            titleText = ReMotionUiTheme.Label(panel.transform, "VideoTitle", "Video", 22, ReMotionUiTheme.TextPrimary);
            ReMotionUiTheme.SetSize(titleText.transform, new Vector2(820f, 32f));

            var imgGo = new GameObject("VideoImage");
            imgGo.transform.SetParent(panel.transform, false);
            videoImage = imgGo.AddComponent<RawImage>();
            // RawImage.color TINTS the texture: black multiplied every
            // decoded frame to black ("playing=True but see nothing").
            videoImage.color = Color.white;
            videoImage.enabled = false; // until the first frame is prepared
            ReMotionUiTheme.SetSize(imgGo.transform, new Vector2(820f, 360f));

            statusText = ReMotionUiTheme.Label(panel.transform, "VideoStatus", "", 15, ReMotionUiTheme.TextSecondary);
            ReMotionUiTheme.SetSize(statusText.transform, new Vector2(820f, 52f));

            var rowGo = new GameObject("VideoActions");
            rowGo.transform.SetParent(panel.transform, false);
            var row = rowGo.AddComponent<HorizontalLayoutGroup>();
            row.childControlWidth = false; row.childControlHeight = false; row.spacing = 12f;
            playPauseButton = ReMotionUiTheme.Button(rowGo.transform, "VideoPlayPauseButton", "Pause",
                ReMotionUiTheme.ButtonStyle.Primary, 15);
            ReMotionUiTheme.SetSize(playPauseButton.transform, new Vector2(160f, 46f));
            closeButton = ReMotionUiTheme.Button(rowGo.transform, "VideoCloseButton", "Back to Library",
                ReMotionUiTheme.ButtonStyle.Secondary, 15);
            ReMotionUiTheme.SetSize(closeButton.transform, new Vector2(220f, 46f));

            videoPlayer = panel.AddComponent<VideoPlayer>();
            videoPlayer.playOnAwake = false;
            videoPlayer.renderMode = VideoRenderMode.RenderTexture;
            videoPlayer.audioOutputMode = VideoAudioOutputMode.None;
            _target = new RenderTexture(640, 360, 0) { name = "ReMotionVideoTarget" };
            videoPlayer.targetTexture = _target;
            videoImage.texture = _target;
            panel.SetActive(false);
            Wire();
        }

        private void Wire()
        {
            if (_wired) return; _wired = true;
            if (playPauseButton != null) playPauseButton.onClick.AddListener(TogglePlayPause);
            if (closeButton != null) closeButton.onClick.AddListener(Close);
            if (videoPlayer != null)
            {
                videoPlayer.prepareCompleted += OnVideoPrepared;
                videoPlayer.errorReceived += OnVideoError;
            }
        }

        private void OnVideoPrepared(VideoPlayer player)
        {
            if (videoImage != null && _target != null)
            {
                videoImage.texture = _target;
                videoImage.enabled = true;
            }
            // Play() issued before Prepare finishes is dropped on Android
            // (device: "prepared playing=False", black panel). Start now.
            if (State == PlaybackState.Playing && !player.isPlaying) player.Play();
            Debug.Log($"[ReMotion] Video prepared {player.width}x{player.height} playing={player.isPlaying} session={CurrentSessionId}");
        }

        private void OnVideoError(VideoPlayer player, string message)
        {
            State = PlaybackState.Missing;
            if (statusText != null) statusText.text = "Video could not be played: " + message;
            Debug.LogError("[ReMotion] VideoPlayer error: " + message);
        }

        /// <summary>Opens the panel for a session folder and starts the mp4 if one exists.</summary>
        public PlaybackState Show(string sessionId, string sessionFolder, string displayName)
        {
            if (panel == null) EnsureBuilt(transform.parent ?? transform);
            CurrentSessionId = sessionId;
            if (titleText != null) titleText.text = displayName ?? sessionId;
            string mp4 = Path.Combine(sessionFolder ?? "", "original_video.mp4");
            string txt = Path.Combine(sessionFolder ?? "", "original_video.txt");
            panel.transform.SetAsLastSibling(); // in front of cards/backdrop
            panel.SetActive(true);
            if (File.Exists(mp4))
            {
                State = PlaybackState.Playing;
                if (statusText != null) statusText.text = "Original video — the regular recording of this capture.";
                videoPlayer.url = new Uri(mp4).AbsoluteUri;
                if (Application.isPlaying) { videoPlayer.Prepare(); videoPlayer.Play(); }
            }
            else if (File.Exists(txt))
            {
                State = PlaybackState.Placeholder;
                if (statusText != null) statusText.text =
                    "No camera video for this capture yet (placeholder recording). " +
                    "Use Enter Virtual Scene to play it back in 3D.";
                if (videoPlayer != null && Application.isPlaying) videoPlayer.Stop();
            }
            else
            {
                State = PlaybackState.Missing;
                if (statusText != null) statusText.text = "No original video was saved for this capture.";
                if (videoPlayer != null && Application.isPlaying) videoPlayer.Stop();
            }
            return State;
        }

        public void TogglePlayPause()
        {
            if (videoPlayer == null || State != PlaybackState.Playing) return;
            if (videoPlayer.isPlaying) videoPlayer.Pause(); else videoPlayer.Play();
            var label = playPauseButton != null ? playPauseButton.GetComponentInChildren<Text>() : null;
            if (label != null) label.text = videoPlayer.isPlaying ? "Pause" : "Play";
        }

        public void Close()
        {
            if (!IsOpen && State == PlaybackState.None) return;
            if (videoPlayer != null && Application.isPlaying) videoPlayer.Stop();
            State = PlaybackState.None;
            if (panel != null) panel.SetActive(false);
            OnClosed?.Invoke();
        }
    }
}
