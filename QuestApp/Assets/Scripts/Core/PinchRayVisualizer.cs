using UnityEngine;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Debug visual cue for pinch menu interaction (remove/disable once
    /// pinch is proven on device): as thumb and index close toward a
    /// pinch, a thin ray fades in from the aiming hand toward the menu
    /// so the user can see exactly what the pinch will touch BEFORE
    /// committing the click. The ray brightens as the fingers close,
    /// turns green when it is touching a menu button, and flashes
    /// larger/brighter at the pinch instant.
    /// To disable permanently: set showRay = false on this component
    /// (or PinchFallbackSelector.showPinchRay = false), or set
    /// PinchRayVisualizer.GlobalEnabled = false. No other code depends
    /// on this visual.
    /// </summary>
    public class PinchRayVisualizer : MonoBehaviour
    {
        public enum RayVisualState { Hidden, Approaching, HoverHit, Pinching }

        /// <summary>Master kill-switch for every ray visual.</summary>
        public static bool GlobalEnabled = true;

        [Tooltip("Uncheck to hide the debug ray without removing the component.")]
        public bool showRay = true;

        [Tooltip("Tip distance (m) where the ray starts fading in.")]
        public float nearPinchDistance = 0.075f;

        [Tooltip("Tip distance (m) treated as a full pinch. Matches PinchFallbackSelector.XrPinching.")]
        public float pinchDistance = 0.035f;

        [Tooltip("OVR pinch strength where the ray starts fading in (fallback when tip distance is unavailable).")]
        public float nearPinchStrength = 0.35f;

        [Tooltip("Ray length (m) when the ray does not touch the menu canvas.")]
        public float rayLength = 3.5f;

        public RayVisualState State { get; private set; } = RayVisualState.Hidden;
        public float Proximity { get; private set; }

        private LineRenderer _line;
        private MeshRenderer _reticleRenderer;
        private Transform _reticle;
        private Material _lineMaterial;
        private Material _reticleMaterial;
        private bool _built;

        public bool EffectiveEnabled => GlobalEnabled && showRay;
        public bool IsBuilt => _built;
        public bool LineVisible => _line != null && _line.enabled;

        /// <summary>0 = fingers open, 1 = at pinch distance. Pure / testable.</summary>
        public float Proximity01(float tipDistance)
        {
            if (float.IsInfinity(tipDistance) || float.IsNaN(tipDistance) ||
                tipDistance >= nearPinchDistance) return 0f;
            if (tipDistance <= pinchDistance) return 1f;
            float span = nearPinchDistance - pinchDistance;
            return span <= 0f ? 1f : 1f - (tipDistance - pinchDistance) / span;
        }

        /// <summary>Strength-based proximity (0..1) for the OVR analog signal.</summary>
        public float StrengthProximity01(float pinchStrength, float pinchThreshold)
        {
            if (pinchStrength < nearPinchStrength) return 0f;
            float span = pinchThreshold - nearPinchStrength;
            return span <= 0f ? 1f : Mathf.Clamp01((pinchStrength - nearPinchStrength) / span);
        }

        /// <summary>Combined proximity across tip-distance and strength signals.</summary>
        public float CombinedProximity(float tipDistance, float pinchStrength, float pinchThreshold) =>
            Mathf.Max(Proximity01(tipDistance), StrengthProximity01(pinchStrength, pinchThreshold));

        /// <summary>Visual state for one frame. Pure / testable.</summary>
        public RayVisualState StateFor(float tipDistance, float pinchStrength,
                                       float pinchThreshold, bool isPinching, bool hasHit)
        {
            if (isPinching) return RayVisualState.Pinching;
            if (CombinedProximity(tipDistance, pinchStrength, pinchThreshold) <= 0f)
                return RayVisualState.Hidden;
            return hasHit ? RayVisualState.HoverHit : RayVisualState.Approaching;
        }

        /// <summary>True once either signal enters the near-pinch zone.</summary>
        public bool ShouldShow(float tipDistance, float pinchStrength,
                               float pinchThreshold, bool isPinching, bool isTracked)
        {
            if (!EffectiveEnabled || !isTracked) return false;
            if (isPinching) return true;
            return CombinedProximity(tipDistance, pinchStrength, pinchThreshold) > 0f;
        }

        public void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            var lineGo = new GameObject("PinchRayLine");
            lineGo.transform.SetParent(transform, false);
            _line = lineGo.AddComponent<LineRenderer>();
            _line.positionCount = 2;
            _line.useWorldSpace = true;
            _line.numCapVertices = 4;
            _lineMaterial = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"));
            _lineMaterial.color = Color.white;
            _line.material = _lineMaterial;
            _line.enabled = false;

            var reticleGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            reticleGo.name = "PinchRayReticle";
            var collider = reticleGo.GetComponent<Collider>();
            if (collider != null) collider.enabled = false; // never blocks menu raycasts
            reticleGo.transform.SetParent(transform, false);
            _reticle = reticleGo.transform;
            _reticleRenderer = reticleGo.GetComponent<MeshRenderer>();
            _reticleMaterial = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"));
            _reticleRenderer.material = _reticleMaterial;
            _reticleRenderer.enabled = false;
        }

        /// <summary>Show/update the ray for this frame. endPoint = canvas hit point, or origin+direction*rayLength.</summary>
        public void ShowRay(Vector3 origin, Vector3 direction, Vector3 endPoint,
                            float proximity01, RayVisualState state)
        {
            if (!EffectiveEnabled || state == RayVisualState.Hidden ||
                direction.sqrMagnitude < 0.0001f)
            {
                HideRay();
                return;
            }
            EnsureBuilt();
            State = state;
            Proximity = Mathf.Clamp01(proximity01);

            Color color = ColorForState(state, Proximity);
            float width = state == RayVisualState.Pinching ? 0.007f
                        : Mathf.Lerp(0.0025f, 0.005f, Proximity);
            _line.enabled = true;
            _line.startWidth = width;
            _line.endWidth = width * 0.6f;
            _line.startColor = new Color(color.r, color.g, color.b, color.a * 0.35f);
            _line.endColor = color;
            _line.SetPosition(0, origin);
            _line.SetPosition(1, endPoint);

            float scale = state == RayVisualState.Pinching ? 0.024f
                        : Mathf.Lerp(0.010f, 0.016f, Proximity);
            _reticle.position = endPoint;
            _reticle.localScale = Vector3.one * scale;
            _reticleMaterial.color = color;
            _reticleRenderer.enabled = true;
        }

        public void HideRay()
        {
            State = RayVisualState.Hidden;
            Proximity = 0f;
            if (_line != null) _line.enabled = false;
            if (_reticleRenderer != null) _reticleRenderer.enabled = false;
        }

        public static Color ColorForState(RayVisualState state, float proximity01) => state switch
        {
            RayVisualState.HoverHit => new Color(0.16f, 0.85f, 0.45f, 0.9f),
            RayVisualState.Pinching => new Color(1f, 0.85f, 0.25f, 1f),
            RayVisualState.Approaching => new Color(0.35f, 0.65f, 1f,
                Mathf.Lerp(0.3f, 0.75f, Mathf.Clamp01(proximity01))),
            _ => Color.clear,
        };

        private void OnDisable() => HideRay();
    }
}
