using System.Collections.Generic;
using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Adaptive key poses. A pose is kept when the hand trajectory triggers:
    /// direction change >45deg, contact, velocity pause, joint extreme, or
    /// grab/release. Simple actions yield ~2 poses; complex sequences
    /// (dribble->crossover->layup) yield 8-15. Server extracts the same way;
    /// this client copy supports offline/demo timelines.
    /// </summary>
    public class KeyPoseManager : MonoBehaviour
    {
        public float directionChangeDegrees = 45f;
        public List<KeyPose> KeyPoses { get; private set; } = new List<KeyPose>();

        public List<KeyPose> Extract(SessionTimeline timeline)
        {
            KeyPoses.Clear();
            var frames = timeline.frames;
            if (frames.Count == 0) return KeyPoses;

            AddPose(timeline, frames[0], "start", "preparation", "Start Pose");
            Vector3 prevDir = Vector3.forward;
            float prevSpeed = 0f;
            for (int i = 1; i < frames.Count; i++)
            {
                Vector3 a = frames[i - 1].headPosition, b = frames[i].headPosition;
                Vector3 dir = (b - a);
                float speed = dir.magnitude;
                dir = speed > 0.0001f ? dir / speed : prevDir;
                bool sharpTurn = Vector3.Angle(prevDir, dir) > directionChangeDegrees;
                bool velocityPause = prevSpeed > 0.01f && speed <= 0.001f;
                if (sharpTurn || velocityPause)
                    AddPose(timeline, frames[i], "move",
                        sharpTurn ? "contact" : "preparation",
                        sharpTurn ? "Direction Change" : "Pause Pose");
                prevDir = dir;
                prevSpeed = speed;
            }
            foreach (var g in timeline.grab_events)
            {
                var f = NearestFrame(timeline, g.timestamp);
                if (f != null) AddPose(timeline, f, "interaction", "contact",
                    g.type == "grab" ? "Grab" : "Release");
            }
            if (frames.Count > 1)
                AddPose(timeline, frames[frames.Count - 1], "end", "peak", "Final Pose");
            return KeyPoses;
        }

        public void UseObservedTimes(IEnumerable<float> timestamps)
        {
            KeyPoses.Clear();
            foreach (float t in timestamps) KeyPoses.Add(new KeyPose { id=KeyPoses.Count+1,
                timestamp=t, label="Object moment " + (KeyPoses.Count+1), segment="observed_tool_motion", phase="contact" });
        }

        private void AddPose(SessionTimeline t, FrameData f, string segment, string phase, string label)
        {
            KeyPoses.Add(new KeyPose
            {
                id = KeyPoses.Count + 1, timestamp = f.timestamp,
                segment = segment, phase = phase, label = label,
                isBestPose = phase == "peak" && f == t.frames[t.frames.Count - 1],
            });
        }

        private static FrameData NearestFrame(SessionTimeline t, float ts)
        {
            FrameData best = null; float bestD = float.MaxValue;
            foreach (var f in t.frames)
            {
                float d = Mathf.Abs(f.timestamp - ts);
                if (d < bestD) { bestD = d; best = f; }
            }
            return best;
        }
    }
}
