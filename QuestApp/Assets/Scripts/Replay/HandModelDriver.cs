using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using ReMotionXR.Capture;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Poses a real skinned hand model (the Meta OpenXR hand meshes,
    /// bones named XRHand_&lt;Joint&gt;) from recorded XR joint positions.
    /// This replaces the sphere-per-joint ghost: the replay shows the
    /// same blue hand model the wearer sees live, with the recorded
    /// finger motion. Bones are set in world space, parents first:
    /// position on the joint, then aim at the child joint.
    /// </summary>
    public class HandModelDriver : MonoBehaviour
    {
        // Unity XR Hands joint +Z follows the finger; this bundled Meta FBX
        // uses -Z along the finger and the opposite local X. Convert the
        // joint basis, not its captured position or motion (both hands).
        private static readonly Quaternion JointToMeshBasis = Quaternion.Euler(0f, 180f, 0f);
        private class Bone
        {
            public Transform Transform;
            public XRHandJointID Joint;
            public XRHandJointID ChildJoint; // joint this bone aims at
            public Vector3 LocalChildDir;    // bind-pose dir to child, bone-local
            public bool HasChild;
        }

        private readonly List<Bone> _bones = new List<Bone>();
        private readonly Dictionary<XRHandJointID, Bone> _byJoint = new Dictionary<XRHandJointID, Bone>();

        public bool IsReady => _bones.Count > 0;
        public int BoneCount => _bones.Count;

        // Finger chains in XRHandJointID order (parents before children).
        private static readonly XRHandJointID[][] Chains =
        {
            new[] { XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal, XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip },
            new[] { XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal, XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip },
            new[] { XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal, XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
            new[] { XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal, XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip },
            new[] { XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal, XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip },
        };

        /// <summary>Build the bone map from an instantiated hand model.</summary>
        public void Bind(GameObject modelRoot)
        {
            _bones.Clear();
            _byJoint.Clear();
            if (modelRoot == null) return;
            var childOf = new Dictionary<XRHandJointID, XRHandJointID>();
            foreach (var chain in Chains)
                for (int i = 0; i + 1 < chain.Length; i++)
                    childOf[chain[i]] = chain[i + 1];

            foreach (var t in modelRoot.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("XRHand_")) continue;
                string jointName = t.name.Substring("XRHand_".Length);
                if (!System.Enum.TryParse(jointName, out XRHandJointID joint)) continue;
                var bone = new Bone { Transform = t, Joint = joint };
                if (childOf.TryGetValue(joint, out var child)) { bone.ChildJoint = child; bone.HasChild = true; }
                _bones.Add(bone);
                _byJoint[joint] = bone;
            }
            // Bind-pose aim directions, in each bone's local space.
            foreach (var bone in _bones)
            {
                if (!bone.HasChild) continue;
                if (_byJoint.TryGetValue(bone.ChildJoint, out var childBone))
                {
                    Vector3 worldDir = childBone.Transform.position - bone.Transform.position;
                    if (worldDir.sqrMagnitude > 1e-8f)
                        bone.LocalChildDir = bone.Transform.InverseTransformDirection(worldDir.normalized);
                }
            }
            // Parents first so child positions land after parent rotation.
            _bones.Sort((a, b) => Depth(a.Transform).CompareTo(Depth(b.Transform)));
        }

        private static int Depth(Transform t)
        {
            int d = 0;
            for (var p = t.parent; p != null; p = p.parent) d++;
            return d;
        }

        /// <summary>
        /// Pose the model from recorded joints (recording world space,
        /// converted to world by frameRoot, the anchored scene root).
        /// When the recording carries joint rotations (captured from the
        /// same tracking poses that drive the live blue hand prefab),
        /// bones get exact position + orientation — no re-animation.
        /// Older position-only recordings fall back to aiming each bone
        /// at its child joint. Returns false when data can't drive it.
        /// </summary>
        public bool Pose(Vector3[] joints, Quaternion[] rotations, Transform frameRoot)
        {
            if (!IsReady || joints == null || joints.Length == 0 || frameRoot == null) return false;
            bool exact = rotations != null && rotations.Length >= joints.Length;
            foreach (var bone in _bones)
            {
                int index = HandPoseRecorder.JointIndexOf(bone.Joint);
                if (index < 0 || index >= joints.Length) continue;
                bone.Transform.position = frameRoot.TransformPoint(joints[index]);
                if (exact)
                {
                    bone.Transform.rotation = frameRoot.rotation * rotations[index] * JointToMeshBasis;
                    continue;
                }
                if (!bone.HasChild || bone.LocalChildDir == Vector3.zero) continue;
                int childIndex = HandPoseRecorder.JointIndexOf(bone.ChildJoint);
                if (childIndex < 0 || childIndex >= joints.Length) continue;
                Vector3 targetDir = frameRoot.TransformPoint(joints[childIndex]) - bone.Transform.position;
                if (targetDir.sqrMagnitude < 1e-8f) continue;
                Vector3 currentDir = bone.Transform.TransformDirection(bone.LocalChildDir);
                bone.Transform.rotation =
                    Quaternion.FromToRotation(currentDir, targetDir.normalized) * bone.Transform.rotation;
            }
            return true;
        }
    }
}
