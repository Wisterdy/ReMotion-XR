using System.Collections.Generic;
using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Replays a recorded session: ghost hands/objects follow the timeline,
    /// objects parent to the hand on GrabEvent. Controls: Play/Pause, scrub,
    /// speed 0.25/0.5/1x, Next/Prev Key Pose, Loop Segment.
    /// Placeholder cubes/avatar stand in until final SAM 3D models land.
    /// </summary>
    public class ReplayPlayer : MonoBehaviour
    {
        public Transform leftHandGhost;
        public Transform rightHandGhost;
        public Transform grabbedObject; // placeholder cube until SAM 3D model loads
        public string grabbedObjectId;
        public Transform sceneRoot; // anchored scene space (release parenting)
        public KeyPoseManager keyPoseManager;

        public bool IsPlaying { get; private set; }
        public float CurrentTime { get; private set; }
        public float PlaybackSpeed { get; private set; } = 1f;

        private SessionTimeline _timeline;
        private Transform _heldBy;
        private bool _released;
        private int _keyPoseIndex = -1;
        private float _loopStart = -1f, _loopEnd = -1f;
        private readonly HashSet<int> _appliedGrabs = new HashSet<int>();

        private static readonly float[] Speeds = { 0.25f, 0.5f, 1f };

        public void Load(SessionTimeline timeline)
        {
            _timeline = timeline;
            CurrentTime = 0f;
            _heldBy = null;
            _released = false;
            _appliedGrabs.Clear();
            keyPoseManager?.Extract(timeline);
            ApplyFrame(0f);
        }

        public void Play() => IsPlaying = true;
        public void Pause() => IsPlaying = false;

        public void SetSpeed(int index) =>
            PlaybackSpeed = Speeds[Mathf.Clamp(index, 0, Speeds.Length - 1)];

        public void Scrub(float time)
        {
            CurrentTime = Mathf.Clamp(time, 0f, Duration);
            _heldBy = null;
            _released = false;
            _appliedGrabs.Clear();
            ApplyFrame(CurrentTime);
        }

        public void NextKeyPose() => JumpToKeyPose(1);
        public void PrevKeyPose() => JumpToKeyPose(-1);

        /// <summary>Loop between two key poses (or clear with start=end=-1).</summary>
        public void SetLoopSegment(float start, float end) { _loopStart = start; _loopEnd = end; }

        public float Duration => _timeline != null ? _timeline.duration_seconds : 0f;

        /// <summary>The loaded reference timeline (retraining compares against it).</summary>
        public SessionTimeline Timeline => _timeline;

        /// <summary>Interpolated reference frame at time t (same state seek/play produce).</summary>
        public FrameData FrameAt(float time) =>
            _timeline == null || _timeline.frames.Count == 0 ? null : NearestFrame(time);

        private void Update()
        {
            if (!IsPlaying || _timeline == null) return;
            CurrentTime += Time.deltaTime * PlaybackSpeed;
            if (_loopStart >= 0f && CurrentTime >= _loopEnd) CurrentTime = _loopStart;
            if (CurrentTime >= Duration) { CurrentTime = Duration; Pause(); }
            ApplyFrame(CurrentTime);
        }

        private void JumpToKeyPose(int dir)
        {
            var poses = keyPoseManager != null ? keyPoseManager.KeyPoses : null;
            if (poses == null || poses.Count == 0) return;
            _keyPoseIndex = Mathf.Clamp(_keyPoseIndex + dir, 0, poses.Count - 1);
            Scrub(poses[_keyPoseIndex].timestamp);
        }

        private void ApplyFrame(float time)
        {
            if (_timeline == null || _timeline.frames.Count == 0) return;
            FrameData frame = NearestFrame(time);
            if (rightHandGhost != null && frame.rightHandJoints.Length > 0)
            {
                rightHandGhost.localPosition = frame.rightHandJoints[0];
                rightHandGhost.GetComponent<HandGhostVisual>()?.SetPose(frame.rightHandJoints, frame.rightHandRotations);
            }
            if (leftHandGhost != null && frame.leftHandJoints.Length > 0)
            {
                leftHandGhost.localPosition = frame.leftHandJoints[0];
                leftHandGhost.GetComponent<HandGhostVisual>()?.SetPose(frame.leftHandJoints, frame.leftHandRotations);
            }
            ApplyGrabEvents(time);
            // The object rides in the holding hand; after release it
            // stays where the recording left it (in anchored scene
            // space) so the user can try the action again against it.
            bool inFlight = false;
            if (grabbedObject != null && _heldBy != null)
                grabbedObject.position = _heldBy.position + new Vector3(0f, -0.16f, 0.06f);
            if (grabbedObject != null &&
                BasketballSceneBuilder.TryBallFlightPosition(_timeline, time, out Vector3 flight))
            { grabbedObject.position = flight; inFlight = true; }
            // No grab evidence: the object stays at its recorded scene
            // position. It is never attached to a hand merely for being first.

        }

        private void ApplyGrabEvents(float time)
        {
            if (grabbedObject == null) return;
            for (int i = 0; i < _timeline.grab_events.Count; i++)
            {
                var g = _timeline.grab_events[i];
                if (!string.IsNullOrEmpty(grabbedObjectId) && g.objectId != grabbedObjectId) continue;
                if (g.timestamp > time || _appliedGrabs.Contains(i)) continue;
                _appliedGrabs.Add(i);
                Transform hand = g.hand == "left" ? leftHandGhost : rightHandGhost;
                if (g.type == "grab" && hand != null)
                { grabbedObject.SetParent(hand, true); _heldBy = hand; _released = false; }
                else if (g.type == "release")
                {
                    // Release into anchored scene space, not the player
                    // root — otherwise the object jumps out of the
                    // viewer-anchored scene on every release.
                    grabbedObject.SetParent(sceneRoot != null ? sceneRoot : transform, true);
                    _heldBy = null; _released = true;
                }
            }
        }

        /// <summary>Bracketing-frame interpolation: seek to T and play to
        /// T produce the same hand state. Untracked hands stay hidden by
        /// their empty joint arrays, never fabricated.</summary>
        private FrameData NearestFrame(float time)
        {
            var frames = _timeline.frames;
            if (time <= frames[0].timestamp) return frames[0];
            for (int i = 1; i < frames.Count; i++)
            {
                if (frames[i].timestamp < time) continue;
                var a = frames[i - 1];
                var b = frames[i];
                float span = b.timestamp - a.timestamp;
                float f = span > 1e-6f ? Mathf.Clamp01((time - a.timestamp) / span) : 0f;
                return new FrameData
                {
                    timestamp = time,
                    headPosition = Vector3.Lerp(a.headPosition, b.headPosition, f),
                    headRotation = Quaternion.Slerp(a.headRotation, b.headRotation, f),
                    leftHandJoints = LerpJoints(a.leftHandJoints, b.leftHandJoints, f),
                    rightHandJoints = LerpJoints(a.rightHandJoints, b.rightHandJoints, f),
                    leftHandRotations = a.leftHandRotations,
                    rightHandRotations = a.rightHandRotations,
                    leftTracked = a.leftTracked, rightTracked = a.rightTracked,
                };
            }
            return frames[frames.Count - 1];
        }

        private static Vector3[] LerpJoints(Vector3[] a, Vector3[] b, float f)
        {
            if (a == null || a.Length == 0) return a ?? System.Array.Empty<Vector3>();
            if (b == null || b.Length != a.Length) return a;
            var outp = new Vector3[a.Length];
            for (int i = 0; i < a.Length; i++) outp[i] = Vector3.Lerp(a[i], b[i], f);
            return outp;
        }
    }
}
