using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ReMotionXR.Core;

namespace ReMotionXR.Accessibility
{
    /// <summary>
    /// Dwell-to-click for menu buttons: hover with the pinch ray (or gaze)
    /// for AccessibilitySettings.DwellSeconds and the button activates —
    /// the same 1-second dwell standard as the scene ObjectIndicators, so
    /// users who cannot pinch can still operate every menu action. A thin
    /// progress strip under the button shows the dwell filling. Pinch and
    /// controller clicks keep working in parallel.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class DwellClick : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Set while recording: no button may dwell-fire at all.</summary>
        public static bool SuppressAll;

        /// <summary>
        /// Major/destructive actions never dwell-fire: hovering Enter,
        /// Watch, Delete, Record or Save for 1s must not trigger them
        /// (device: virtual scene opened with no pinch at all).
        /// </summary>
        /// <summary>Action policy set by the action owner, never inferred
        /// from a GameObject name (renames silently changed safety).</summary>
        public enum DwellPolicy { Allow, RequireExplicit }
        public DwellPolicy policy = DwellPolicy.Allow;
        public bool IsDwellExcluded() => policy == DwellPolicy.RequireExplicit;

        public static bool IsDwellExcluded(string buttonName) =>
            string.IsNullOrEmpty(buttonName) ? false :
            buttonName.Contains("Delete") ||
            buttonName == "EnterButton" || buttonName == "WatchButton" ||
            buttonName == "RecordButton" || buttonName == "SaveButton";

        private Button _button;
        private Image _progress;
        private bool _hovering;
        private float _elapsed;

        public bool IsDwelling => _hovering;
        public float Progress01 => AccessibilitySettings.DwellSeconds > 0f
            ? Mathf.Clamp01(_elapsed / AccessibilitySettings.DwellSeconds) : 0f;

        private void Awake()
        {
            _button = GetComponent<Button>();
            _progress = FindOrCreateProgress();
        }

        private void OnDisable() => EndDwell();

        public void OnPointerEnter(PointerEventData eventData) => BeginDwell();
        public void OnPointerExit(PointerEventData eventData) => EndDwell();

        public void BeginDwell()
        {
            if (SuppressAll || IsDwellExcluded() || IsDwellExcluded(gameObject.name)) return;
            if (!AccessibilitySettings.DwellEnabled || AccessibilitySettings.DiagnosticSuppressDwell) return;
            if (_button == null) _button = GetComponent<Button>();
            if (_button == null || !_button.interactable) return;
            _hovering = true;
            _elapsed = 0f;
        }

        public void EndDwell()
        {
            _hovering = false;
            _elapsed = 0f;
            if (_progress != null) _progress.fillAmount = 0f;
        }

        private void Update() => Advance(Time.unscaledDeltaTime);

        /// <summary>Advances the dwell timer (also driven directly by tests).</summary>
        public void Advance(float deltaTime)
        {
            if (SuppressAll || IsDwellExcluded() || IsDwellExcluded(gameObject.name)) { EndDwell(); return; }
            if (!_hovering) return;
            _elapsed += deltaTime;
            if (_progress != null) _progress.fillAmount = Progress01;
            if (_elapsed < AccessibilitySettings.DwellSeconds) return;
            EndDwell();
            if (_button != null && _button.interactable)
            {
                // Event-source marker: dwell is distinguishable from a
                // native pinch click in every device trace.
                Debug.Log("[ReMotion] Activation source=DWELL button=" + gameObject.name);
                ExecuteEvents.Execute(gameObject,
                    new PointerEventData(EventSystem.current),
                    ExecuteEvents.submitHandler);
            }
        }

        private Image FindOrCreateProgress()
        {
            var existing = transform.Find("DwellProgress");
            if (existing != null) return existing.GetComponent<Image>();
            var go = new GameObject("DwellProgress");
            go.transform.SetParent(transform, false);
            var image = go.AddComponent<Image>();
            image.sprite = ReMotionXR.Library.ReMotionUiTheme.RoundedSprite;
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = (int)Image.OriginHorizontal.Left;
            image.color = ReMotionXR.Library.ReMotionUiTheme.Success;
            image.raycastTarget = false;
            image.fillAmount = 0f;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.offsetMin = new Vector2(6f, 3f);
            rt.offsetMax = new Vector2(-6f, 7f);
            return image;
        }
    }
}
