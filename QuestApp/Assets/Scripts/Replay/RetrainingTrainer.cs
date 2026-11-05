using System;
using UnityEngine;
using ReMotionXR.Capture;
using ReMotionXR.Core;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Retrain practice: the recorded timeline is the reference. While
    /// active, the user's live hand (sampled through HandPoseRecorder,
    /// the same XR Hands source as capture) is compared joint-by-joint
    /// with the recorded hand at the player's current time, and the
    /// user gets a meaningful score + instruction — not just a boolean.
    /// </summary>
    public class RetrainingTrainer : MonoBehaviour
    {
        public ReplayPlayer player;
        public HandPoseRecorder liveRecorder;

        public bool Active { get; private set; }
        public float LastScore01 { get; private set; }
        public float LastMeanErrorMeters { get; private set; } = -1f;
        public string LastFeedback { get; private set; } = "";
        public event Action<string, float> OnFeedback;

        public void SetActive(bool on)
        {
            Active = on && player != null;
            if (!Active) LastFeedback = "";
            else LastFeedback = "Match the ghost hand: follow it through the movement.";
        }

        private void Update()
        {
            if (!Active || player == null || liveRecorder == null) return;
            if (!player.IsPlaying) return;
            var live = liveRecorder.SampleNow(player.CurrentTime, null);
            Evaluate(live, player.FrameAt(player.CurrentTime));
        }

        /// <summary>Compares live vs reference joints; returns score 0..1.</summary>
        public float Evaluate(FrameData live, FrameData reference)
        {
            if (live == null || reference == null)
            {
                LastFeedback = "No recorded reference at this moment.";
                return 0f;
            }
            float total = 0f;
            int count = 0;
            AddHand(live.rightHandJoints, reference.rightHandJoints, ref total, ref count);
            AddHand(live.leftHandJoints, reference.leftHandJoints, ref total, ref count);
            if (count == 0)
            {
                LastMeanErrorMeters = -1f;
                LastFeedback = "Hand not tracked — raise your hand where the ghost is.";
                OnFeedback?.Invoke(LastFeedback, 0f);
                return 0f;
            }
            float mean = total / count;
            LastMeanErrorMeters = mean;
            // 2cm = perfect, 25cm+ = no credit; linear in between.
            LastScore01 = Mathf.Clamp01(1f - (mean - 0.02f) / 0.23f);
            LastFeedback = mean < 0.05f
                ? "Excellent — your hand matches the recording."
                : mean < 0.12f
                    ? "Close — small adjustments to match the ghost hand."
                    : "Follow the ghost hand through the movement.";
            OnFeedback?.Invoke(LastFeedback, LastScore01);
            return LastScore01;
        }

        private static void AddHand(Vector3[] live, Vector3[] reference,
                                    ref float total, ref int count)
        {
            if (live == null || reference == null) return;
            int n = Mathf.Min(live.Length, reference.Length);
            for (int i = 0; i < n; i++)
            {
                total += Vector3.Distance(live[i], reference[i]);
                count++;
            }
        }
    }
}
