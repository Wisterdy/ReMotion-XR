using System;
using Oculus.Interaction;
using Oculus.Interaction.Input;
using UnityEngine;

namespace ReMotionXR.Core
{
    // One selector feeds the existing Meta ray; no parallel uGUI click dispatcher.
    public sealed class JointPinchSelector : MonoBehaviour, ISelector
    {
        [SerializeField] private UnityEngine.Object _hand;
        [SerializeField] private RayInteractor _ray;
        public IHand Hand => _hand as IHand;
        public event Action WhenSelected = delegate { };
        public event Action WhenUnselected = delegate { };
        private readonly JointPinchLatch _latch = new JointPinchLatch();
        private bool? _valid;
        private bool _focused = true;

        private void OnEnable()
        {
            if (Hand != null) Hand.WhenHandUpdated += Sample;
        }
        private void OnDisable()
        {
            if (Hand != null) Hand.WhenHandUpdated -= Sample;
            Apply(false, 0);
        }
        private void OnApplicationFocus(bool focus) { _focused = focus; if (!focus) Apply(false, 0); }
        private void Update() { Sample(); }

        private void Sample()
        {
            if (!_focused) { Apply(false, 0); return; }
            var hand = Hand;
            if (hand == null || !hand.IsConnected || !hand.IsTrackedDataValid ||
                !hand.GetJointPose(HandJointId.HandThumbTip, out var thumb) ||
                !hand.GetJointPose(HandJointId.HandIndexTip, out var index))
            { Apply(false, 0); return; }
            Apply(true, Vector3.Distance(thumb.position, index.position));
        }

        private void Apply(bool valid, float distance)
        {
            if (_valid != valid)
            {
                _valid = valid;
                Debug.Log($"[ReMotionJointPinch] {Hand?.Handedness} jointsValid={valid}");
            }
            var edge = _latch.Sample(valid, distance);
            if (edge == JointPinchLatch.Edge.None) return;
            // Cancel BEFORE releasing the selector. Meta's canvas clears eligibility
            // on Cancel; a tracking dropout must never become an onClick.
            if (edge == JointPinchLatch.Edge.Cancel && _ray != null)
            {
                var target = _ray.Interactable;
                if (target != null)
                    target.PublishPointerEvent(new PointerEvent(_ray.Identifier,
                        PointerEventType.Cancel, Pose.identity));
                _ray.Disable();
            }
            Debug.Log($"[ReMotionJointPinch] {Hand?.Handedness} {edge} distanceMm={distance * 1000:F0}");
            if (edge == JointPinchLatch.Edge.Select) WhenSelected();
            else WhenUnselected();
        }
    }

    public sealed class JointPinchLatch
    {
        public enum Edge { None, Select, Release, Cancel }
        private bool _armed, _selected;
        public Edge Sample(bool valid, float distance)
        {
            if (!valid || float.IsNaN(distance) || float.IsInfinity(distance) || distance < 0)
            {
                bool wasSelected = _selected;
                _armed = _selected = false;
                return wasSelected ? Edge.Cancel : Edge.None;
            }
            if (!_armed)
            {
                if (distance >= 0.040f) _armed = true;
                return Edge.None;
            }
            if (!_selected && distance <= 0.025f)
            { _selected = true; return Edge.Select; }
            if (_selected && distance >= 0.040f)
            { _selected = false; return Edge.Release; }
            return Edge.None;
        }
    }
}
