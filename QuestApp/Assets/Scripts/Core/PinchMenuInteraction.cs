using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Pinch-to-interact for the Library menu, on Meta's Interaction SDK:
    /// RayInteractor (pinch selector) per hand aims a ray at the canvas,
    /// PointableCanvas forwards pointer events to uGUI, and
    /// PointableCanvasModule (on the EventSystem) turns pinch into clicks.
    /// The interactors themselves are baked into the scene by the editor
    /// setup (ReMotion XR / Setup Pinch Menu Interaction) because the
    /// official prefab is too interconnected to rebuild at runtime; this
    /// class ensures the canvas/module side at load and reports status.
    /// All Meta types are reached by reflection: no hard SDK reference.
    /// </summary>
    public static class PinchMenuInteraction
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureAtLoad()
        {
            // Main-scene bootstrap only: never inject ReMotion input
            // objects into other scenes (e.g. the pinch diagnostic app).
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main") return;
            var canvas = FindLibraryCanvas();
            if (canvas == null)
            {
                Debug.LogWarning("[ReMotion] Pinch menu: no world-space Library canvas found.");
                return;
            }
            bool canvasReady = EnsureCanvasPointable(canvas);
            bool moduleReady = EnsureCanvasModule();
            // The interactors come from Meta's building blocks, connected
            // into the scene by ReMotionPinchSetup (editor). OVRHand does
            // not implement ISDK's IHand — the [BB] Hands block supplies
            // that chain — so there is no honest runtime shortcut here;
            // this side only ensures canvas/module and reports status.
            int rayInteractors = CountByTypeName("RayInteractor");
            int beams = CountByTypeName("RayInteractorRayVisual");
            bool surfaceReady = CanvasSurfaceBound(canvas);
            bool chainReady = canvasReady && moduleReady && rayInteractors > 0
                              && beams > 0 && surfaceReady;
            if (chainReady)
                Debug.Log($"[ReMotion] Native pinch structure configured ({rayInteractors} interactor(s), " +
                          $"{beams} beam(s), bounded canvas surface bound); native click not verified until a Select transition is observed.");
            else
                Debug.LogWarning("[ReMotion] Native pinch INCOMPLETE " +
                    $"(canvasBound={canvasReady}, module={moduleReady}, interactors={rayInteractors}, " +
                    $"beams={beams}, surfaceBound={surfaceReady}). " +
                    "Run: ReMotion XR / Setup Pinch Menu Interaction, then save the scene.");

            // Native Meta input is authoritative. The OVRHand fallback is
            // diagnostic only: explicitly requested, mutually exclusive
            // (it disables the native module), and logged as diagnostic.
            if (FallbackExplicitlyRequested) EnableFallback();
            if (canvas.GetComponent<PinchInputTrace>() == null)
                canvas.gameObject.AddComponent<PinchInputTrace>();
        }

        /// <summary>True when the canvas RayInteractable has a surface.</summary>
        public static bool CanvasSurfaceBound(Canvas canvas)
        {
            var rayType = FindLoadedType("RayInteractable");
            if (canvas == null || rayType == null) return false;
            var ray = canvas.GetComponent(rayType);
            if (ray == null) return false;
            return rayType.GetProperty("Surface")?.GetValue(ray) != null;
        }

        /// <summary>
        /// Bounded native-input trace: logs RayInteractor state
        /// transitions (Hover/Select) per hand so a device run shows
        /// the first missing stage (selector → interactor → canvas).
        /// Transition-only; no per-frame spam.
        /// </summary>
        private sealed class PinchInputTrace : MonoBehaviour
        {
            private sealed class Edge
            {
                public Component Interactor;
                public object SelectorHand;   // IHand (IndexPinchSelector.Hand)
                public Component OvrHand;     // same-side OVRHand
                public string Side;
                public bool? OvrPinch, IHandPinch, Connected, OvrTracked;
                public int StrengthBand = -1;
                public string State;
            }

            private readonly System.Collections.Generic.List<Edge> _edges =
                new System.Collections.Generic.List<Edge>();

            private static object Get(object obj, string property) =>
                obj?.GetType().GetProperty(property)?.GetValue(obj);

            private static object Field(object obj, string field) =>
                obj?.GetType().GetField(field,
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public)?.GetValue(obj);

            private void Start()
            {
                var rayType = PinchMenuInteraction.FindLoadedType("RayInteractor");
                var ovrType = PinchMenuInteraction.FindLoadedType("OVRHand");
                if (rayType == null) return;
                var ovrHands = new System.Collections.Generic.List<Component>();
                if (ovrType != null)
                    foreach (var obj in FindObjectsByType(ovrType, FindObjectsSortMode.None))
                        if (obj is Component c) ovrHands.Add(c);
                foreach (var obj in FindObjectsByType(rayType, FindObjectsSortMode.None))
                {
                    if (obj is not Component interactor) continue;
                    string side = interactor.gameObject.name.Contains("Left") ? "Left"
                                : interactor.gameObject.name.Contains("Right") ? "Right" : "?";
                    // Selector may sit on a parent/ancestor of the named GO.
                    for (var t = interactor.transform; t != null && side == "?"; t = t.parent)
                        side = t.name.Contains("Left") ? "Left" : t.name.Contains("Right") ? "Right" : "?";
                    var selector = Field(interactor, "_selector") as Component;
                    object hand = selector != null ? Get(selector, "Hand") : null;
                    Component ovr = null;
                    foreach (var c in ovrHands)
                        if ((side == "Left" && c.gameObject.name.Contains("left")) ||
                            (side == "Right" && c.gameObject.name.Contains("right")) ||
                            (side != "?" && c.transform.root.GetComponentsInChildren<Component>(true).Length >= 0
                             && SideOf(c.transform) == side))
                            ovr = c;
                    var edge = new Edge
                    {
                        Interactor = interactor, SelectorHand = hand,
                        OvrHand = ovr, Side = side,
                    };
                    _edges.Add(edge);
                    // True selector events (not polling): proves the
                    // IndexPinchSelector lifecycle actually fires.
                    if (selector != null)
                    {
                        try
                        {
                            var selEvent = selector.GetType().GetEvent("WhenSelected");
                            var unselEvent = selector.GetType().GetEvent("WhenUnselected");
                            string capturedSide = side;
                            selEvent?.AddEventHandler(selector, new System.Action(() =>
                                Debug.Log($"[ReMotion] Native input[{capturedSide}]: selector WhenSelected FIRED")));
                            unselEvent?.AddEventHandler(selector, new System.Action(() =>
                                Debug.Log($"[ReMotion] Native input[{capturedSide}]: selector WhenUnselected FIRED")));
                        }
                        catch { }
                    }
                    Debug.Log($"[ReMotion] Input trace edge[{side}]: interactor={interactor.gameObject.name} " +
                              $"selector={(selector != null ? selector.gameObject.name : "null")} " +
                              $"iHand={(hand != null ? "bound" : "null")} ovrHand={(ovr != null ? ovr.gameObject.name : "null")}");
                }
                Debug.Log($"[ReMotion] Input trace armed: {_edges.Count} edge(s), " +
                          $"dwellEnabled={AccessibilitySettings.DwellEnabled} (configured={AccessibilitySettings.DwellConfigured}), " +
                          $"fallbackDiagnostic={PinchMenuInteraction.FallbackExplicitlyRequested}");
            }

            private static string SideOf(Transform t)
            {
                for (var x = t; x != null; x = x.parent)
                {
                    if (x.name.Contains("Left")) return "Left";
                    if (x.name.Contains("Right")) return "Right";
                }
                return "?";
            }

            private void Update()
            {
                foreach (var e in _edges)
                {
                    // Raw OVR index pinch bit (first edge of the chain).
                    if (e.OvrHand != null)
                    {
                        try
                        {
                            var fingerType = e.OvrHand.GetType().GetNestedType("HandFinger");
                            var index = System.Enum.Parse(fingerType, "Index");
                            bool pinch = (bool)e.OvrHand.GetType()
                                .GetMethod("GetFingerIsPinching").Invoke(e.OvrHand, new[] { index });
                            if (e.OvrPinch != pinch)
                            {
                                e.OvrPinch = pinch;
                                Debug.Log($"[ReMotion] Native input[{e.Side}]: OVR index pinch -> {pinch}");
                            }
                            // Strength bands: shows a pinch approaching
                            // even when the bit threshold never flips.
                            float strength = (float)e.OvrHand.GetType()
                                .GetMethod("GetFingerPinchStrength").Invoke(e.OvrHand, new[] { index });
                            int band = strength < 0.25f ? 0 : strength < 0.5f ? 1 : strength < 0.75f ? 2 : 3;
                            if (e.StrengthBand != band && band > 0)
                            {
                                Debug.Log($"[ReMotion] Native input[{e.Side}]: OVR pinch strength band -> {band} ({strength:F2})");
                            }
                            e.StrengthBand = band;
                            bool tracked = (bool)(Get(e.OvrHand, "IsTracked") ?? false);
                            if (e.OvrTracked != tracked)
                            {
                                e.OvrTracked = tracked;
                                Debug.Log($"[ReMotion] Native input[{e.Side}]: OVRHand IsTracked -> {tracked}");
                            }
                        }
                        catch { /* bounded: value edges only */ }
                    }
                    // IHand pinch bit + connected (selector's source).
                    if (e.SelectorHand != null)
                    {
                        try
                        {
                            bool pinch = (bool)e.SelectorHand.GetType()
                                .GetMethod("GetIndexFingerIsPinching").Invoke(e.SelectorHand, null);
                            if (e.IHandPinch != pinch)
                            {
                                e.IHandPinch = pinch;
                                Debug.Log($"[ReMotion] Native input[{e.Side}]: IHand index pinch -> {pinch} (selector event expected)");
                            }
                            bool conn = (bool)(Get(e.SelectorHand, "IsConnected") ?? false);
                            if (e.Connected != conn)
                            {
                                e.Connected = conn;
                                Debug.Log($"[ReMotion] Native input[{e.Side}]: IHand connected -> {conn}");
                            }
                        }
                        catch { }
                    }
                    string state = Get(e.Interactor, "State")?.ToString() ?? "?";
                    if (e.State != state)
                    {
                        e.State = state;
                        Debug.Log($"[ReMotion] Native input[{e.Side}]: RayInteractor state -> {state}");
                    }
                }
            }
        }

        /// <summary>Diagnostic fallback is opt-in only (Settings/PlayerPrefs), never the default path.</summary>
        public static bool FallbackExplicitlyRequested => PlayerPrefs.GetInt("remotion_pinch_fallback", 0) == 1;

        /// <summary>True while any RayInteractor reports Hover/Select state.</summary>
        public static bool AnyRayInteractorActive()
        {
            var type = FindLoadedType("RayInteractor");
            if (type == null) return false;
            foreach (var component in UnityEngine.Object.FindObjectsByType(type, FindObjectsSortMode.None))
            {
                var state = type.GetProperty("State")?.GetValue(component)?.ToString();
                if (state != null && state != "Normal" && state != "Disabled") return true;
                if (type.GetProperty("HasInteractable")?.GetValue(component) is bool has && has) return true;
            }
            return false;
        }

        /// <summary>
        /// Switches menu selection to the direct OVRHand fallback and turns
        /// the ISDK canvas module off, so one pinch can never click twice.
        /// </summary>
        public static PinchFallbackSelector EnableFallback()
        {
            var moduleType = FindLoadedType("PointableCanvasModule");
            var eventSystem = UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>();
            if (moduleType != null && eventSystem != null &&
                eventSystem.GetComponent(moduleType) is Behaviour module)
                module.enabled = false;

            var go = UnityEngine.Object.FindFirstObjectByType<PinchFallbackSelector>()?.gameObject
                     ?? new GameObject("PinchFallbackSelector");
            var selector = go.GetComponent<PinchFallbackSelector>()
                           ?? go.AddComponent<PinchFallbackSelector>();
            selector.targetCanvas = FindLibraryCanvas();
            selector.Initialize();
            selector.SetEnabled(true);
            Debug.Log("[ReMotion] DIAGNOSTIC FALLBACK ACTIVE: Pinch clicks owned by OVRHand selector " +
                      $"(hands found: {selector.HandCount}); ISDK ray stays for cursor/hover.");
            return selector;
        }

        public static Canvas FindLibraryCanvas()
        {
            // Explicit named canvas first; world-space search is fallback.
            var named = GameObject.Find("LibraryCanvas_WorldSpace");
            if (named != null && named.GetComponent<Canvas>() is Canvas namedCanvas)
                return namedCanvas;
            foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (canvas.renderMode == RenderMode.WorldSpace) return canvas;
            return null;
        }

        /// <summary>Adds PointableCanvas and binds its Canvas via the
        /// SDK's public InjectCanvas — no private-field reflection.</summary>
        public static bool EnsureCanvasPointable(Canvas canvas)
        {
            if (canvas == null) return false;
            var type = FindLoadedType("Oculus.Interaction.PointableCanvas") ??
                       FindLoadedType("PointableCanvas");
            if (type == null) return false;
            var component = canvas.GetComponent(type) ?? canvas.gameObject.AddComponent(type);
            var bound = type.GetProperty("Canvas")?.GetValue(component) as Canvas;
            if (bound == null)
                type.GetMethod("InjectCanvas")?.Invoke(component, new object[] { canvas });
            if (canvas.GetComponent<GraphicRaycaster>() == null)
                canvas.gameObject.AddComponent<GraphicRaycaster>();
            return type.GetProperty("Canvas")?.GetValue(component) != null;
        }

        /// <summary>
        /// Reconciles the EventSystem for native mode: the native module
        /// enabled, competing modules disabled, diagnostic selector off.
        /// Exactly one input ownership at a time.
        /// </summary>
        public static bool EnsureCanvasModule()
        {
            var eventSystem = XrUiInputSetup.EnsureEventSystem();
            var type = FindLoadedType("Oculus.Interaction.PointableCanvasModule") ??
                       FindLoadedType("PointableCanvasModule");
            if (type == null) return false;
            var module = eventSystem.GetComponent(type) as Behaviour;
            if (module == null)
                module = eventSystem.gameObject.AddComponent(type) as Behaviour;
            if (module != null) module.enabled = true;
            var standalone = eventSystem.GetComponent<StandaloneInputModule>();
            if (standalone != null) standalone.enabled = false;
            if (!FallbackExplicitlyRequested)
            {
                var fallback = UnityEngine.Object.FindFirstObjectByType<PinchFallbackSelector>();
                if (fallback != null) fallback.SetEnabled(false);
            }
            return module != null && module.enabled;
        }

        public static int CountByTypeName(string typeName)
        {
            int count = 0;
            foreach (var component in UnityEngine.Object.FindObjectsByType<Component>(FindObjectsSortMode.None))
                if (component != null && component.GetType().Name == typeName) count++;
            return count;
        }

        public static Type FindLoadedType(string fullOrSimpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = asm.GetType(fullOrSimpleName);
                if (type != null) return type;
                foreach (var candidate in asm.GetTypes())
                    if (candidate.Name == fullOrSimpleName) return candidate;
            }
            return null;
        }
    }
}
