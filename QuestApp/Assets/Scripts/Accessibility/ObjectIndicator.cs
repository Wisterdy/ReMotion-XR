using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Accessibility
{
    /// <summary>
    /// Hovering accessibility circle above an object/person. Always faces the
    /// camera (billboard) and pulses. Colors: Blue=Object, Yellow=Person,
    /// Green=Target. Gaze 1s / pinch / tap opens the info card + outline.
    /// </summary>
    public class ObjectIndicator : MonoBehaviour
    {
        public AccessibilityData data;
        public Renderer ringRenderer;
        public float hoverHeight = 0.35f;
        public float gazeSelectSeconds = 1f;

        private Transform _target;
        private float _gazeTime;
        private bool _cardOpen;

        public static Color ColorFor(string kind) => kind switch
        {
            "yellow" => Color.yellow, // Person
            "green" => Color.green,   // Target
            _ => Color.blue,          // Object
        };

        public void Bind(Transform target, AccessibilityData info)
        {
            _target = target;
            data = info;
            if (ringRenderer != null)
                ringRenderer.material.color = ColorFor(info.indicatorColor);
        }

        private void LateUpdate()
        {
            if (_target != null)
                transform.position = _target.position + Vector3.up * hoverHeight;
            if (Camera.main != null)
                transform.rotation = Camera.main.transform.rotation; // billboard
            float pulse = AccessibilitySettings.ReduceMotion
                ? 1f : 1f + 0.12f * Mathf.Sin(Time.time * 3f);
            transform.localScale = Vector3.one * pulse;
        }

        /// <summary>Call each frame while the user's gaze ray hits this indicator.</summary>
        public void OnGazeStay(float deltaTime)
        {
            _gazeTime += deltaTime;
            if (_gazeTime >= gazeSelectSeconds) Select();
        }

        public void OnGazeExit() => _gazeTime = 0f;

        /// <summary>Pinch/tap select — same path as gaze dwell.</summary>
        public void OnPinchOrTap() => Select();

        private void Select()
        {
            if (_cardOpen || data == null) return;
            _cardOpen = true;
            _gazeTime = 0f;
            IndicatorManager.Instance?.ShowCard(data, _target);
        }

        public void CloseCard() => _cardOpen = false;
    }
}
