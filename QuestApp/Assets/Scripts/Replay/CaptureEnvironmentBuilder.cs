using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Generic replay environment for any free-form capture (no predefined
    /// sport or lesson types): a floor, a yellow Person the wearer faced, a
    /// blue Object in hand, and a green Target — the accessibility colour
    /// standard (Blue = Object, Yellow = Person, Green = Target). The one
    /// bundled sample whose recorded action is a basketball crossover gets
    /// its court from BasketballSceneBuilder instead.
    /// </summary>
    public static class CaptureEnvironmentBuilder
    {
        public static readonly Vector3 TargetPosition = new Vector3(0f, 1.1f, 3.0f);

        public static Transform Build(Transform sceneRoot, SessionTimeline timeline, Transform ball,
                                      Transform leftHand = null, Transform rightHand = null,
                                      Mesh personMesh = null, Color personColor = default)
        {
            if (BasketballSceneBuilder.IsBasketball(timeline))
                return BasketballSceneBuilder.Build(sceneRoot, ball, leftHand, rightHand);

            var oldBasketball = sceneRoot.Find("BasketballCourt");
            if (oldBasketball != null) oldBasketball.gameObject.SetActive(false);
            var existing = sceneRoot.Find("CaptureStage");
            if (existing != null)
            {
                existing.gameObject.SetActive(true);
                StyleObject(ball);
                if (personMesh != null) ApplyPersonMesh(existing, personMesh, personColor);
                return existing;
            }

            var stage = new GameObject("CaptureStage");
            stage.transform.SetParent(sceneRoot, false);
            Prim(stage.transform, PrimitiveType.Cube, "StageFloor",
                new Vector3(0f, -0.03f, 1.5f), new Vector3(5f, 0.06f, 6f),
                new Color(0.16f, 0.2f, 0.26f));

            var person = new GameObject("Person");
            person.transform.SetParent(stage.transform, false);
            // Face the wearer where they actually stood: first recorded
            // head position + facing, instead of a fixed stage spot.
            Vector3 personPos = new Vector3(0.55f, 0f, 1.9f);
            if (timeline != null && timeline.frames.Count > 0)
            {
                var first = timeline.frames[0];
                Vector3 forward = first.headRotation * Vector3.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 0.001f)
                    personPos = new Vector3(first.headPosition.x, 0f, first.headPosition.z)
                                + forward.normalized * 1.9f;
            }
            person.transform.localPosition = personPos;
            if (personMesh != null)
            {
                // SAM-built person model replaces the capsule placeholder.
                var meshGo = new GameObject("PersonSamMesh");
                meshGo.transform.SetParent(person.transform, false);
                meshGo.AddComponent<MeshFilter>().sharedMesh = personMesh;
                var renderer = meshGo.AddComponent<MeshRenderer>();
                SceneMaterials.ApplyColor(renderer, personColor == default ? Color.yellow : personColor);
            }
            else
            {
                Prim(person.transform, PrimitiveType.Capsule, "PersonBody",
                    new Vector3(0f, 0.95f, 0f), new Vector3(0.46f, 0.95f, 0.46f), Color.yellow);
                Prim(person.transform, PrimitiveType.Sphere, "PersonHead",
                    new Vector3(0f, 1.78f, 0f), Vector3.one * 0.26f, new Color(0.9f, 0.65f, 0.45f));
            }
            Label(person.transform, "PersonLabel", "Person", new Vector3(0f, 2.15f, 0f), Color.yellow);

            Prim(stage.transform, PrimitiveType.Cylinder, "Target",
                TargetPosition, new Vector3(0.55f, 0.05f, 0.55f), Color.green);
            Label(stage.transform, "TargetLabel", "Target", TargetPosition + new Vector3(0f, 0.45f, 0f), Color.green);

            StyleObject(ball);
            return stage.transform;
        }

        /// <summary>Swap an existing stage's placeholder Person for the SAM mesh.</summary>
        private static void ApplyPersonMesh(Transform stage, Mesh personMesh, Color personColor)
        {
            var person = stage.Find("Person");
            if (person == null || person.Find("PersonSamMesh") != null) return;
            foreach (var name in new[] { "PersonBody", "PersonHead" })
            {
                var prim = person.Find(name);
                if (prim != null) prim.gameObject.SetActive(false);
            }
            var meshGo = new GameObject("PersonSamMesh");
            meshGo.transform.SetParent(person, false);
            meshGo.AddComponent<MeshFilter>().sharedMesh = personMesh;
            var renderer = meshGo.AddComponent<MeshRenderer>();
            SceneMaterials.ApplyColor(renderer, personColor == default ? Color.yellow : personColor);
        }

        /// <summary>Object (blue) flights to the green target after the last release.</summary>
        public static bool TryObjectFlight(SessionTimeline timeline, float time, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (timeline == null || BasketballSceneBuilder.IsBasketball(timeline)) return false;
            GrabEvent last = null;
            foreach (var g in timeline.grab_events)
                if (g.type == "release" && (last == null || g.timestamp > last.timestamp)) last = g;
            if (last == null || time < last.timestamp) return false;
            FrameData best = timeline.frames[0]; float bd = float.MaxValue;
            foreach (var f in timeline.frames)
            { float d = Mathf.Abs(f.timestamp - last.timestamp); if (d < bd) { bd = d; best = f; } }
            var joints = last.hand == "left" ? best.leftHandJoints : best.rightHandJoints;
            Vector3 from = joints != null && joints.Length > 0 ? joints[0] : best.headPosition;
            float u = Mathf.Clamp01((time - last.timestamp) / 0.7f);
            pos = Vector3.Lerp(from, TargetPosition, u);
            pos.y += Mathf.Sin(u * Mathf.PI) * 0.35f;
            return true;
        }

        private static void StyleObject(Transform ball)
        {
            if (ball == null) return;
            ball.name = "CaptureObject";
            SceneMaterials.ApplyColor(ball.GetComponent<Renderer>(), Color.blue); // blue = Object
        }

        private static void Label(Transform parent, string name, string text, Vector3 pos, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text; tm.fontSize = 48; tm.characterSize = 0.06f;
            tm.anchor = TextAnchor.MiddleCenter; tm.color = color;
        }

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
            SceneMaterials.ApplyColor(go.GetComponent<Renderer>(), color);
        }
    }
}
