using System;
using UnityEngine;
using UnityEngine.UI;
using ReMotionXR.Library;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// In-scene playback HUD shown after Enter Virtual Scene: Play/Pause,
    /// timeline scrub, speed, key-pose jump, God Mode, Retrain, and Back to
    /// Library. Built procedurally so saved scenes get it without a rebuild.
    /// </summary>
    public class ReplayHudController : MonoBehaviour
    {
        public GameObject panel;
        public Button playPauseButton;
        public Slider scrubSlider;
        public Text timeText;
        public Button speedButton;
        public Button prevPoseButton;
        public Button nextPoseButton;
        public Button godModeButton;
        public Button retrainButton;
        public Button backButton;
        public Button controlsButton;
        private bool _drawerOpen;

        public ReplayController Controller { get; private set; }
        public bool IsOpen => panel != null && panel.activeSelf;
        public event Action OnBack;

        private bool _wired, _syncing;
        private int _speedIndex = 2; // 1x

        public void EnsureBuilt(Transform parent, ReplayController controller)
        {
            if (controller != null) Controller = controller;
            if (panel != null) { Wire(); return; }
            var panelGo = ReMotionUiTheme.MakePanel(parent, "ReplayHudPanel", ReMotionUiTheme.Background);
            panel = panelGo.gameObject;
            ReMotionUiTheme.SetSize(panel.transform, new Vector2(880f, 150f));
            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true; layout.childControlHeight = false;
            layout.spacing = 8f; layout.padding = new RectOffset(16, 16, 12, 12);

            var sliderGo = new GameObject("ScrubSlider");
            sliderGo.transform.SetParent(panel.transform, false);
            scrubSlider = sliderGo.AddComponent<Slider>();
            scrubSlider.minValue = 0f; scrubSlider.maxValue = 1f;
            ReMotionUiTheme.SetSize(sliderGo.transform, new Vector2(840f, 28f));
            BuildSliderVisuals(sliderGo);

            timeText = ReMotionUiTheme.Label(panel.transform, "ReplayTime", "0:00 / 0:00", 13,
                ReMotionUiTheme.TextSecondary);
            ReMotionUiTheme.SetSize(timeText.transform, new Vector2(840f, 20f));

            var rowGo = new GameObject("ReplayActions");
            rowGo.transform.SetParent(panel.transform, false);
            var row = rowGo.AddComponent<HorizontalLayoutGroup>();
            row.childControlWidth = false; row.childControlHeight = false; row.spacing = 8f;
            backButton = HudButton(rowGo.transform, "BackButton", "‹ Library");
            prevPoseButton = HudButton(rowGo.transform, "PrevPoseButton", "‹ Pose");
            playPauseButton = HudButton(rowGo.transform, "PlayPauseButton", "Pause", true);
            nextPoseButton = HudButton(rowGo.transform, "NextPoseButton", "Pose ›");
            speedButton = HudButton(rowGo.transform, "SpeedButton", "1x");
            godModeButton = HudButton(rowGo.transform, "GodModeButton", "God Mode");
            retrainButton = HudButton(rowGo.transform, "RetrainButton", "Retrain");
            controlsButton = HudButton(rowGo.transform, "ControlsButton", "Controls");
            panel.SetActive(false);
            Wire();
        }

        private static Button HudButton(Transform parent, string name, string label, bool primary = false)
        {
            var b = ReMotionUiTheme.Button(parent, name, label,
                primary ? ReMotionUiTheme.ButtonStyle.Primary : ReMotionUiTheme.ButtonStyle.Secondary, 13);
            ReMotionUiTheme.SetSize(b.transform, new Vector2(108f, 42f));
            return b;
        }

        private static void BuildSliderVisuals(GameObject sliderGo)
        {
            var bg = new GameObject("Background"); bg.transform.SetParent(sliderGo.transform, false);
            var bgImg = bg.AddComponent<Image>(); bgImg.color = ReMotionUiTheme.PanelAlt;
            Stretch(bg);
            var fillArea = new GameObject("Fill Area"); fillArea.transform.SetParent(sliderGo.transform, false); Stretch(fillArea);
            var fill = new GameObject("Fill"); fill.transform.SetParent(fillArea.transform, false);
            var fillImg = fill.AddComponent<Image>(); fillImg.color = ReMotionUiTheme.Accent; Stretch(fill);
            var handleArea = new GameObject("Handle Slide Area"); handleArea.transform.SetParent(sliderGo.transform, false); Stretch(handleArea);
            var handle = new GameObject("Handle"); handle.transform.SetParent(handleArea.transform, false);
            var handleImg = handle.AddComponent<Image>(); handleImg.color = Color.white;
            var hrt = handle.GetComponent<RectTransform>(); hrt.sizeDelta = new Vector2(22f, 22f);
            var slider = sliderGo.GetComponent<Slider>();
            slider.fillRect = fillImg.rectTransform; slider.handleRect = handleImg.rectTransform;
            slider.targetGraphic = handleImg;
        }

        private static void Stretch(GameObject go)
        {
            // Empty container objects have a plain Transform; UI needs RectTransform.
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private void Wire()
        {
            if (_wired) return; _wired = true;
            if (playPauseButton != null) playPauseButton.onClick.AddListener(TogglePlayPause);
            if (backButton != null) backButton.onClick.AddListener(GoBack);
            if (speedButton != null) speedButton.onClick.AddListener(CycleSpeed);
            if (prevPoseButton != null) prevPoseButton.onClick.AddListener(() => Controller?.Player.PrevKeyPose());
            if (nextPoseButton != null) nextPoseButton.onClick.AddListener(() => Controller?.Player.NextKeyPose());
            if (godModeButton != null) godModeButton.onClick.AddListener(() =>
                Controller?.SetGodMode(!(Controller.godMode != null && Controller.godMode.GodModeActive)));
            if (retrainButton != null) retrainButton.onClick.AddListener(() =>
                Controller?.SetRetrain(!Controller.RetrainActive));
            if (controlsButton != null) controlsButton.onClick.AddListener(ToggleDrawer);
            if (scrubSlider != null) scrubSlider.onValueChanged.AddListener(OnScrub);
        }

        public void Show()
        {
            ApplyMinimalMenu();
            if (panel != null) panel.SetActive(true);
        }

        public void Hide() { if (panel != null) panel.SetActive(false); }

        /// <summary>
        /// In-scene menu is intentionally minimal (user-directed): only
        /// Back to Library and God Mode exist inside the immersive
        /// scene. Playback transport controls stay implemented but hidden.
        /// </summary>
        private void ApplyMinimalMenu()
        {
            // Minimal shell: Back + God Mode + the Controls drawer toggle.
            // The drawer holds the working transport and Retrain.
            _drawerOpen = false;
            ApplyDrawer();
            if (backButton != null) backButton.gameObject.SetActive(true);
            if (godModeButton != null) godModeButton.gameObject.SetActive(true);
            if (controlsButton != null) controlsButton.gameObject.SetActive(true);
        }

        public void ToggleDrawer()
        {
            _drawerOpen = !_drawerOpen;
            ApplyDrawer();
        }

        private void ApplyDrawer()
        {
            if (scrubSlider != null) scrubSlider.gameObject.SetActive(_drawerOpen);
            if (timeText != null) timeText.gameObject.SetActive(_drawerOpen);
            if (prevPoseButton != null) prevPoseButton.gameObject.SetActive(_drawerOpen);
            if (playPauseButton != null) playPauseButton.gameObject.SetActive(_drawerOpen);
            if (nextPoseButton != null) nextPoseButton.gameObject.SetActive(_drawerOpen);
            if (speedButton != null) speedButton.gameObject.SetActive(_drawerOpen);
            if (retrainButton != null) retrainButton.gameObject.SetActive(_drawerOpen);
        }

        public void TogglePlayPause()
        {
            var p = Controller?.Player; if (p == null) return;
            if (p.IsPlaying) p.Pause(); else p.Play();
            SetLabel(playPauseButton, p.IsPlaying ? "Pause" : "Play");
        }

        public void CycleSpeed()
        {
            var p = Controller?.Player; if (p == null) return;
            _speedIndex = (_speedIndex + 1) % 3;
            p.SetSpeed(_speedIndex);
            SetLabel(speedButton, p.PlaybackSpeed.ToString("0.##") + "x");
        }

        private void OnScrub(float v)
        {
            if (_syncing || Controller?.Player == null) return;
            Controller.Player.Scrub(v * Controller.Player.Duration);
        }

        public void GoBack()
        {
            Controller?.ExitSession();
            Hide();
            OnBack?.Invoke();
        }

        private void Update()
        {
            if (!IsOpen || Controller?.Player == null) return;
            var p = Controller.Player;
            _syncing = true;
            if (scrubSlider != null && p.Duration > 0f) scrubSlider.value = p.CurrentTime / p.Duration;
            _syncing = false;
            if (timeText != null) timeText.text = $"{Fmt(p.CurrentTime)} / {Fmt(p.Duration)}";
        }

        private static string Fmt(float s) => $"{(int)s / 60}:{(int)s % 60:00}";
        private static void SetLabel(Button b, string text)
        { var t = b != null ? b.GetComponentInChildren<Text>() : null; if (t != null) t.text = text; }
    }
}
