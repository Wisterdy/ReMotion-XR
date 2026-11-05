using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using ReMotionXR.Core;

namespace ReMotionXR.Capture
{
    /// <summary>
    /// Samples hand/head pose at an interval from Unity XR Hands
    /// (OpenXR joint data on Quest). Previously this returned a fixed
    /// stub skeleton on device too, so real captures replayed as static
    /// hands and "nothing from the previous recording" was visible.
    /// The stub remains only as the no-subsystem fallback (Editor/tests).
    /// Joint order is XRHandJointID BeginMarker..EndMarker; use
    /// JointIndexOf to locate tips.
    /// </summary>
    public class HandPoseRecorder : MonoBehaviour
    {
        [Tooltip("Samples per second while recording.")]
        public float sampleInterval = 0.1f;

        private XRHandSubsystem _xrHands;

        public static int JointIndexOf(XRHandJointID id)
        {
            int index = 0;
            for (var joint = XRHandJointID.BeginMarker; joint <= XRHandJointID.EndMarker; joint++)
            {
                if (joint == id) return index;
                index++;
            }
            return -1;
        }

        /// <summary>Read the current pose (real XR joints when tracked, else stub).</summary>
        public FrameData SampleNow(float timestamp, Transform head)
        {
            var frame = new FrameData { timestamp = timestamp };
            if (head != null)
            {
                frame.headPosition = head.position;
                frame.headRotation = head.rotation;
                frame.headTracked = true;
            }
            if (TrySampleXrHands(out Vector3[] left, out Vector3[] right,
                                 out Quaternion[] leftRot, out Quaternion[] rightRot))
            {
                // Missing tracking is recorded as missing (empty +
                // untracked), never as fabricated joint positions.
                frame.leftTracked = left != null;
                frame.rightTracked = right != null;
                frame.leftHandJoints = left ?? System.Array.Empty<Vector3>();
                frame.rightHandJoints = right ?? System.Array.Empty<Vector3>();
                frame.leftHandRotations = leftRot ?? System.Array.Empty<Quaternion>();
                frame.rightHandRotations = rightRot ?? System.Array.Empty<Quaternion>();
            }
            else
            {
#if UNITY_EDITOR
                // Editor/tests only: no XR subsystem exists off-device.
                frame.leftHandJoints = SampleHandsStub(-0.2f);
                frame.rightHandJoints = SampleHandsStub(0.2f);
#endif
                // On device: hands stay empty/untracked until XR reports them.
            }
            return frame;
        }

        private bool TrySampleXrHands(out Vector3[] left, out Vector3[] right,
                                      out Quaternion[] leftRot, out Quaternion[] rightRot)
        {
            left = null; right = null; leftRot = null; rightRot = null;
            if (_xrHands == null || !_xrHands.running)
            {
                var subsystems = new List<XRHandSubsystem>();
                SubsystemManager.GetSubsystems(subsystems);
                foreach (var s in subsystems)
                    if (s.running) { _xrHands = s; break; }
                if (_xrHands == null) return false;
            }
            left = SampleHand(_xrHands.leftHand, out leftRot);
            right = SampleHand(_xrHands.rightHand, out rightRot);
            return left != null || right != null;
        }

        private static Vector3[] SampleHand(XRHand hand, out Quaternion[] rotations)
        {
            rotations = null;
            if (!hand.isTracked) return null;
            var joints = new List<Vector3>();
            var quats = new List<Quaternion>();
            Pose fallback = Pose.identity;
            bool hasFallback = hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wrist)
                               || hand.GetJoint(XRHandJointID.Palm).TryGetPose(out wrist);
            if (hasFallback) fallback = wrist;
            for (var id = XRHandJointID.BeginMarker; id <= XRHandJointID.EndMarker; id++)
            {
                var joint = hand.GetJoint(id);
                bool ok = joint.TryGetPose(out Pose pose);
                joints.Add(ok ? pose.position : fallback.position);
                quats.Add(ok ? pose.rotation : fallback.rotation);
            }
            rotations = quats.ToArray();
            return joints.ToArray();
        }

        /// <summary>Thumb-tip to index-tip distance from a sampled joint array (MaxValue when unavailable).</summary>
        public static float TipDistance(Vector3[] joints)
        {
            if (joints == null) return float.MaxValue;
            int index = JointIndexOf(XRHandJointID.IndexTip);
            int thumb = JointIndexOf(XRHandJointID.ThumbTip);
            if (index < 0 || thumb < 0 || index >= joints.Length || thumb >= joints.Length)
                return float.MaxValue;
            return Vector3.Distance(joints[index], joints[thumb]);
        }

        // Fallback skeleton (Editor/tests, or hand tracking unavailable).
        private static Vector3[] SampleHandsStub(float xOffset)
        {
            var joints = new Vector3[3];
            for (int i = 0; i < joints.Length; i++)
                joints[i] = new Vector3(xOffset, -0.3f, 0.4f + i * 0.05f);
            return joints;
        }
    }
}
