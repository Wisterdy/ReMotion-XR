using UnityEngine;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Deterministic offline demo (Basketball Crossover): dribble with the
    /// right hand, cross the ball over to the left hand in front of a
    /// defender, drive, and finish with a left-hand layup at the hoop.
    /// Paired with BasketballSceneBuilder, which builds the court, hoop,
    /// defender and basketball for any timeline whose action sequence is
    /// basketball/crossover. No server, network, or AI models needed.
    /// </summary>
    public static class DemoTimeline
    {
        public const string SessionId = "basketball_crossover_demo";
        public const string DisplayName = "Basketball Crossover (Demo)";
        public const string FileName = SessionId + ".remotion";

        public static readonly Vector3 HandStart = new Vector3(0.3f, 1.0f, 0.5f);
        public static readonly Vector3 HandEnd = new Vector3(-0.15f, 1.8f, 2.2f);
        public const float ShotReleaseTime = 2.9f;

        public static SessionTimeline Create()
        {
            var timeline = new SessionTimeline
            {
                action_sequence = "basketball_dribble_crossover_layup",
                duration_seconds = 4f,
            };
            const int steps = 40;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps * timeline.duration_seconds;
                Vector3 right, left;
                if (t < 1.2f) // dribble, right hand, moving forward
                {
                    float u = t / 1.2f;
                    right = new Vector3(0.32f, 1.0f + 0.1f * Mathf.Sin(u * 12f),
                        Mathf.Lerp(0.5f, 1.0f, u));
                    left = new Vector3(-0.28f, 1.05f, Mathf.Lerp(0.45f, 0.95f, u));
                }
                else if (t < 1.8f) // crossover: ball sweeps right -> left, low
                {
                    float u = (t - 1.2f) / 0.6f;
                    right = new Vector3(Mathf.Lerp(0.32f, -0.3f, u), 0.82f,
                        Mathf.Lerp(1.0f, 1.3f, u));
                    left = new Vector3(Mathf.Lerp(-0.28f, -0.32f, u), 0.85f,
                        Mathf.Lerp(0.95f, 1.28f, u));
                }
                else // drive + left-hand layup reach
                {
                    float u = (t - 1.8f) / (timeline.duration_seconds - 1.8f);
                    left = new Vector3(Mathf.Lerp(-0.32f, -0.15f, u),
                        Mathf.Lerp(0.85f, 1.8f, Mathf.SmoothStep(0f, 1f, u)),
                        Mathf.Lerp(1.3f, 2.2f, u));
                    right = new Vector3(Mathf.Lerp(-0.3f, 0.25f, u), 0.9f,
                        Mathf.Lerp(1.3f, 1.9f, u));
                }
                timeline.frames.Add(new FrameData
                {
                    timestamp = t,
                    headPosition = new Vector3(0f, 1.6f, Mathf.Lerp(0f, 1.1f, t / timeline.duration_seconds)),
                    headRotation = Quaternion.identity,
                    leftHandJoints = new[] { left },
                    rightHandJoints = new[] { right },
                });
            }
            timeline.grab_events.Add(new GrabEvent { timestamp = 0f, hand = "right", objectId = "ball_01", type = "grab" });
            timeline.grab_events.Add(new GrabEvent { timestamp = 1.35f, hand = "right", objectId = "ball_01", type = "release" });
            timeline.grab_events.Add(new GrabEvent { timestamp = 1.5f, hand = "left", objectId = "ball_01", type = "grab" });
            timeline.grab_events.Add(new GrabEvent { timestamp = ShotReleaseTime, hand = "left", objectId = "ball_01", type = "release" });
            return timeline;
        }
    }
}
