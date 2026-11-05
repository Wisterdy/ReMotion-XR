using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Builds the actual basketball environment for basketball/crossover
    /// timelines: court floor with lines, hoop with backboard and rim, an
    /// opponent defender, and an orange basketball sphere. Also computes
    /// the shot flight after the final release so the ball arcs to the rim
    /// instead of freezing where the hand let go.
    /// </summary>
    public static class BasketballSceneBuilder
    {
        public static readonly Vector3 HoopPosition = new Vector3(0f, 3.05f, 3.4f);
        public const float FlightSeconds = 0.8f;

        public static bool IsBasketball(SessionTimeline timeline)
        {
            string a = timeline != null ? (timeline.action_sequence ?? "").ToLowerInvariant() : "";
            return a.Contains("basketball") || a.Contains("crossover");
        }

        public static bool IsBasketballSession(string sessionId, SessionTimeline timeline) =>
            IsBasketball(timeline) ||
            (sessionId ?? "").ToLowerInvariant().Contains("basketball");

        /// <summary>Builds (once) and returns the BasketballCourt root.</summary>
        public static Transform Build(Transform sceneRoot, Transform ball,
                                      Transform leftHand = null, Transform rightHand = null)
        {
            StyleBall(ball);
            StyleHand(leftHand); StyleHand(rightHand);
            var existing = sceneRoot.Find("BasketballCourt");
            if (existing != null) { existing.gameObject.SetActive(true); return existing; }

            var court = new GameObject("BasketballCourt");
            court.transform.SetParent(sceneRoot, false);

            Box(court.transform, "CourtFloor", new Vector3(0f, -0.03f, 2f),
                new Vector3(7f, 0.06f, 8f), new Color(0.72f, 0.48f, 0.27f));
            // Court lines: boundary + key + centre line (thin white boxes).
            Box(court.transform, "LineNear", new Vector3(0f, 0.01f, -1.9f), new Vector3(7f, 0.01f, 0.08f), Color.white);
            Box(court.transform, "LineFar", new Vector3(0f, 0.01f, 5.9f), new Vector3(7f, 0.01f, 0.08f), Color.white);
            Box(court.transform, "LineLeft", new Vector3(-3.45f, 0.01f, 2f), new Vector3(0.08f, 0.01f, 7.9f), Color.white);
            Box(court.transform, "LineRight", new Vector3(3.45f, 0.01f, 2f), new Vector3(0.08f, 0.01f, 7.9f), Color.white);
            Box(court.transform, "KeyLine", new Vector3(0f, 0.01f, 2.2f), new Vector3(1.8f, 0.01f, 0.08f), Color.white);

            var hoop = new GameObject("Hoop");
            hoop.transform.SetParent(court.transform, false);
            Cylinder(hoop.transform, "Pole", new Vector3(0f, 1.9f, 3.9f),
                new Vector3(0.18f, 3.8f, 0.18f), new Color(0.25f, 0.27f, 0.3f));
            Box(hoop.transform, "Backboard", new Vector3(0f, 3.45f, 3.68f),
                new Vector3(1.8f, 1.05f, 0.06f), new Color(0.92f, 0.94f, 0.96f));
            Box(hoop.transform, "BoardSquare", new Vector3(0f, 3.3f, 3.64f),
                new Vector3(0.6f, 0.45f, 0.02f), new Color(0.95f, 0.4f, 0.1f));
            Cylinder(hoop.transform, "Rim", HoopPosition,
                new Vector3(0.46f, 0.04f, 0.46f), new Color(0.95f, 0.4f, 0.1f));
            Cylinder(hoop.transform, "Net", HoopPosition + new Vector3(0f, -0.2f, 0f),
                new Vector3(0.36f, 0.4f, 0.36f), new Color(0.9f, 0.9f, 0.9f));

            var defender = new GameObject("Defender");
            defender.transform.SetParent(court.transform, false);
            defender.transform.localPosition = new Vector3(0.45f, 0f, 1.7f);
            Prim(defender.transform, PrimitiveType.Capsule, "DefenderBody",
                new Vector3(0f, 0.95f, 0f), new Vector3(0.46f, 0.95f, 0.46f),
                new Color(0.95f, 0.8f, 0.15f)); // yellow = Person (accessibility standard)
            Prim(defender.transform, PrimitiveType.Sphere, "DefenderHead",
                new Vector3(0f, 1.78f, 0f), Vector3.one * 0.26f, new Color(0.9f, 0.65f, 0.45f));
            Box(defender.transform, "DefenderArmL", new Vector3(-0.42f, 1.35f, 0f),
                new Vector3(0.5f, 0.09f, 0.09f), new Color(0.9f, 0.65f, 0.45f));
            Box(defender.transform, "DefenderArmR", new Vector3(0.42f, 1.35f, 0f),
                new Vector3(0.5f, 0.09f, 0.09f), new Color(0.9f, 0.65f, 0.45f));
            return court.transform;
        }

        /// <summary>
        /// After the last release, the ball arcs from the releasing hand to
        /// the rim over FlightSeconds, then drops through the net. Returns
        /// false while the ball is still held / before the shot.
        /// </summary>
        public static bool TryBallFlightPosition(SessionTimeline timeline, float time, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (timeline == null || !IsBasketball(timeline)) return false;
            GrabEvent shot = null;
            foreach (var g in timeline.grab_events)
                if (g.type == "release" && (shot == null || g.timestamp > shot.timestamp)) shot = g;
            if (shot == null || time < shot.timestamp) return false;
            Vector3 from = HandAt(timeline, shot);
            float u = Mathf.Clamp01((time - shot.timestamp) / FlightSeconds);
            pos = Vector3.Lerp(from, HoopPosition, u);
            pos.y += Mathf.Sin(u * Mathf.PI) * 0.55f;
            if (u >= 1f) // through the net
                pos = HoopPosition + Vector3.down * Mathf.Min(0.75f, (time - shot.timestamp - FlightSeconds) * 1.8f);
            return true;
        }

        private static Vector3 HandAt(SessionTimeline timeline, GrabEvent g)
        {
            FrameData best = timeline.frames[0]; float bd = float.MaxValue;
            foreach (var f in timeline.frames)
            { float d = Mathf.Abs(f.timestamp - g.timestamp); if (d < bd) { bd = d; best = f; } }
            var joints = g.hand == "left" ? best.leftHandJoints : best.rightHandJoints;
            return joints != null && joints.Length > 0 ? joints[0] : best.headPosition;
        }

        private static void StyleBall(Transform ball)
        {
            if (ball == null) return;
            ball.name = "Basketball";
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying) Object.Destroy(temp); else Object.DestroyImmediate(temp);
            ball.GetComponent<MeshFilter>().mesh = mesh;
            ball.localScale = Vector3.one * 0.24f; // real basketball diameter
            SetColor(ball, new Color(0.95f, 0.45f, 0.1f));
        }

        private static void StyleHand(Transform hand)
        {
            if (hand == null) return;
            var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            var mesh = temp.GetComponent<MeshFilter>().sharedMesh;
            if (Application.isPlaying) Object.Destroy(temp); else Object.DestroyImmediate(temp);
            var mf = hand.GetComponent<MeshFilter>(); if (mf != null) mf.mesh = mesh;
            SetColor(hand, new Color(0.9f, 0.65f, 0.45f));
        }

        private static void SetColor(Transform t, Color color) =>
            SceneMaterials.ApplyColor(t != null ? t.GetComponent<Renderer>() : null, color);

        private static void Box(Transform p, string n, Vector3 pos, Vector3 scale, Color c) =>
            Prim(p, PrimitiveType.Cube, n, pos, scale, c);

        private static void Cylinder(Transform p, string n, Vector3 pos, Vector3 scale, Color c) =>
            Prim(p, PrimitiveType.Cylinder, n, pos, scale, c);

        private static void Prim(Transform parent, PrimitiveType type, string name,
                                 Vector3 pos, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var col = go.GetComponent<Collider>();
            if (col != null) { if (Application.isPlaying) Object.Destroy(col); else Object.DestroyImmediate(col); }
            SetColor(go.transform, color);
        }
    }
}
