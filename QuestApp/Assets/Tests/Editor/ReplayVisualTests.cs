using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using ReMotionXR.Core;
using ReMotionXR.Replay;
using Object = UnityEngine.Object;

namespace ReMotionXR.Tests
{
    public class ReplayVisualTests
    {
        [Test]
        public void RealSamCapture_TexturesScalePlacementAndRender()
        {
            // Integration fixture: actual private capture stays outside Assets/build output.
            string evidence = Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/evidence"));
            string package = Path.Combine(evidence, "visual-fix-scene.remotion");
            if (!File.Exists(package)) Assert.Ignore("Private real-capture fixture is not available on this machine.");
            byte[] bytes = File.ReadAllBytes(package);
            Assert.IsTrue(ScenePackage.TryParseAssets(bytes, out var assets, out string error), error);
            Assert.IsTrue(ScenePackage.TryParseTimeline(bytes, out var timeline, out error), error);
            Assert.AreEqual(2, assets.Count);
            var root = new GameObject("VisualFixture");
            var cameraGo = new GameObject("VisualFixtureCamera");
            var camera = cameraGo.AddComponent<Camera>();
            RenderTexture rt = null; Texture2D rendered = null;
            try
            {
                Vector3 keyboard = Vector3.zero, monitor = Vector3.zero;
                for (int i = 0; i < assets.Count; ++i)
                {
                    var a = assets[i];
                    Assert.AreEqual("sam3d", a.Info.reconstruction_backend);
                    var mesh = GlbMeshLoader.LoadMesh(a.GlbBytes, out Color color, out Texture2D texture, out error);
                    Assert.IsNotNull(mesh, error); Assert.IsNotNull(texture, "Embedded base-color texture must be decoded");
                    Assert.AreEqual(mesh.vertexCount, mesh.uv.Length);
                    Assert.GreaterOrEqual(texture.width, 512);
                    var parent = new GameObject(a.Info.label); parent.transform.SetParent(root.transform, false);
                    ReplayAssetLayout.Place(parent.transform, mesh, a.Info, timeline, i);
                    var model = ReplayAssetLayout.AddModel(parent.transform, mesh, color, texture, a.Info);
                    Assert.AreSame(texture, model.GetComponent<Renderer>().sharedMaterial.mainTexture);
                    Assert.IsTrue(model.GetComponent<Renderer>().sharedMaterial.shader.isSupported);
                    Assert.That(mesh.bounds.size.x * model.transform.localScale.x,
                        Is.EqualTo(i == 0 ? .45f : .60f).Within(.001f));
                    if (i == 0) keyboard = parent.transform.position; else monitor = parent.transform.position;
                    float min = 1f, max = 0f;
                    foreach (var p in texture.GetPixels()) { float v = p.grayscale; min = Mathf.Min(min, v); max = Mathf.Max(max, v); }
                    Assert.Greater(max - min, .05f, "Texture must contain details, not solid white");
                    File.WriteAllBytes(Path.Combine(evidence, "visual-texture-" + i + ".png"), texture.EncodeToPNG());
                }
                Assert.Greater(monitor.y, keyboard.y + .08f, "Monitor should be above keyboard");
                Assert.Greater(Vector3.Distance(keyboard, monitor), .15f);
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.065f, .08f, .11f);
                camera.fieldOfView = 65f; camera.nearClipPlane = .01f; camera.farClipPlane = 10f;
                camera.transform.position = timeline.frames[0].headPosition;
                camera.transform.LookAt((keyboard + monitor) * .5f);
                rt = new RenderTexture(1280, 960, 24); camera.targetTexture = rt; camera.Render();
                var previous = RenderTexture.active; RenderTexture.active = rt;
                rendered = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                rendered.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); rendered.Apply(); RenderTexture.active = previous;
                File.WriteAllBytes(Path.Combine(evidence, "replay-visual-preview.png"), rendered.EncodeToPNG());
                Debug.Log($"[ReMotion visual validation] keyboard={keyboard} monitor={monitor}; textured preview saved.");
            }
            finally
            {
                camera.targetTexture = null;
                Object.DestroyImmediate(root); Object.DestroyImmediate(cameraGo);
                if (rt != null) Object.DestroyImmediate(rt); if (rendered != null) Object.DestroyImmediate(rendered);
            }
        }

        [Test]
        public void RealSamCapture_ControllerHidesGiantLabelAndKeepsTextureOnReentry()
        {
            string package = Path.GetFullPath(Path.Combine(Application.dataPath, "../../docs/evidence/visual-fix-scene.remotion"));
            if (!File.Exists(package)) Assert.Ignore("Private real-capture fixture is unavailable.");
            string folder = Path.Combine(Path.GetTempPath(), "remotion-visual-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder); File.Copy(package, Path.Combine(folder, "scene.remotion"));
            var root = new GameObject("controller-visual-fixture");
            try
            {
                var controller = root.AddComponent<ReplayController>();
                for (int i = 0; i < 2; ++i)
                {
                    Assert.IsTrue(controller.EnterSession("visual", folder, out string error), error);
                    Assert.IsFalse(controller.Player.grabbedObject.Find("ObjectLabel").gameObject.activeSelf);
                    Assert.AreEqual(2, root.GetComponentsInChildren<ReplayAssetResources>(true).Length);
                    foreach (var owner in root.GetComponentsInChildren<ReplayAssetResources>(true)) Assert.IsNotNull(owner.texture);
                    var label = controller.sceneRoot.Find("IdentifiedObjects/Computer Monitor/ObjectLabel");
                    Assert.IsNotNull(label);
                    Assert.Less(label.GetComponent<Renderer>().bounds.size.y, .04f, "Replay label must not fill the view");
                    controller.ExitSession();
                }
            }
            finally { Object.DestroyImmediate(root); Directory.Delete(folder, true); }
        }

        [Test]
        public void Replay_AnchoredHandsAndUnrelatedGrabKeepStaticObject()
        {
            var root = new GameObject("anchored-scene"); var hand = new GameObject("hand"); var obj = new GameObject("keyboard");
            try
            {
                root.transform.SetPositionAndRotation(new Vector3(3f, 0f, 2f), Quaternion.Euler(0f, 90f, 0f));
                hand.transform.SetParent(root.transform, false); obj.transform.SetParent(root.transform, false);
                obj.transform.localPosition = new Vector3(.1f, .8f, .5f);
                var player = root.AddComponent<ReplayPlayer>(); player.sceneRoot = root.transform;
                player.rightHandGhost = hand.transform; player.grabbedObject = obj.transform; player.grabbedObjectId = "keyboard";
                var timeline = new SessionTimeline { duration_seconds = 1f };
                timeline.frames.Add(new FrameData { headRotation = Quaternion.identity, rightHandJoints = new[] { new Vector3(.2f, 1f, .4f) } });
                timeline.grab_events.Add(new GrabEvent { objectId = "mouse", hand = "right", type = "grab", timestamp = 0f });
                Vector3 original = obj.transform.position; player.Load(timeline); player.Scrub(.5f);
                Assert.That(Vector3.Distance(hand.transform.position, root.transform.TransformPoint(timeline.frames[0].rightHandJoints[0])), Is.LessThan(.0001f));
                Assert.AreSame(root.transform, obj.transform.parent);
                Assert.That(Vector3.Distance(original, obj.transform.position), Is.LessThan(.0001f));
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
