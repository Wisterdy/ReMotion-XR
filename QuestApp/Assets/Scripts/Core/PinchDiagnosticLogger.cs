using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Observer-only logger for the pinch diagnostic scene (the untouched
    /// SDK RayExamples graph). Feeds nothing into input; it only reports
    /// the chain edges so a device run names the first dead stage:
    /// OVR raw pinch/strength -> IHand pinch -> selector events ->
    /// RayInteractor state. First read exception per member is logged
    /// once instead of being swallowed silently.
    /// </summary>
    public sealed class PinchDiagnosticLogger : MonoBehaviour
    {
        private sealed class Edge
        {
            public Component Interactor;
            public object SelectorHand;
            public Component OvrHand;
            public string Side;
            public bool? OvrPinch, IHandPinch, Connected, OvrTracked;
            public int StrengthBand = -1;
            public string State;
        }

        private readonly List<Edge> _edges = new List<Edge>();
        private readonly HashSet<string> _loggedExceptions = new HashSet<string>();
        private float _nextHeartbeat;

        private static object Get(object obj, string property) =>
            obj?.GetType().GetProperty(property)?.GetValue(obj);

        private static object Field(object obj, string field) =>
            obj?.GetType().GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(obj);

        private void LogExceptionOnce(string member, Exception ex)
        {
            if (_loggedExceptions.Add(member))
                Debug.LogWarning($"[PinchDiagnostic] read failed once: {member}: {ex.GetType().Name}: {ex.Message}");
        }

        private void Start()
        {
            var rayType = PinchMenuInteraction.FindLoadedType("RayInteractor");
            var ovrType = PinchMenuInteraction.FindLoadedType("OVRHand");
            var ovrHands = new List<Component>();
            if (ovrType != null)
                foreach (var obj in FindObjectsByType(ovrType, FindObjectsSortMode.None))
                    if (obj is Component c) ovrHands.Add(c);

            Debug.Log($"[PinchDiagnostic] logger start: rayType={(rayType != null)} ovrHands={ovrHands.Count}");
            foreach (var c in ovrHands)
            {
                string handType = "?";
                try { handType = Get(c, "HandType")?.ToString() ?? "null"; }
                catch (Exception ex) { LogExceptionOnce("OVRHand.HandType", ex); }
                Debug.Log($"[PinchDiagnostic] OVRHand found: go={c.gameObject.name} HandType={handType}");
            }
            if (rayType == null) return;

            foreach (var obj in FindObjectsByType(rayType, FindObjectsSortMode.None))
            {
                if (obj is not Component interactor) continue;
                string side = SideOf(interactor.transform);
                var selector = Field(interactor, "_selector") as Component;
                object hand = selector != null ? Get(selector, "Hand") : null;
                Component ovr = null;
                foreach (var c in ovrHands)
                    if (SideOf(c.transform) == side) ovr = c;
                _edges.Add(new Edge
                {
                    Interactor = interactor, SelectorHand = hand, OvrHand = ovr, Side = side,
                });
                if (selector != null)
                {
                    try
                    {
                        string capturedSide = side;
                        selector.GetType().GetEvent("WhenSelected")?.AddEventHandler(selector,
                            new Action(() => Debug.Log($"[PinchDiagnostic][{capturedSide}] selector WhenSelected FIRED")));
                        selector.GetType().GetEvent("WhenUnselected")?.AddEventHandler(selector,
                            new Action(() => Debug.Log($"[PinchDiagnostic][{capturedSide}] selector WhenUnselected FIRED")));
                    }
                    catch (Exception ex) { LogExceptionOnce("Selector events", ex); }
                }
                Debug.Log($"[PinchDiagnostic] edge[{side}]: interactor={interactor.gameObject.name} " +
                          $"selector={(selector != null ? selector.gameObject.name : "null")} " +
                          $"iHand={(hand != null ? "bound" : "null")} ovrHand={(ovr != null ? ovr.gameObject.name : "null")}");
            }
            Debug.Log($"[PinchDiagnostic] armed: {_edges.Count} edge(s). Do one pinch-and-release now.");
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
                if (e.OvrHand != null)
                {
                    try
                    {
                        var fingerType = e.OvrHand.GetType().GetNestedType("HandFinger");
                        var index = Enum.Parse(fingerType, "Index");
                        bool pinch = (bool)e.OvrHand.GetType()
                            .GetMethod("GetFingerIsPinching").Invoke(e.OvrHand, new[] { index });
                        if (e.OvrPinch != pinch)
                        {
                            e.OvrPinch = pinch;
                            Debug.Log($"[PinchDiagnostic][{e.Side}] OVR index pinch -> {pinch}");
                        }
                        float strength = (float)e.OvrHand.GetType()
                            .GetMethod("GetFingerPinchStrength").Invoke(e.OvrHand, new[] { index });
                        int band = strength < 0.25f ? 0 : strength < 0.5f ? 1 : strength < 0.75f ? 2 : 3;
                        if (e.StrengthBand != band)
                        {
                            // Log every band change, including the return to 0.
                            Debug.Log($"[PinchDiagnostic][{e.Side}] OVR pinch strength band -> {band} ({strength:F2})");
                            e.StrengthBand = band;
                        }
                        bool tracked = (bool)(Get(e.OvrHand, "IsTracked") ?? false);
                        if (e.OvrTracked != tracked)
                        {
                            e.OvrTracked = tracked;
                            Debug.Log($"[PinchDiagnostic][{e.Side}] OVRHand IsTracked -> {tracked}");
                        }
                    }
                    catch (Exception ex) { LogExceptionOnce("OVRHand reads", ex); }
                }
                if (e.SelectorHand != null)
                {
                    try
                    {
                        bool pinch = (bool)e.SelectorHand.GetType()
                            .GetMethod("GetIndexFingerIsPinching").Invoke(e.SelectorHand, null);
                        if (e.IHandPinch != pinch)
                        {
                            e.IHandPinch = pinch;
                            Debug.Log($"[PinchDiagnostic][{e.Side}] IHand index pinch -> {pinch} (selector event expected)");
                        }
                        bool conn = (bool)(Get(e.SelectorHand, "IsConnected") ?? false);
                        if (e.Connected != conn)
                        {
                            e.Connected = conn;
                            Debug.Log($"[PinchDiagnostic][{e.Side}] IHand connected -> {conn}");
                        }
                    }
                    catch (Exception ex) { LogExceptionOnce("IHand reads", ex); }
                }
                string state = Get(e.Interactor, "State")?.ToString() ?? "?";
                if (e.State != state)
                {
                    e.State = state;
                    Debug.Log($"[PinchDiagnostic][{e.Side}] RayInteractor state -> {state}");
                }
            }
            if (Time.unscaledTime >= _nextHeartbeat)
            {
                _nextHeartbeat = Time.unscaledTime + 5f;
                Debug.Log($"[PinchDiagnostic] heartbeat: edges={_edges.Count} " +
                          $"anyOvrPinch={AnyTrue(_edges, e => e.OvrPinch)} anySelect={AnyState(_edges, "Select")}");
            }
        }

        private static bool AnyTrue(List<Edge> edges, Func<Edge, bool?> pick)
        {
            foreach (var e in edges) if (pick(e) == true) return true;
            return false;
        }

        private static bool AnyState(List<Edge> edges, string state)
        {
            foreach (var e in edges) if (e.State == state) return true;
            return false;
        }
    }
}
