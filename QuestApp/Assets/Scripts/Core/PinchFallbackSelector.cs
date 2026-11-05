using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Hands;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Guaranteed point-and-pinch menu selection, driven directly by
    /// OVRHand (Meta Quest hand tracking): the hand's pointer pose casts a
    /// ray at the world-space menu canvas, hover is forwarded to uGUI, and
    /// an index pinch (strength crossing threshold) presses the button.
    /// This is the safety net, not the primary path: Meta's Interaction
    /// SDK blocks stay in charge, and PinchMenuInteraction only enables
    /// this fallback when no ISDK ray interactor ever hovers/selects —
    /// at which point it disables PointableCanvasModule so a pinch can
    /// never fire a button twice. OVR types via reflection (no hard ref).
    /// </summary>
    public class PinchFallbackSelector : MonoBehaviour
    {
        public Canvas targetCanvas;
        public Camera rayCamera;
        public float pinchThreshold = 0.7f;

        [Tooltip("Debug cue: show a ray as fingers close to a pinch so the hit point on the menu is visible before clicking. Uncheck once pinch is proven.")]
        public bool showPinchRay = true;
        public PinchRayVisualizer pinchRayVisual;

        private class HandState
        {
            public Component ovrHand;
            public MethodInfo pinchStrength;
            public MethodInfo isPinchingMethod;
            public PropertyInfo pointerPose;
            public PropertyInfo pointerPoseValid;
            public PropertyInfo isTracked;
            public object indexFinger;
            public Component skeleton;
            public HandednessGuard.HandSide side = HandednessGuard.HandSide.None;
            public float xrTipDistance = float.MaxValue;
            public float lastTipDistance = float.MaxValue;
            public int tipSampleFrame = -100;
            public bool wasPinching;
            public GameObject hovered;
            public GameObject pressed;
            public float pressStartedAt;
            public bool pressWasDirect;
            public string Label => side == HandednessGuard.HandSide.None ? "Hand" : side.ToString();
        }

        private readonly List<HandState> _hands = new List<HandState>();
        private GraphicRaycaster _raycaster;
        private bool _initialized;
        private float _lastHeartbeat;
        private XRHandSubsystem _xrHands;
        private float _lastClickTime = -10f;
        private GameObject _stickyTarget;
        private float _stickyAt = -10f;
        private int _heartbeatCount;
        private int _isdkHitFrame = -1;
        private HandState _isdkHitHand;
        private GameObject _isdkHitTarget;
        private PointerEventData _isdkHitData;

        /// <summary>
        /// Pinch from Unity XR Hands (OpenXR joint data): thumb-tip to
        /// index-tip distance. This is the reliable signal on Quest — the
        /// Meta OVRHand analog strength/gesture reads stayed 0 on device
        /// even while the hand root pose tracked fine.
        /// </summary>
        /// <summary>Closest tip distance across hands (diagnostics only — never decide a hand's pinch from this).</summary>
        public float XrTipDistance { get; private set; } = float.MaxValue;
        public float LeftXrTipDistance { get; private set; } = float.MaxValue;
        public float RightXrTipDistance { get; private set; } = float.MaxValue;
        public bool XrPinching => XrTipDistance < 0.035f;

        /// <summary>Tip distance belonging to ONE hand. Left pinch must never fire the right hand (and vice versa).</summary>
        public static float OwnTipDistance(HandednessGuard.HandSide side, float leftTip, float rightTip) =>
            side == HandednessGuard.HandSide.Left ? leftTip :
            side == HandednessGuard.HandSide.Right ? rightTip :
            Mathf.Min(leftTip, rightTip);

        /// <summary>Per-hand pinch decision from that hand's own signals only.</summary>
        public static bool IsPinchingForHand(float ownXrTip, float skeletonTip,
                                             float strength, bool gesture, float threshold) =>
            gesture || strength >= threshold || skeletonTip < 0.028f || ownXrTip < 0.035f;

        private void UpdateXrHandsPinch()
        {
            XrTipDistance = float.MaxValue;
            LeftXrTipDistance = float.MaxValue;
            RightXrTipDistance = float.MaxValue;
            if (_xrHands == null || !_xrHands.running)
            {
                var subsystems = new List<XRHandSubsystem>();
                SubsystemManager.GetSubsystems(subsystems);
                foreach (var s in subsystems)
                    if (s.running) { _xrHands = s; break; }
                if (_xrHands == null) return;
                Debug.Log("[ReMotion] XR Hands subsystem found for pinch input.");
            }
            LeftXrTipDistance = TipDistance(_xrHands.leftHand);
            RightXrTipDistance = TipDistance(_xrHands.rightHand);
            XrTipDistance = Mathf.Min(LeftXrTipDistance, RightXrTipDistance);
            foreach (var hand in _hands)
                hand.xrTipDistance = OwnTipDistance(hand.side, LeftXrTipDistance, RightXrTipDistance);
        }

        private static float TipDistance(XRHand hand)
        {
            if (!hand.isTracked) return float.MaxValue;
            var index = hand.GetJoint(XRHandJointID.IndexTip);
            var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
            if (index.trackingState == XRHandJointTrackingState.None ||
                thumb.trackingState == XRHandJointTrackingState.None) return float.MaxValue;
            return index.TryGetPose(out Pose ip) && thumb.TryGetPose(out Pose tp)
                ? Vector3.Distance(ip.position, tp.position) : float.MaxValue;
        }

        public bool IsEnabled { get; private set; }
        public int HandCount => _hands.Count;

        /// <summary>Finds OVRHand components and prepares reflection accessors.</summary>
        public bool Initialize()
        {
            _hands.Clear();
            var ovrHandType = PinchMenuInteraction.FindLoadedType("OVRHand");
            if (ovrHandType == null) return false;
            var fingerType = ovrHandType.GetNestedType("HandFinger");
            foreach (var component in FindObjectsByType<Component>(FindObjectsSortMode.None))
            {
                if (component == null || component.GetType() != ovrHandType) continue;
                var state = new HandState
                {
                    ovrHand = component,
                    pinchStrength = ovrHandType.GetMethod("GetFingerPinchStrength"),
                    isPinchingMethod = ovrHandType.GetMethod("GetFingerIsPinching"),
                    pointerPose = ovrHandType.GetProperty("PointerPose"),
                    pointerPoseValid = ovrHandType.GetProperty("IsPointerPoseValid"),
                    isTracked = ovrHandType.GetProperty("IsTracked"),
                    indexFinger = fingerType != null ? Enum.Parse(fingerType, "Index") : null,
                    side = HandednessGuard.ResolveSide(component.transform),
                };
                if (state.pinchStrength != null && state.pointerPose != null)
                    _hands.Add(state);
            }
            _initialized = _hands.Count > 0;
            return _initialized;
        }

        /// <summary>Highest index-pinch strength across tracked hands (0 without hand data).</summary>
        public float SampleMaxPinch()
        {
            if (!_initialized && !Initialize()) return 0f;
            float max = 0f;
            foreach (var hand in _hands)
            {
                if (hand.isTracked != null && hand.isTracked.GetValue(hand.ovrHand) is bool t && !t) continue;
                if (hand.indexFinger == null) continue;
                float s = (float)hand.pinchStrength.Invoke(hand.ovrHand, new[] { hand.indexFinger });
                if (s > max) max = s;
            }
            return max;
        }

        private readonly List<Component> _isdkRayInteractors = new List<Component>();
        private PropertyInfo _isdkRayProperty;

        /// <summary>
        /// Hit test along Meta RayInteractors' own rays — the exact rays
        /// behind the cursor the user sees, so targeting cannot disagree
        /// with the visual pointer.
        /// </summary>
        private bool EnsureIsdkInteractors()
        {
            if (_isdkRayInteractors.Count > 0) return _isdkRayProperty != null;
            var type = PinchMenuInteraction.FindLoadedType("RayInteractor");
            if (type == null) return false;
            _isdkRayProperty = type.GetProperty("Ray");
            if (_isdkRayProperty == null) return false;
            foreach (var c in FindObjectsByType(type, FindObjectsSortMode.None))
                if (c is Component comp) _isdkRayInteractors.Add(comp);
            return _isdkRayInteractors.Count > 0;
        }

        private static bool SideMatches(Component interactor, HandednessGuard.HandSide handSide)
        {
            if (handSide == HandednessGuard.HandSide.None || interactor == null) return true;
            var side = HandednessGuard.ResolveSide(interactor.transform);
            return side == HandednessGuard.HandSide.None || side == handSide;
        }

        /// <summary>Hit along THIS hand's RayInteractor ray only — the other hand's ray must not steal its click.</summary>
        private bool TryIsdkRayHit(HandState hand, out GameObject target, out PointerEventData data)
        {
            target = null; data = null;
            int frame = Time.frameCount;
            if (hand != null && hand == _isdkHitHand && _isdkHitFrame == frame)
            {
                target = _isdkHitTarget; data = _isdkHitData;
                return target != null;
            }
            if (!EnsureIsdkInteractors()) return false;
            foreach (var interactor in _isdkRayInteractors)
            {
                if (interactor == null || !SideMatches(interactor, hand?.side ?? HandednessGuard.HandSide.None)) continue;
                if (!(_isdkRayProperty.GetValue(interactor) is Ray ray)) continue;
                if (TryHit(targetCanvas, rayCamera, _raycaster, ray.origin, ray.direction,
                           out target, out data))
                {
                    _isdkHitFrame = frame; _isdkHitHand = hand;
                    _isdkHitTarget = target; _isdkHitData = data;
                    return true;
                }
            }
            return false;
        }

        /// <summary>This hand's live RayInteractor ray (the aiming ray behind its cursor), if any.</summary>
        private bool TryGetIsdkRay(HandState hand, out Ray ray)
        {
            ray = default;
            if (!EnsureIsdkInteractors()) return false;
            foreach (var interactor in _isdkRayInteractors)
            {
                if (interactor == null || !SideMatches(interactor, hand?.side ?? HandednessGuard.HandSide.None)) continue;
                if (_isdkRayProperty.GetValue(interactor) is Ray r &&
                    r.direction.sqrMagnitude > 0.0001f)
                {
                    ray = r;
                    return true;
                }
            }
            return false;
        }

        private void EnsureRayVisual()
        {
            if (pinchRayVisual != null) return;
            pinchRayVisual = GetComponent<PinchRayVisualizer>() ??
                             gameObject.AddComponent<PinchRayVisualizer>();
            pinchRayVisual.EnsureBuilt();
        }

        /// <summary>Drive the near-pinch debug ray for the best (closest-to-pinch) hand this frame.</summary>
        private void UpdateRayVisual(Vector3 origin, Vector3 direction, GameObject hit,
                                     float tipDistance, float strength, bool pinching, bool tracked)
        {
            if (!showPinchRay)
            {
                if (pinchRayVisual != null) pinchRayVisual.HideRay();
                return;
            }
            EnsureRayVisual();
            pinchRayVisual.showRay = showPinchRay;
            var state = pinchRayVisual.StateFor(tipDistance, strength, pinchThreshold, pinching, hit != null);
            if (!pinchRayVisual.ShouldShow(tipDistance, strength, pinchThreshold, pinching, tracked))
            {
                pinchRayVisual.HideRay();
                return;
            }
            float proximity = pinchRayVisual.CombinedProximity(tipDistance, strength, pinchThreshold);
            Vector3 endPoint = origin + direction.normalized * pinchRayVisual.rayLength;
            if (hit != null && targetCanvas != null)
            {
                var plane = new Plane(targetCanvas.transform.forward, targetCanvas.transform.position);
                var ray = new Ray(origin, direction);
                if (plane.Raycast(ray, out float d) && d > 0f) endPoint = ray.GetPoint(d);
            }
            pinchRayVisual.ShowRay(origin, direction, endPoint, proximity, state);
        }

        /// <summary>Index-tip to thumb-tip distance from the hand skeleton (reflection).</summary>
        private float TipDistance(HandState hand)
        {
            if (hand.skeleton == null)
            {
                var skeletonType = PinchMenuInteraction.FindLoadedType("OVRSkeleton");
                if (skeletonType == null) return float.MaxValue;
                hand.skeleton = (hand.ovrHand.GetComponentInChildren(skeletonType)
                                 ?? hand.ovrHand.GetComponent(skeletonType)) as Component;
                if (hand.skeleton == null) return float.MaxValue;
            }
            var bonesProp = hand.skeleton.GetType().GetProperty("Bones");
            if (!(bonesProp?.GetValue(hand.skeleton) is System.Collections.IList bones))
                return float.MaxValue;
            Transform indexTip = null, thumbTip = null;
            foreach (var bone in bones)
            {
                if (bone == null) continue;
                string id = bone.GetType().GetProperty("Id")?.GetValue(bone)?.ToString();
                var tr = bone.GetType().GetProperty("Transform")?.GetValue(bone) as Transform;
                if (id == "Hand_IndexTip") indexTip = tr;
                else if (id == "Hand_ThumbTip") thumbTip = tr;
            }
            return indexTip != null && thumbTip != null
                ? Vector3.Distance(indexTip.position, thumbTip.position) : float.MaxValue;
        }

        public void SetEnabled(bool on)
        {
            IsEnabled = on;
            enabled = on;
            if (!on && pinchRayVisual != null) pinchRayVisual.HideRay();
            if (on) Debug.Log("[ReMotion] Pinch fallback selector ENABLED (OVRHand direct). Pinch a menu button to click it.");
        }

        private void OnDisable()
        {
            if (pinchRayVisual != null) pinchRayVisual.HideRay();
        }

        private void Update()
        {
            if (!IsEnabled) return;
            if (!_initialized && !Initialize()) return;
            if (targetCanvas == null) targetCanvas = PinchMenuInteraction.FindLibraryCanvas();
            if (targetCanvas == null) return;
            _raycaster ??= targetCanvas.GetComponent<GraphicRaycaster>();
            if (_raycaster == null) return;
            rayCamera ??= Camera.main;
            if (rayCamera == null) return;

            UpdateXrHandsPinch();

            // Head-gaze target for this frame: centre of view. Quest 3 has
            // no eye tracking, so "gaze" is head direction — look at a
            // button to hover it, pinch with either hand to click it.
            GameObject gazeHit = null;
            PointerEventData gazeData = null;
            TryHit(targetCanvas, rayCamera, _raycaster, rayCamera.transform.position,
                   rayCamera.transform.forward, out gazeHit, out gazeData);

            // Debug ray tracking: keep the aiming ray of the hand closest
            // to a pinch this frame (visualised after the hand loop).
            if (showPinchRay) EnsureRayVisual();
            Vector3 visualOrigin = rayCamera.transform.position;
            Vector3 visualDirection = rayCamera.transform.forward;
            GameObject visualHit = gazeHit;
            float visualTip = float.MaxValue;
            float visualStrength = 0f;
            bool visualPinching = false;
            bool visualTracked = false;
            float visualScore = -1f;

            float heartbeat = Time.realtimeSinceStartup;
            foreach (var hand in _hands)
            {
                // Pinch is read FIRST, unconditionally: gating it on the
                // pointer (aim) pose silently killed every pinch on this
                // runtime, where that pose may never report valid. The pose
                // only decides where the hand ray points, never whether a
                // pinch happened. Gaze covers targeting when it is absent.
                float strength = 0f;
                bool gesturePinch = false;
                float tipDistance = float.MaxValue;
                bool tracked = !(hand.isTracked != null &&
                                 hand.isTracked.GetValue(hand.ovrHand) is bool t && !t);
                try
                {
                    if (hand.indexFinger != null)
                    {
                        strength = (float)hand.pinchStrength.Invoke(hand.ovrHand, new[] { hand.indexFinger });
                        if (hand.isPinchingMethod != null)
                            gesturePinch = (bool)hand.isPinchingMethod.Invoke(hand.ovrHand, new[] { hand.indexFinger });
                    }
                    // XR Hands supplies fingertip joints reliably. Only
                    // sample the reflection-heavy OVR skeleton as a fallback,
                    // and only four times per second.
                    if (_xrHands == null && Time.frameCount - hand.tipSampleFrame >= 15)
                    {
                        hand.tipSampleFrame = Time.frameCount;
                        hand.lastTipDistance = TipDistance(hand);
                    }
                    tipDistance = _xrHands == null ? hand.lastTipDistance : float.MaxValue;
                }
                catch (Exception e) { Debug.LogWarning("[ReMotion] Pinch read failed: " + e.Message); }
                bool pinching = IsPinchingForHand(hand.xrTipDistance, tipDistance, strength, gesturePinch, pinchThreshold);
                if (pinching != hand.wasPinching)
                    Debug.Log($"[ReMotion] Pinch signal {(pinching ? "ON" : "OFF")} ({hand.Label}) " +
                              $"ownTip={hand.xrTipDistance:F3} gesture={gesturePinch} strength={strength:F2} tracked={tracked}");
                if (_heartbeatCount < 4 && heartbeat - _lastHeartbeat > 5f)
                {
                    _heartbeatCount++;
                    _lastHeartbeat = heartbeat;
                    Debug.Log($"[ReMotion] Pinch state: xrTips={(XrTipDistance > 100f ? -1f : XrTipDistance):F3} tracked={tracked} " +
                              $"gazeHit={(gazeHit != null ? gazeHit.name : "none")}");
                }
                if (!tracked) { hand.wasPinching = pinching; continue; }

                GameObject hit = null;
                PointerEventData data = gazeData;
                // The ray the user actually sees is Meta's RayInteractor ray;
                // prefer it so the click lands where their cursor is.
                TryIsdkRayHit(hand, out hit, out PointerEventData isdkData);
                if (hit != null) data = isdkData;
                bool poseValid = !(hand.pointerPoseValid != null &&
                                   hand.pointerPoseValid.GetValue(hand.ovrHand) is bool v && !v);
                if (hit == null && poseValid && hand.pointerPose.GetValue(hand.ovrHand) is Transform pose)
                {
                    // TryHit nulls its out data on a miss: never let a missed
                    // pose ray erase the gaze data the click depends on.
                    if (TryHit(targetCanvas, rayCamera, _raycaster, pose.position, pose.forward,
                               out hit, out PointerEventData poseData))
                        data = poseData;
                }
                if (hit == null && gazeHit != null) hit = gazeHit; // gaze + pinch
                data ??= gazeData;
                if (hit != null) { _stickyTarget = hit; _stickyAt = Time.realtimeSinceStartup; }
                // Candidate for the debug ray: same aiming ray that just
                // produced `hit` (ISDK ray > pointer pose > gaze).
                {
                    Vector3 handOrigin = rayCamera.transform.position;
                    Vector3 handDirection = rayCamera.transform.forward;
                    if (TryGetIsdkRay(hand, out Ray isdkRay))
                    { handOrigin = isdkRay.origin; handDirection = isdkRay.direction; }
                    else if (poseValid && hand.pointerPose.GetValue(hand.ovrHand) is Transform pp)
                    { handOrigin = pp.position; handDirection = pp.forward; }
                    float effTip = Mathf.Min(hand.xrTipDistance, tipDistance);
                    float score = pinchRayVisual != null
                        ? pinchRayVisual.CombinedProximity(effTip, strength, pinchThreshold)
                        : 0f;
                    if (pinching) score = 1f;
                    if (score > visualScore)
                    {
                        visualScore = score;
                        visualOrigin = handOrigin;
                        visualDirection = handDirection;
                        visualHit = hit;
                        visualTip = effTip;
                        visualStrength = strength;
                        visualPinching = pinching;
                        visualTracked = tracked;
                    }
                }
                UpdateHover(hand, hit, data);
                if (pinching && !hand.wasPinching)
                {
                    // One physical pinch can be seen by both hands/signals;
                    // ignore press edges inside the cooldown window.
                    if (Time.realtimeSinceStartup - _lastClickTime < 0.35f)
                    { hand.wasPinching = pinching; continue; }
                    _lastClickTime = Time.realtimeSinceStartup;
                    // Pinching moves the hand; accept the hovered or the
                    // just-hovered (sticky, 0.8s) button when the ray slips
                    // off at the pinch instant — distance makes this common.
                    hand.pressed = hit ?? hand.hovered ??
                        (Time.realtimeSinceStartup - _stickyAt < 0.8f ? _stickyTarget : null);
                    hand.pressStartedAt = Time.realtimeSinceStartup;
                    hand.pressWasDirect = hit != null;
                    if (RecordingActive && hit == null && hand.pressed != null)
                    {
                        Debug.Log("[ReMotion] Ignored mid-air pinch while recording (not aimed at Stop).");
                        hand.pressed = null;
                    }
                    Debug.Log($"[ReMotion] Pinch DOWN ({hand.Label}) strength={strength:F2} hit=" +
                              (hand.pressed != null ? hand.pressed.name : "none"));
                    if (hand.pressed != null)
                    {
                        var downData = ResolveClickData(data, gazeData, hand.pressed);
                        ExecuteEvents.ExecuteHierarchy(hand.pressed, downData, ExecuteEvents.pointerDownHandler);
                    }
                }
                else if (!pinching && hand.wasPinching && hand.pressed != null)
                {
                    var releaseTarget = hit ?? hand.hovered ??
                        (Time.realtimeSinceStartup - _stickyAt < 0.8f ? _stickyTarget : null);
                    // Pinching itself moves the hand, so the release ray
                    // routinely slips onto a neighbour (device: Watch Video
                    // DOWN then UP "cancelled"). The button chosen at DOWN
                    // is the user's intent — always click it on release.
                    bool slipped = releaseTarget != null && releaseTarget != hand.pressed;
                    // A click must never silently vanish: data can be null
                    // on the release frame, so fall back to gaze/synthetic
                    // data centred on the pressed button. (Device: Record
                    // "clicked" in the log but recording never started.)
                    var clickData = ResolveClickData(data, gazeData, hand.pressed);
                    float holdSeconds = Time.realtimeSinceStartup - hand.pressStartedAt;
                    ExecuteEvents.ExecuteHierarchy(hand.pressed, clickData, ExecuteEvents.pointerUpHandler);
                    if (ShouldClickWhileRecording(RecordingActive, hand.pressWasDirect, holdSeconds))
                    {
                        ExecuteEvents.ExecuteHierarchy(hand.pressed, clickData, ExecuteEvents.pointerClickHandler);
                        Debug.Log($"[ReMotion] Pinch UP ({hand.Label}) clicked={hand.pressed.name}" +
                                  (slipped ? $" (release slipped to {releaseTarget.name})" : ""));
                    }
                    else
                    {
                        Debug.Log($"[ReMotion] Stop not triggered: hold a pinch on Stop for " +
                                  $"{RecordingStopHoldSeconds:F1}s (held {holdSeconds:F2}s).");
                    }
                    hand.pressed = null;
                }
                hand.wasPinching = pinching;
            }
            UpdateRayVisual(visualOrigin, visualDirection, visualHit,
                            visualTip, visualStrength, visualPinching, visualTracked);
        }

        /// <summary>Seconds a pinch must be held on Stop to end a recording.</summary>
        public const float RecordingStopHoldSeconds = 0.8f;

        private ReMotionXR.Capture.SessionRecorder _sessionRecorder;

        /// <summary>True while a capture is recording: menu pinch rules tighten.</summary>
        public bool RecordingActive
        {
            get
            {
                if (_sessionRecorder == null)
                    _sessionRecorder = FindFirstObjectByType<ReMotionXR.Capture.SessionRecorder>();
                return _sessionRecorder != null && _sessionRecorder.IsRecording;
            }
        }

        /// <summary>
        /// While recording, a click fires only for a pinch aimed directly
        /// at the button and held — mid-air action pinches (recorded as
        /// content) must never end the recording or open other UI.
        /// </summary>
        public static bool ShouldClickWhileRecording(bool recording, bool directHit, float holdSeconds) =>
            !recording || (directHit && holdSeconds >= RecordingStopHoldSeconds);

        /// <summary>Never-null event data for a pinch click: live ray data, else gaze, else the button centre.</summary>
        public PointerEventData ResolveClickData(PointerEventData data, PointerEventData gazeData, GameObject pressed)
        {
            if (data != null) return data;
            if (gazeData != null) return gazeData;
            Vector2 position = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (pressed != null && rayCamera != null)
            {
                Vector3 screen = rayCamera.WorldToScreenPoint(pressed.transform.position);
                if (screen.z > 0f) position = screen;
            }
            return new PointerEventData(EventSystem.current) { position = position };
        }

        private void UpdateHover(HandState hand, GameObject hit, PointerEventData data)
        {
            if (hand.hovered == hit) return;
            if (hand.hovered != null && data != null)
                ExecuteEvents.ExecuteHierarchy(hand.hovered, data, ExecuteEvents.pointerExitHandler);
            hand.hovered = hit;
            if (hit != null && data != null)
                ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerEnterHandler);
        }

        private sealed class CanvasHitCache
        {
            public float refreshedAt = -10f;
            public Selectable[] selectables = Array.Empty<Selectable>();
            public Button[] buttons = Array.Empty<Button>();
        }

        private static readonly ConditionalWeakTable<Canvas, CanvasHitCache> CanvasCaches =
            new ConditionalWeakTable<Canvas, CanvasHitCache>();
        private static readonly List<RaycastResult> RaycastResults = new List<RaycastResult>();

        private static CanvasHitCache GetCanvasCache(Canvas canvas)
        {
            var cache = CanvasCaches.GetOrCreateValue(canvas);
            if (Time.realtimeSinceStartup - cache.refreshedAt < 0.35f) return cache;
            cache.refreshedAt = Time.realtimeSinceStartup;
            cache.selectables = canvas.GetComponentsInChildren<Selectable>(false);
            cache.buttons = canvas.GetComponentsInChildren<Button>(false);
            return cache;
        }

        private static bool TryExpandedRectHit(Selectable[] candidates, Vector3 screen,
                                               Camera camera, out GameObject target)
        {
            target = null;
            const float pad = 16f;
            foreach (var selectable in candidates)
            {
                if (selectable == null || !selectable.IsInteractable()) continue;
                var rt = selectable.GetComponent<RectTransform>();
                if (rt == null) continue;
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                        rt, screen, camera, out Vector2 local)) continue;
                var expanded = new Rect(rt.rect.xMin - pad, rt.rect.yMin - pad,
                    rt.rect.width + pad * 2f, rt.rect.height + pad * 2f);
                if (!expanded.Contains(local)) continue;
                target = selectable.gameObject;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Ray -> canvas plane -> screen point -> GraphicRaycaster. Pure
        /// enough to unit test with a camera and a world-space canvas.
        /// </summary>
        public static bool TryHit(Canvas canvas, Camera camera, GraphicRaycaster raycaster,
                                  Vector3 origin, Vector3 direction,
                                  out GameObject target, out PointerEventData data)
        {
            target = null; data = null;
            if (canvas == null || camera == null || raycaster == null) return false;
            var plane = new Plane(canvas.transform.forward, canvas.transform.position);
            var ray = new Ray(origin, direction);
            if (!plane.Raycast(ray, out float distance)) return false;
            Vector3 world = ray.GetPoint(distance);
            Vector3 screen = camera.WorldToScreenPoint(world);
            if (screen.z < 0f) return false;
            data = new PointerEventData(EventSystem.current) { position = screen };
            RaycastResults.Clear();
            raycaster.Raycast(data, RaycastResults);
            foreach (var result in RaycastResults)
            {
                var selectable = result.gameObject.GetComponentInParent<Selectable>();
                if (selectable != null && selectable.IsInteractable())
                {
                    target = selectable.gameObject;
                    return true;
                }
            }
            // Geometric fallback: test each selectable's rect directly. This
            // does not depend on GraphicRegistry state (which only fills as
            // graphics enable), so the pinch path works in every context.
            // Buttons win over other selectables (a centred text field must
            // not steal clicks meant for the button next to it), and rects
            // are inflated — pinching moves the hand, so exact edges miss.
            var hitCache = GetCanvasCache(canvas);
            if (TryExpandedRectHit(hitCache.buttons, screen, camera, out target)) return true;
            if (TryExpandedRectHit(hitCache.selectables, screen, camera, out target)) return true;
            // Super-long forgiving ray: exact hits get hard at distance
            // (tiny buttons, hand jitter), so snap to the button whose
            // centre lies within a narrow cone around the ray. Works at
            // any distance because it is angular, not metric.
            const float coneDegrees = 7f;
            GameObject best = null;
            float bestAngle = coneDegrees;
            foreach (var button in hitCache.buttons)
            {
                if (button == null || !button.IsInteractable()) continue; // destroyed card in stale cache
                var rt = button.GetComponent<RectTransform>();
                if (rt == null) continue;
                Vector3 toButton = rt.position - origin;
                if (toButton.sqrMagnitude < 0.0001f) continue;
                float angle = Vector3.Angle(direction, toButton);
                if (angle < bestAngle) { bestAngle = angle; best = button.gameObject; }
            }
            if (best != null)
            {
                target = best;
                data = new PointerEventData(EventSystem.current)
                    { position = camera.WorldToScreenPoint(best.transform.position) };
                return true;
            }
            return false;
        }
    }
}
