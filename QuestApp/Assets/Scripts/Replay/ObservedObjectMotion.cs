using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Replay
{
    /// <summary>Reuse one tool mesh along observed video motion; no fabricated grabs.</summary>
    public sealed class ObservedObjectMotion : MonoBehaviour
    {
        private ScenePackage.ObjectPose[] poses = System.Array.Empty<ScenePackage.ObjectPose>();
        private ReplayPlayer player;
        public IEnumerable<float> Timestamps => poses.Select(p => p.timestamp);
        public void Configure(ScenePackage.ObjectPose[] values, ReplayPlayer clock)
        {
            poses = (values ?? System.Array.Empty<ScenePackage.ObjectPose>())
                .Where(p => p != null && !float.IsNaN(p.timestamp) && !float.IsInfinity(p.timestamp)
                    && IsFinite(p.position)).OrderBy(p => p.timestamp).ToArray();
            player = clock;
        }
        private static bool IsFinite(Vector3 p) => !float.IsNaN(p.x+p.y+p.z) && !float.IsInfinity(p.x+p.y+p.z);
        private void LateUpdate() { if(player != null) Apply(player.CurrentTime); }
        public void Apply(float time)
        {
            if (poses.Length < 2) return;
            // Large tracking gaps hold the last observation; do not invent a trajectory.
            var a=poses[0]; var b=a;
            foreach(var pose in poses) { if(pose.timestamp <= time) a=pose; else { b=pose; break; } b=a; }
            float span=b.timestamp-a.timestamp;
            transform.localPosition=span>0 && span<=1.5f
                ? Vector3.Lerp(a.position,b.position,Mathf.Clamp01((time-a.timestamp)/span)) : a.position;
        }
    }
}
