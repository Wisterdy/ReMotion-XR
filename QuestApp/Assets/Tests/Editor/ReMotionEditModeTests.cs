using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using ReMotionXR.Accessibility;
using ReMotionXR.Capture;
using ReMotionXR.Core;
using ReMotionXR.Library;
using ReMotionXR.Replay;

namespace ReMotionXR.Tests
{
    /// <summary>EditMode unit tests for core ReMotion XR logic (no SDK needed).</summary>
    public class ReMotionEditModeTests
    {
        [Test]
        public void IndicatorColors_MapByKind()
        {
            Assert.AreEqual(Color.blue, ObjectIndicator.ColorFor("blue"));     // Object
            Assert.AreEqual(Color.yellow, ObjectIndicator.ColorFor("yellow")); // Person
            Assert.AreEqual(Color.green, ObjectIndicator.ColorFor("green"));   // Target
            Assert.AreEqual(Color.blue, ObjectIndicator.ColorFor("unknown"));  // default = Object
        }

        [Test]
        public void SessionData_JsonRoundTrip()
        {
            var meta = new SessionMetadata
            {
                sessionId = "abc123",
                displayName = "Crossover",
                durationSeconds = 5.2f,
                status = "Ready",
            };
            var json = JsonUtility.ToJson(meta);
            var back = JsonUtility.FromJson<SessionMetadata>(json);
            Assert.AreEqual("abc123", back.sessionId);
            Assert.AreEqual("Crossover", back.displayName);
            Assert.AreEqual(5.2f, back.durationSeconds, 0.001f);
            Assert.AreEqual("Ready", back.status);
        }

        [Test]
        public void KeyPoseManager_ExtractsBookendPoses()
        {
            var go = new GameObject("kpm");
            try
            {
                var kpm = go.AddComponent<KeyPoseManager>();
                var timeline = new SessionTimeline { duration_seconds = 2f };
                for (int i = 0; i <= 10; i++)
                {
                    timeline.frames.Add(new FrameData
                    {
                        timestamp = i * 0.2f,
                        headPosition = new Vector3(i * 0.1f, 0f, 0f),
                    });
                }
                var poses = kpm.Extract(timeline);
                Assert.GreaterOrEqual(poses.Count, 2, "start + end poses expected");
                Assert.AreEqual(0f, poses[0].timestamp, 0.001f);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void ReplayPlayer_SpeedSelectionAndClamp()
        {
            var go = new GameObject("replay");
            try
            {
                var player = go.AddComponent<ReplayPlayer>();
                player.Load(new SessionTimeline { duration_seconds = 1f, frames = new List<FrameData>() });
                player.SetSpeed(0);
                Assert.AreEqual(0.25f, player.PlaybackSpeed, 0.001f);
                player.SetSpeed(2);
                Assert.AreEqual(1f, player.PlaybackSpeed, 0.001f);
                player.SetSpeed(99); // clamps to fastest
                Assert.AreEqual(1f, player.PlaybackSpeed, 0.001f);
                player.Play();
                Assert.IsTrue(player.IsPlaying);
                player.Pause();
                Assert.IsFalse(player.IsPlaying);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void GodMode_TogglesDioramaScale()
        {
            var root = new GameObject("sceneRoot");
            var ctrlGo = new GameObject("godmode");
            try
            {
                var ctrl = ctrlGo.AddComponent<GodModeController>();
                ctrl.sceneRoot = root.transform;
                ctrl.dioramaScale = 0.2f;
                // Awake already ran on AddComponent; scale starts at full size.
                ctrl.SetGodMode(true);
                Assert.IsTrue(ctrl.GodModeActive);
                Assert.AreEqual(0.2f, root.transform.localScale.x, 0.001f);
                ctrl.SetGodMode(false);
                Assert.AreEqual(1f, root.transform.localScale.x, 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(ctrlGo);
            }
        }

        // ---- Fix 1: Record/Save wiring lives in a runtime controller ----
        // Editor-time AddListener lambdas do not survive scene save/reload,
        // so MainUIController must (un)register in OnEnable/OnDisable and a
        // click must fire exactly once per enable cycle.
        [Test]
        public void MainUIController_ButtonsFireOnceAcrossEnableCycles()
        {
            var root = new GameObject("ui-test");
            try
            {
                var recorder = root.AddComponent<SessionRecorder>();
                var statusGo = new GameObject("StatusText");
                statusGo.transform.SetParent(root.transform);
                var status = statusGo.AddComponent<Text>();
                var recordGo = new GameObject("RecordButton");
                recordGo.transform.SetParent(root.transform);
                var recordBtn = recordGo.AddComponent<Button>();
                var saveGo = new GameObject("SaveButton");
                saveGo.transform.SetParent(root.transform);
                var saveBtn = saveGo.AddComponent<Button>();

                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.recorder = recorder;
                ctrl.recordButton = recordBtn;
                ctrl.saveButton = saveBtn;
                ctrl.statusText = status;
                // OnEnable does not fire for plain MonoBehaviours in EditMode,
                // so drive the exact registration OnEnable/OnDisable perform.
                ctrl.WireHandlers();

                recordBtn.onClick.Invoke();
                Assert.IsFalse(recorder.IsRecording,
                    "no Recording claim before camera readiness");
                Assert.IsTrue(status.text.Contains("Starting") || status.text.Contains("Requesting"),
                    "delayed readiness shows Starting/Requesting, got: " + status.text);
                recorder.NotifyCameraReady();
                Assert.IsTrue(recorder.IsRecording, "first frame must reach Recording");
                Assert.IsTrue(status.text.Contains("Recording"));

                ctrl.UnwireHandlers(); // what OnDisable does on scene unload
                recorder.StopAndSave();
                Assert.IsFalse(recorder.IsRecording);
                recordBtn.onClick.Invoke();
                Assert.IsFalse(recorder.IsRecording, "unwired controller must not handle clicks");

                ctrl.WireHandlers(); // re-enable: registration restored
                recordBtn.onClick.Invoke();
                recorder.NotifyCameraReady();
                Assert.IsTrue(recorder.IsRecording);
                recorder.StopAndSave(); // cleanup path (autoProcessor null: no upload)
                ctrl.UnwireHandlers();
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ---- Fix 2: XR UI input path (single EventSystem + input module) ----
        [Test]
        public void XrUiInputSetup_CreatesSingleEventSystemWithModule()
        {
            var first = XrUiInputSetup.EnsureEventSystem();
            try
            {
                Assert.IsNotNull(first);
                Assert.IsNotNull(first.GetComponent<BaseInputModule>(),
                    "EventSystem needs an input module or UI clicks never fire");
                var second = XrUiInputSetup.EnsureEventSystem();
                Assert.AreSame(first, second, "must not create duplicate EventSystems");
                Assert.AreEqual(1, Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length);
            }
            finally { if (first != null) Object.DestroyImmediate(first.gameObject); }
        }

        // ---- Fix 3: Library creates cards even without an assigned prefab ----
        [Test]
        public void LibraryManager_RefreshCreatesCardFromFixture()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-lib-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            string sessionDir = Path.Combine(rootDir, "demo1");
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "metadata.json"), JsonUtility.ToJson(
                new SessionMetadata { sessionId = "demo1", displayName = "Demo One", status = "Ready", durationSeconds = 2f }));
            Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out string pkgErr), pkgErr);
            File.WriteAllBytes(Path.Combine(sessionDir, "scene.remotion"), pkg);

            var libGo = new GameObject("library");
            var parentGo = new GameObject("cardParent");
            try
            {
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir;
                lib.cardParent = parentGo.transform;
                lib.cardPrefab = null; // saved Main.unity state: must not silently show nothing
                int created = lib.Refresh();
                Assert.AreEqual(1, created, "one fixture session must produce one card");
                Assert.AreEqual(1, parentGo.transform.childCount);
                Assert.IsNotNull(parentGo.GetComponentInChildren<SceneCardUI>());
            }
            finally
            {
                Object.DestroyImmediate(libGo);
                Object.DestroyImmediate(parentGo);
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            }
        }

        [Test]
        public void LibraryManager_DeleteWithoutConfirmPanel_FailsClosed()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-lib-test2");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            string sessionDir = Path.Combine(rootDir, "keep1");
            Directory.CreateDirectory(sessionDir);
            File.WriteAllText(Path.Combine(sessionDir, "scene.remotion"), "x");
            var libGo = new GameObject("library2");
            try
            {
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir;
                lib.confirmPanel = null; // unwired dialog must never delete immediately
                lib.RequestDeleteEntireScene("keep1");
                Assert.IsTrue(Directory.Exists(sessionDir),
                    "delete without a confirmation dialog must fail closed");
            }
            finally
            {
                Object.DestroyImmediate(libGo);
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            }
        }

        // ---- Fix 4: Enter Virtual Scene loads and plays a visible replay ----
        [Test]
        public void ReplayController_EnterSession_PlaysAndMovesGhost()
        {
            string dir = Path.Combine(Path.GetTempPath(), "remotion-replay-test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out string pkgErr), pkgErr);
            File.WriteAllBytes(Path.Combine(dir, "scene.remotion"), pkg);

            var go = new GameObject("replay-ctrl");
            try
            {
                var ctrl = go.AddComponent<ReplayController>();
                ctrl.EnsureBuilt();
                Assert.IsTrue(ctrl.EnterSession("demo", dir, out string err), err);
                Assert.IsTrue(ctrl.IsInReplay);
                Assert.IsTrue(ctrl.Player.IsPlaying, "Enter must start playback, not only log");
                Vector3 atStart = ctrl.Player.rightHandGhost.position;
                ctrl.Player.Scrub(ctrl.Player.Duration);
                Vector3 atEnd = ctrl.Player.rightHandGhost.position;
                Assert.Greater(Vector3.Distance(atStart, atEnd), 0.1f,
                    "ghost must visibly move between the two known demo positions");
                ctrl.ExitSession();
                Assert.IsFalse(ctrl.IsInReplay);
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void ReplayController_EnterSession_RejectsInvalidPackage()
        {
            string dir = Path.Combine(Path.GetTempPath(), "remotion-replay-bad");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "scene.remotion"), "stub scene package (placeholder)");
            var go = new GameObject("replay-bad");
            try
            {
                var ctrl = go.AddComponent<ReplayController>();
                ctrl.EnsureBuilt();
                LogAssert.Expect(LogType.Error, new Regex("Enter failed"));
                Assert.IsFalse(ctrl.EnterSession("bad", dir, out string err));
                Assert.IsFalse(string.IsNullOrEmpty(err));
                Assert.IsFalse(ctrl.IsInReplay);
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        // ---- Fix 5: bundled offline demo is a real, parseable package ----
        [Test]
        public void DemoTimeline_IsDeterministic_AndDemoInstallParses()
        {
            var a = DemoTimeline.Create();
            var b = DemoTimeline.Create();
            Assert.AreEqual(JsonUtility.ToJson(a), JsonUtility.ToJson(b), "demo must be deterministic");
            Assert.GreaterOrEqual(a.frames.Count, 2);
            Assert.Greater(a.duration_seconds, 0f);
            Vector3 first = a.frames[0].rightHandJoints[0];
            Vector3 last = a.frames[a.frames.Count - 1].rightHandJoints[0];
            Assert.Greater(Vector3.Distance(first, last), 0.1f, "demo hand must move");

            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-demo-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            try
            {
                Assert.IsTrue(LibraryManager.TryInstallBundledDemo(rootDir, out string err), err);
                string pkgPath = Path.Combine(rootDir, DemoTimeline.SessionId, "scene.remotion");
                Assert.IsTrue(File.Exists(pkgPath));
                Assert.IsTrue(ScenePackage.TryParseTimeline(File.ReadAllBytes(pkgPath),
                    out SessionTimeline parsed, out string parseErr), parseErr);
                Assert.AreEqual(a.frames.Count, parsed.frames.Count);
                string metaPath = Path.Combine(rootDir, DemoTimeline.SessionId, "metadata.json");
                var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath));
                Assert.AreEqual("Ready", meta.status);
            }
            finally { if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); }
        }

        // ---- Fix 6: READY only after a real downloaded package validates ----
        [Test]
        public void ScenePackage_RoundTrip_AndRejectsPlaceholderText()
        {
            Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out string err), err);
            Assert.IsTrue(ScenePackage.TryParseTimeline(pkg, out SessionTimeline back, out err), err);
            Assert.Greater(back.frames.Count, 0);
            byte[] placeholder = System.Text.Encoding.UTF8.GetBytes("stub scene package (serverless output placeholder)");
            Assert.IsFalse(ScenePackage.TryParseTimeline(placeholder, out _, out string badErr));
            Assert.IsFalse(string.IsNullOrEmpty(badErr));
            Assert.IsFalse(ScenePackage.TryParseTimeline(new byte[0], out _, out _));
        }

        [Test]
        public void AutoProcessor_TryMarkReady_OnlyOnValidPackage()
        {
            string id = "testmarkready1";
            string folder = SessionRecorder.SessionFolder(id);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "metadata.json"), JsonUtility.ToJson(
                new SessionMetadata { sessionId = id, status = "Processing" }));
            var go = new GameObject("auto");
            try
            {
                var auto = go.AddComponent<AutoProcessor>();
                byte[] bad = System.Text.Encoding.UTF8.GetBytes("not a package");
                Assert.IsFalse(auto.TryMarkReady(id, bad, out string badErr));
                Assert.IsFalse(string.IsNullOrEmpty(badErr));
                Assert.IsFalse(File.Exists(Path.Combine(folder, "scene.remotion")),
                    "invalid download must never become READY");
                var metaAfterBad = JsonUtility.FromJson<SessionMetadata>(
                    File.ReadAllText(Path.Combine(folder, "metadata.json")));
                Assert.AreEqual("Processing", metaAfterBad.status);

                Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] good, out string err), err);
                Assert.IsTrue(auto.TryMarkReady(id, good, out err), err);
                Assert.IsTrue(File.Exists(Path.Combine(folder, "scene.remotion")));
                var meta = JsonUtility.FromJson<SessionMetadata>(
                    File.ReadAllText(Path.Combine(folder, "metadata.json")));
                Assert.AreEqual("Ready", meta.status);
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        // ---- Handedness: right hand mirrored because side was never set ----
        [Test]
        public void HandednessGuard_SideFromName()
        {
            Assert.AreEqual(HandednessGuard.HandSide.Left,
                HandednessGuard.SideFromName("LeftHandAnchor"));
            Assert.AreEqual(HandednessGuard.HandSide.Right,
                HandednessGuard.SideFromName("RightHandAnchor"));
            Assert.AreEqual(HandednessGuard.HandSide.Right,
                HandednessGuard.SideFromName("[BuildingBlock] Hand Tracking right"));
            Assert.AreEqual(HandednessGuard.HandSide.None,
                HandednessGuard.SideFromName("CenterEyeAnchor"));
            Assert.AreEqual(HandednessGuard.HandSide.None,
                HandednessGuard.SideFromName("LeftRightAmbiguous"));
        }

        [Test]
        public void HandednessGuard_SetsRightHandOnMetaComponents()
        {
            // Reflection-only: the test assembly does not reference Meta SDK.
            System.Type ovrHand = FindLoadedType("OVRHand");
            if (ovrHand == null) Assert.Ignore("Meta SDK not loaded in this Editor.");
            var anchor = new GameObject("RightHandAnchor");
            var handGo = new GameObject("hand");
            try
            {
                handGo.transform.SetParent(anchor.transform);
                var hand = handGo.AddComponent(ovrHand);
                var field = ovrHand.GetField("HandType",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(field);
                StringAssert.AreNotEqualIgnoringCase("HandRight", field.GetValue(hand).ToString(),
                    "prefab default must start unset for this test to prove the fix");
                int fixedCount = HandednessGuard.ApplyToHierarchy(anchor.transform);
                Assert.AreEqual(1, fixedCount);
                Assert.AreEqual("HandRight", field.GetValue(hand).ToString(),
                    "right anchor hierarchy must yield a right hand (was mirrored/None)");
            }
            finally
            {
                Object.DestroyImmediate(handGo);
                Object.DestroyImmediate(anchor);
            }
        }

        [Test]
        public void HandednessGuard_SetsSkeletonTypeThroughSetter()
        {
            // Regression (two crashes): the guard first set _skeletonType
            // directly after Awake, then forced the classic HandLeft family
            // while this project uses the OpenXR skeleton — both left bone
            // count vs mesh bone-weights mismatched ("Bone weights do not
            // match bones" -> native crash). The guard must take the type
            // from the hand's own data provider, through SetSkeletonType.
            System.Type ovrSkeleton = FindLoadedType("OVRSkeleton");
            System.Type ovrHand = FindLoadedType("OVRHand");
            System.Type ovrMesh = FindLoadedType("OVRMesh");
            if (ovrSkeleton == null || ovrHand == null || ovrMesh == null)
                Assert.Ignore("Meta SDK not loaded in this Editor.");
            var anchor = new GameObject("LeftHandAnchor");
            var handGo = new GameObject("hand");
            try
            {
                handGo.transform.SetParent(anchor.transform);
                handGo.AddComponent(ovrHand);
                var skeleton = handGo.AddComponent(ovrSkeleton);
                var mesh = handGo.AddComponent(ovrMesh);
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic;
                var getType = ovrSkeleton.GetMethod("GetSkeletonType", flags);
                Assert.IsNotNull(getType);
                Assert.AreEqual("None", getType.Invoke(skeleton, null).ToString(),
                    "prefab default must start unset for this test to prove the fix");
                int fixedCount = HandednessGuard.ApplyToHierarchy(anchor.transform);
                // OVRHand + OVRSkeleton + OVRMesh set, exactly once.
                Assert.AreEqual(3, fixedCount);
                // Project uses OpenXR skeleton (OculusRuntimeSettings
                // handSkeletonVersion=1): provider family is XRHand*.
                Assert.AreEqual("XRHand", HandednessGuard.SkeletonTypePrefix());
                Assert.AreEqual("XRHandLeft", getType.Invoke(skeleton, null).ToString(),
                    "skeleton must follow the provider (OpenXR family), not the classic family");
                // Mesh must be RELOADED with the corrected type: OVRMesh
                // loaded the default mesh in Awake, and on device nothing
                // reloads it (right hand kept the left mesh -> deformed).
                var getMeshType = ovrMesh.GetMethod("GetMeshType", flags);
                Assert.AreEqual("XRHandLeft", getMeshType.Invoke(mesh, null).ToString());
                var loadedField = ovrMesh.GetField("_loadedMeshType", flags);
                Assert.AreEqual("XRHandLeft", loadedField.GetValue(mesh).ToString(),
                    "guard must reload the mesh, not only change its type field");
                // Idempotent: a second pass changes nothing (no re-init churn).
                Assert.AreEqual(0, HandednessGuard.ApplyToHierarchy(anchor.transform));
            }
            finally
            {
                Object.DestroyImmediate(handGo);
                Object.DestroyImmediate(anchor);
            }
        }

        private static System.Type FindLoadedType(string typeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                    if (t.Name == typeName) return t;
            }
            return null;
        }

        // ---- UI theme + pinch menu interaction ----
        [Test]
        public void UiTheme_RoundedSpriteAndStatusColors()
        {
            var sprite = ReMotionUiTheme.RoundedSprite;
            Assert.IsNotNull(sprite);
            Assert.Greater(sprite.border.x, 0f, "rounded panel sprite must be 9-sliced");
            Assert.AreEqual(ReMotionUiTheme.Success, ReMotionUiTheme.ColorForStatus("Ready"));
            Assert.AreEqual(ReMotionUiTheme.Warning, ReMotionUiTheme.ColorForStatus("Processing"));
            Assert.AreEqual(ReMotionUiTheme.Danger, ReMotionUiTheme.ColorForStatus("Failed"));
        }

        [Test]
        public void SceneCardFactory_CreatesThemedCardWithPill()
        {
            var parent = new GameObject("card-test-parent");
            try
            {
                var card = SceneCardFactory.CreateCard(parent.transform);
                Assert.IsNotNull(card.enterSceneButton);
                Assert.IsNotNull(card.statusText);
                var pill = card.statusText.transform.parent;
                Assert.IsNotNull(pill.Find("Dot"), "status pill needs its color dot");
                var libGo = new GameObject("card-test-lib");
                try
                {
                    var lib = libGo.AddComponent<LibraryManager>();
                    lib.LibraryRootOverride = Path.GetTempPath();
                    card.Bind(new SessionMetadata
                    {
                        sessionId = "s1", displayName = "Demo", status = "Ready", durationSeconds = 2f,
                    }, lib);
                }
                finally { Object.DestroyImmediate(libGo); }
                var dot = pill.Find("Dot").GetComponent<Image>();
                Assert.AreEqual(ReMotionUiTheme.Success, dot.color,
                    "Bind must sync the pill dot with the session status");
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void PinchMenuInteraction_EnsuresCanvasAndModule()
        {
            if (PinchMenuInteraction.FindLoadedType("PointableCanvas") == null)
                Assert.Ignore("Meta Interaction SDK not loaded in this Editor.");
            var canvasGo = new GameObject("pinch-canvas");
            var eventGo = new GameObject("pinch-es");
            try
            {
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                Assert.IsTrue(PinchMenuInteraction.EnsureCanvasPointable(canvas));
                Assert.IsNotNull(canvas.GetComponent(
                    PinchMenuInteraction.FindLoadedType("PointableCanvas")));
                Assert.IsNotNull(canvas.GetComponent<GraphicRaycaster>(),
                    "PointableCanvas requires a GraphicRaycaster");

                // EnsureCanvasModule uses the scene EventSystem helper;
                // verify the ISDK module type resolves for the runtime path.
                Assert.IsNotNull(PinchMenuInteraction.FindLoadedType("PointableCanvasModule"));
                Assert.IsNotNull(PinchMenuInteraction.FindLoadedType("RayInteractor"));
            }
            finally
            {
                Object.DestroyImmediate(canvasGo);
                Object.DestroyImmediate(eventGo);
            }
        }

        // ---- Accessibility: settings, dwell-click, text scaling ----
        [Test]
        public void AccessibilitySettings_DefaultsMatchIndicatorStandard()
        {
            PlayerPrefs.DeleteKey("remotion_dwell_enabled");
            PlayerPrefs.DeleteKey("remotion_dwell_seconds");
            PlayerPrefs.DeleteKey("remotion_text_scale");
            Assert.IsFalse(AccessibilitySettings.DwellEnabled,
                "normal interaction is native pinch: dwell off until explicit opt-in");
            Assert.AreEqual(1f, AccessibilitySettings.DwellSeconds, 0.001f,
                "must match ObjectIndicator gazeSelectSeconds standard");
            Assert.AreEqual(20, AccessibilitySettings.ScaledFontSize(20));
            AccessibilitySettings.TextScale = 1.5f;
            try { Assert.AreEqual(30, AccessibilitySettings.ScaledFontSize(20)); }
            finally { AccessibilitySettings.TextScale = 1f; }
        }

        [Test]
        public void DwellClick_HoverActivatesButtonWithoutPinch()
        {
            var go = new GameObject("dwell-button");
            try
            {
                go.AddComponent<Image>();
                var button = go.AddComponent<Button>();
                var dwell = go.AddComponent<ReMotionXR.Accessibility.DwellClick>();
                AccessibilitySettings.DwellEnabled = true; // explicit opt-in path
                int clicks = 0;
                button.onClick.AddListener(() => clicks++);
                dwell.BeginDwell();
                Assert.IsTrue(dwell.IsDwelling);
                dwell.Advance(AccessibilitySettings.DwellSeconds * 0.5f);
                Assert.AreEqual(0, clicks, "half a dwell must not activate");
                dwell.Advance(AccessibilitySettings.DwellSeconds * 0.6f);
                Assert.AreEqual(1, clicks, "completed dwell must activate exactly once");
                dwell.Advance(AccessibilitySettings.DwellSeconds * 2f);
                Assert.AreEqual(1, clicks, "dwell must not refire while held");
                dwell.BeginDwell();
                dwell.EndDwell(); // pointer left: dwell cancels
                dwell.Advance(AccessibilitySettings.DwellSeconds * 2f);
                Assert.AreEqual(1, clicks);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void UiTheme_ButtonsCarryDwellAndLabelsScale()
        {
            AccessibilitySettings.TextScale = 1.5f;
            var parent = new GameObject("theme-test");
            try
            {
                var button = ReMotionUiTheme.Button(parent.transform, "B", "Go",
                    ReMotionUiTheme.ButtonStyle.Primary, 20);
                Assert.IsNotNull(button.GetComponent<ReMotionXR.Accessibility.DwellClick>(),
                    "every themed menu button must be dwell-operable");
                var label = button.GetComponentInChildren<Text>();
                Assert.AreEqual(30, label.fontSize, "labels must honor TextScale");
            }
            finally
            {
                AccessibilitySettings.TextScale = 1f;
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void PinchMenuInteraction_BlockTypesResolve()
        {
            // The pinch stack is Meta's building blocks ([BB] Interactions,
            // [BB] Hands, [BB] Hand Ray) connected by the editor setup;
            // OVRHand alone cannot feed ISDK (it is not an IHand), which is
            // why the runtime side only ensures canvas/module and reports.
            if (PinchMenuInteraction.FindLoadedType("RayInteractor") == null)
                Assert.Ignore("Meta Interaction SDK not loaded in this Editor.");
            Assert.IsNotNull(PinchMenuInteraction.FindLoadedType("PointableCanvas"));
            Assert.IsNotNull(PinchMenuInteraction.FindLoadedType("PointableCanvasModule"));
            Assert.IsNotNull(PinchMenuInteraction.FindLoadedType("FromOVRHandDataSource"));
            Assert.IsNotNull(PinchMenuInteraction.FindLoadedType("OVRCameraRigRef"));
            Assert.AreEqual(0, PinchMenuInteraction.CountByTypeName("RayInteractor"),
                "test scene starts without interactors (editor setup installs them)");
        }

        // ---- Basketball demo must be a basketball scene, not a sliding cube ----
        [Test]
        public void DemoTimeline_IsBasketballCrossoverWithHandSwitchAndShot()
        {
            var t = DemoTimeline.Create();
            StringAssert.Contains("basketball", t.action_sequence);
            Assert.GreaterOrEqual(t.duration_seconds, 3.5f, "dribble -> crossover -> layup needs time");
            Assert.IsTrue(t.grab_events.Exists(g => g.hand == "left" && g.type == "grab"),
                "crossover must switch the ball to the left hand");
            Assert.IsTrue(t.grab_events.Exists(g => g.hand == "right" && g.type == "release"),
                "ball must leave the right hand at the crossover / shot");
            Assert.IsTrue(BasketballSceneBuilder.IsBasketball(t));
        }

        [Test]
        public void BasketballScene_BuildsCourtHoopDefenderAndOrangeBall()
        {
            string dir = Path.Combine(Path.GetTempPath(), "remotion-bball-test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out string e), e);
            File.WriteAllBytes(Path.Combine(dir, "scene.remotion"), pkg);
            var go = new GameObject("bball-ctrl");
            try
            {
                var ctrl = go.AddComponent<ReplayController>();
                ctrl.EnsureBuilt();
                Assert.IsTrue(ctrl.EnterSession(DemoTimeline.SessionId, dir, out string err), err);
                Assert.IsNotNull(ctrl.sceneRoot.Find("BasketballCourt"), "court must be built");
                Assert.IsNotNull(FindDeep(ctrl.sceneRoot, "Hoop"), "hoop must be built");
                Assert.IsNotNull(FindDeep(ctrl.sceneRoot, "Defender"), "opponent defender must be present");
                var ball = ctrl.Player.grabbedObject;
                Assert.AreEqual("Sphere", ball.GetComponent<MeshFilter>().sharedMesh.name,
                    "grabbed object must be a basketball sphere, not a cube");
                ctrl.Player.Scrub(3.6f);
                float toHoop = Vector3.Distance(ball.position, BasketballSceneBuilder.HoopPosition);
                Assert.Less(toHoop, 0.8f, "after the layup release the ball must fly to the hoop");
            }
            finally { Object.DestroyImmediate(go); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            { var hit = FindDeep(root.GetChild(i), name); if (hit != null) return hit; }
            return null;
        }

        // ---- Library: Netflix-style grid with Video vs Virtual choice ----
        [Test]
        public void Library_UsesGridAndCardOffersVideoAndVirtual()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-grid-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            string sdir = Path.Combine(rootDir, "cap1");
            Directory.CreateDirectory(sdir);
            File.WriteAllText(Path.Combine(sdir, "metadata.json"), JsonUtility.ToJson(
                new SessionMetadata { sessionId = "cap1", displayName = "My Capture", status = "Ready", durationSeconds = 4f }));
            Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out string e), e);
            File.WriteAllBytes(Path.Combine(sdir, "scene.remotion"), pkg);
            File.WriteAllBytes(Path.Combine(sdir, "original_video.mp4"), new byte[] { 1, 2, 3 });
            var libGo = new GameObject("grid-lib"); var parentGo = new GameObject("grid-parent");
            parentGo.AddComponent<RectTransform>().sizeDelta = new Vector2(760, 430); // actual Library viewport
            try
            {
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir; lib.cardParent = parentGo.transform; lib.cardPrefab = null;
                Assert.AreEqual(1, lib.Refresh());
                var grid = parentGo.GetComponent<UnityEngine.UI.GridLayoutGroup>();
                Assert.IsNotNull(grid, "library must lay captures out in a grid");
                Assert.AreEqual(5, grid.constraintCount, "user requested five captures across");
                var tile = parentGo.GetComponentInChildren<SceneCardUI>();
                tile.openDetailsButton.onClick.Invoke();
                var card = lib.ActiveDetailsCard;
                Assert.IsTrue(card.watchVideoButton.interactable, "card must offer the regular video");
                Assert.IsTrue(card.enterSceneButton.interactable, "card must offer the virtual scene");
                Assert.IsNotNull(card.thumbnail, "card must have a poster thumbnail");
            }
            finally { Object.DestroyImmediate(libGo); Object.DestroyImmediate(parentGo); if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); }
        }

        [Test]
        public void VideoPlayback_PlaceholderWhenNoMp4_AndPanelCloses()
        {
            string dir = Path.Combine(Path.GetTempPath(), "remotion-video-test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "original_video.txt"), "placeholder");
            var go = new GameObject("video-test");
            try
            {
                var player = go.AddComponent<VideoPlaybackController>();
                player.EnsureBuilt(go.transform);
                var state = player.Show("cap1", dir, "My Capture");
                Assert.AreEqual(VideoPlaybackController.PlaybackState.Placeholder, state);
                Assert.IsTrue(player.IsOpen);
                player.Close();
                Assert.IsFalse(player.IsOpen);
            }
            finally { Object.DestroyImmediate(go); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Test]
        public void MainUI_CaptureAndLibraryModesTogglePanels()
        {
            var root = new GameObject("mode-test");
            try
            {
                var capture = new GameObject("CapturePanel"); capture.transform.SetParent(root.transform);
                var library = new GameObject("LibraryPanel"); library.transform.SetParent(root.transform);
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.capturePanel = capture; ctrl.libraryPanel = library;
                ctrl.ShowCapture();
                Assert.AreEqual(ReMotionMainUIController.AppMode.Capture, ctrl.CurrentMode);
                Assert.IsFalse(capture.activeSelf, "Capture tab must not show a panel or video list");
                Assert.IsFalse(library.activeSelf);
                ctrl.ShowLibrary();
                Assert.AreEqual(ReMotionMainUIController.AppMode.Library, ctrl.CurrentMode);
                Assert.IsTrue(library.activeSelf); Assert.IsFalse(capture.activeSelf);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ---- Capture is free-form: user names any action, no predefined types ----
        [Test]
        public void SessionRecorder_FreeFormTitleIsSavedVerbatim()
        {
            var go = new GameObject("freeform-recorder");
            string id = null;
            try
            {
                var rec = go.AddComponent<SessionRecorder>();
                rec.captureTitle = "My morning stretch routine";
                rec.StartRecording();
                rec.NotifyCameraReady();
                id = rec.StopAndSave();
                string metaPath = Path.Combine(SessionRecorder.SessionFolder(id), "metadata.json");
                var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath));
                Assert.AreEqual("My morning stretch routine", meta.displayName,
                    "the user's own name for the action must be saved, not a preset type");
                Assert.AreEqual("recorded_action", meta.actionSequence);
            }
            finally
            {
                Object.DestroyImmediate(go);
                if (id != null && Directory.Exists(SessionRecorder.SessionFolder(id)))
                    Directory.Delete(SessionRecorder.SessionFolder(id), true);
            }
        }

        [Test]
        public void GenericCapture_GetsObjectPersonTargetStageNotBasketball()
        {
            var timeline = new SessionTimeline { action_sequence = "recorded_action", duration_seconds = 1f };
            timeline.frames.Add(new FrameData { timestamp = 0f,
                leftHandJoints = new[] { Vector3.zero }, rightHandJoints = new[] { Vector3.one } });
            string dir = Path.Combine(Path.GetTempPath(), "remotion-generic-test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            Assert.IsTrue(ScenePackage.TryCreatePackage(timeline, out byte[] pkg, out string e), e);
            File.WriteAllBytes(Path.Combine(dir, "scene.remotion"), pkg);
            var go = new GameObject("generic-ctrl");
            try
            {
                var ctrl = go.AddComponent<ReplayController>();
                ctrl.EnsureBuilt();
                Assert.IsTrue(ctrl.EnterSession("mycapture", dir, out string err), err);
                // User-directed: a capture scene holds only the SAM
                // object + recorded hands — no invented Person/Target.
                Assert.IsNull(FindDeep(ctrl.sceneRoot, "Person"),
                    "no generic Person may be invented for a capture");
                Assert.IsNull(FindDeep(ctrl.sceneRoot, "Target"),
                    "no generic Target may be invented for a capture");
                var stage = FindDeep(ctrl.sceneRoot, "CaptureStage");
                Assert.IsTrue(stage == null || !stage.gameObject.activeSelf,
                    "no generic stage in a capture scene");
                Assert.IsFalse(ctrl.Player.grabbedObject.gameObject.activeSelf,
                    "no placeholder object when the package has no SAM model");
                var court = ctrl.sceneRoot.Find("BasketballCourt");
                Assert.IsTrue(court == null || !court.gameObject.activeSelf,
                    "a free-form capture must not be forced into a basketball scene");
            }
            finally { Object.DestroyImmediate(go); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        // ---- Pinch: fallback selector ray math + takeover path ----
        [Test]
        public void PinchFallback_TryHitFindsButtonUnderRay()
        {
            var esGo = new GameObject("pinch-es");
            var canvasGo = new GameObject("pinch-canvas");
            var camGo = new GameObject("pinch-cam");
            try
            {
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                esGo.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvasGo.AddComponent<GraphicRaycaster>();
                var canvasRt = canvasGo.GetComponent<RectTransform>();
                canvasRt.sizeDelta = new Vector2(400f, 300f);
                canvasGo.transform.position = new Vector3(0f, 0f, 2f);
                var btnGo = new GameObject("PinchButton");
                btnGo.transform.SetParent(canvasGo.transform, false);
                btnGo.AddComponent<Image>();
                var button = btnGo.AddComponent<Button>();
                btnGo.GetComponent<RectTransform>().sizeDelta = new Vector2(120f, 60f);
                canvas.worldCamera = camGo.AddComponent<Camera>();
                camGo.transform.position = Vector3.zero;
                camGo.transform.LookAt(canvasGo.transform.position);
                Canvas.ForceUpdateCanvases();
                // EditMode doesn't run OnEnable for test objects; toggling
                // active registers the button's Graphic with the raycaster.
                btnGo.SetActive(false); btnGo.SetActive(true);
                canvasGo.SetActive(false); canvasGo.SetActive(true);
                Canvas.ForceUpdateCanvases();

                bool hit = PinchFallbackSelector.TryHit(canvas, camGo.GetComponent<Camera>(),
                    canvas.GetComponent<GraphicRaycaster>(),
                    camGo.transform.position, camGo.transform.forward,
                    out GameObject target, out _);
                Assert.IsTrue(hit, "a ray at the canvas centre must find the button");
                Assert.AreSame(button.gameObject, target);
            }
            finally
            {
                Object.DestroyImmediate(esGo); Object.DestroyImmediate(canvasGo); Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void PinchFallback_TryHitConeSnapsToNearButtonAtDistance()
        {
            var esGo = new GameObject("cone-es");
            var canvasGo = new GameObject("cone-canvas");
            var camGo = new GameObject("cone-cam");
            try
            {
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvasGo.AddComponent<GraphicRaycaster>();
                canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(400f, 300f);
                canvasGo.transform.position = new Vector3(0f, 0f, 8f);
                canvasGo.transform.localScale = Vector3.one * 0.01f;
                var btnGo = new GameObject("ConeButton");
                btnGo.transform.SetParent(canvasGo.transform, false);
                btnGo.AddComponent<Image>();
                var button = btnGo.AddComponent<Button>();
                var btnRt = btnGo.GetComponent<RectTransform>();
                btnRt.sizeDelta = new Vector2(60f, 40f);
                btnRt.anchoredPosition = new Vector2(70f, 0f);
                var camera = camGo.AddComponent<Camera>();
                canvas.worldCamera = camera;
                camGo.transform.position = Vector3.zero;
                camGo.transform.LookAt(canvasGo.transform.position);
                Canvas.ForceUpdateCanvases();

                bool hit = PinchFallbackSelector.TryHit(canvas, camera,
                    canvas.GetComponent<GraphicRaycaster>(), Vector3.zero, Vector3.forward,
                    out GameObject target, out _);
                Assert.IsTrue(hit, "the distance cone must snap a near-centre ray to the closest button");
                Assert.AreSame(button.gameObject, target);
            }
            finally
            {
                Object.DestroyImmediate(esGo); Object.DestroyImmediate(canvasGo); Object.DestroyImmediate(camGo);
            }
        }

        [Test]
        public void PinchMenuInteraction_EnableFallbackTakesOver()
        {
            var selector = PinchMenuInteraction.EnableFallback();
            try
            {
                Assert.IsNotNull(selector);
                Assert.IsTrue(selector.IsEnabled, "watchdog takeover must enable pinch selection");
            }
            finally { if (selector != null) Object.DestroyImmediate(selector.gameObject); }
        }

        // ---- Quest camera capture: frames in, playable video out ----
        private class FakeEncoder : IVideoEncoder
        {
            public int Frames;
            public int LastWidth;
            public int LastHeight;
            public int LastFps;
            private string _folder;
            public bool Start(string folder, int w, int h, int fps)
            {
                _folder = folder; LastWidth = w; LastHeight = h; LastFps = fps; return true;
            }
            public void AddFrame(Texture2D frame) { Frames++; }
            public void AddFrame(byte[] rgba, int w, int h) { Frames++; }
            public long LastPtsUs;
            public void AddFrame(byte[] rgba, int w, int h, long ptsUs) { Frames++; LastPtsUs = ptsUs; }
            public string Finish()
            {
                string path = Path.Combine(_folder, "original_video.mp4");
                File.WriteAllBytes(path, new byte[] { 0, 0, 0, 1 });
                return path;
            }
        }

        [Test]
        public void QuestCameraCapture_SamplesFramesAndProducesVideo()
        {
            string dir = Path.Combine(Path.GetTempPath(), "remotion-camera-test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var go = new GameObject("camera-test");
            var texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(new Color32[16 * 16]);
                texture.Apply();
                var fake = new FakeEncoder();
                var capture = go.AddComponent<QuestCameraCapture>();
                capture.EncoderOverride = fake;
                capture.TextureOverride = texture;
                capture.encoderWidth = 1920;
                capture.encoderHeight = 1080;
                capture.sampleFps = 30;
                Assert.IsTrue(capture.BeginCapture(dir));
                Assert.AreEqual(480, fake.LastWidth, "capture width must be capped for Quest performance");
                Assert.AreEqual(270, fake.LastHeight, "capture height must be capped for Quest performance");
                Assert.AreEqual(8, fake.LastFps, "capture rate must be capped for Quest performance");
                int before = capture.FramesCaptured; // sample loop may already have queued one
                bool queued = capture.CaptureFrameNow();
                Assert.IsTrue(queued || capture.HasPendingReadback,
                    "an injected camera frame must be queued exactly once");
                capture.FlushPendingFrame();
                Assert.AreEqual(before + 1, capture.FramesCaptured,
                    "the queued frame must complete through readback without a synchronous stall");
                string path = capture.EndCapture();
                Assert.IsNotNull(path, "finishing capture must yield the session video");
                Assert.IsTrue(capture.HasPlayableVideo);
                Assert.IsTrue(File.Exists(Path.Combine(dir, "original_video.mp4")));
            }
            finally
            {
                Object.DestroyImmediate(go); Object.DestroyImmediate(texture);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void QuestBuildSettings_PassthroughAndCameraAccessEnabled()
        {
            // Meta's project-setup processor can flip the passthrough flag
            // based on scene layers, so set both flags the way the build
            // menu (ReMotion XR / Apply Quest Build Settings) does.
            var configAssets = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(
                "Assets/Oculus/OculusProjectConfig.asset");
            Assert.IsNotNull(configAssets);
            Assert.Greater(configAssets.Length, 0);
            var so = new UnityEditor.SerializedObject(configAssets[0]);
            // insightPassthroughEnabled is obsolete/auto-reset by Meta; the
            // live flag is _insightPassthroughSupport (2 = Required).
            so.FindProperty("_insightPassthroughSupport").intValue = 2;
            so.FindProperty("isPassthroughCameraAccessEnabled").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            UnityEditor.AssetDatabase.SaveAssets();
            string config = File.ReadAllText(Path.Combine(Application.dataPath, "Oculus/OculusProjectConfig.asset"));
            StringAssert.Contains("_insightPassthroughSupport: 2", config,
                "passthrough support must be Required (camera API requires it)");
            StringAssert.Contains("isPassthroughCameraAccessEnabled: 1", config,
                "Passthrough Camera Access must be enabled or the Quest gives no camera frames");
        }

        // ---- Processing must work standalone (no server on a headset) ----
        [Test]
        public void AutoProcessor_ProcessLocallyMakesSceneReady_AndRecoversStuck()
        {
            string id = "localproc_test_" + System.Guid.NewGuid().ToString("N").Substring(0, 6);
            string dir = SessionRecorder.SessionFolder(id);
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "timeline.json"),
                    JsonUtility.ToJson(DemoTimeline.Create()));
                File.WriteAllText(Path.Combine(dir, "metadata.json"), JsonUtility.ToJson(
                    new SessionMetadata { sessionId = id, displayName = "Local", status = "Processing" }));
                var go = new GameObject("localproc");
                try
                {
                    var processor = go.AddComponent<AutoProcessor>();
                    Assert.IsTrue(processor.ProcessLocally(id, out string error), error);
                    Assert.IsTrue(File.Exists(Path.Combine(dir, "scene.remotion")),
                        "on-device processing must write the scene package");
                    var meta = JsonUtility.FromJson<SessionMetadata>(
                        File.ReadAllText(Path.Combine(dir, "metadata.json")));
                    Assert.AreEqual("Ready", meta.status);
                }
                finally { Object.DestroyImmediate(go); }

                // A second stuck capture is recovered by the library sweep.
                File.Delete(Path.Combine(dir, "scene.remotion"));
                File.WriteAllText(Path.Combine(dir, "metadata.json"), JsonUtility.ToJson(
                    new SessionMetadata { sessionId = id, displayName = "Local", status = "Processing" }));
                var go2 = new GameObject("localproc-recover");
                try
                {
                    var processor = go2.AddComponent<AutoProcessor>();
                    Assert.GreaterOrEqual(processor.RecoverProcessingSessions(), 1);
                    Assert.IsTrue(File.Exists(Path.Combine(dir, "scene.remotion")));
                }
                finally { Object.DestroyImmediate(go2); }
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        // ---- Pinch debug ray: fades in near pinch, green on menu, off when disabled ----
        [Test]
        public void PinchRayVisualizer_ProximityAndStateThresholds()
        {
            var go = new GameObject("pinch-ray-test");
            try
            {
                var viz = go.AddComponent<PinchRayVisualizer>();
                Assert.AreEqual(0f, viz.Proximity01(0.20f), 0.001f, "open fingers: no ray");
                Assert.AreEqual(0f, viz.Proximity01(float.MaxValue), 0.001f, "no hand data: no ray");
                Assert.AreEqual(1f, viz.Proximity01(viz.pinchDistance), 0.001f, "at pinch distance: full");
                float mid = viz.Proximity01((viz.nearPinchDistance + viz.pinchDistance) * 0.5f);
                Assert.Greater(mid, 0.4f); Assert.Less(mid, 0.6f);
                Assert.AreEqual(PinchRayVisualizer.RayVisualState.Hidden,
                    viz.StateFor(0.2f, 0f, 0.7f, false, false));
                Assert.AreEqual(PinchRayVisualizer.RayVisualState.Approaching,
                    viz.StateFor(0.05f, 0f, 0.7f, false, false),
                    "near pinch without a menu hit shows the aiming ray");
                Assert.AreEqual(PinchRayVisualizer.RayVisualState.HoverHit,
                    viz.StateFor(0.05f, 0f, 0.7f, false, true),
                    "near pinch touching the menu turns the ray green");
                Assert.AreEqual(PinchRayVisualizer.RayVisualState.Pinching,
                    viz.StateFor(0.05f, 0f, 0.7f, true, true));
                Assert.IsFalse(viz.ShouldShow(0.2f, 0f, 0.7f, false, true));
                Assert.IsTrue(viz.ShouldShow(0.05f, 0f, 0.7f, false, true));
                Assert.IsFalse(viz.ShouldShow(0.05f, 0f, 0.7f, false, false),
                    "untracked hand must not show a stale ray");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void PinchRayVisualizer_ShowsLineAndReticle_AndDisableHides()
        {
            var go = new GameObject("pinch-ray-visual");
            try
            {
                var viz = go.AddComponent<PinchRayVisualizer>();
                viz.ShowRay(Vector3.zero, Vector3.forward, new Vector3(0f, 0f, 2f), 0.8f,
                    PinchRayVisualizer.RayVisualState.HoverHit);
                Assert.IsTrue(viz.IsBuilt);
                Assert.IsTrue(viz.LineVisible, "near-pinch ray line must be visible");
                Assert.AreEqual(PinchRayVisualizer.RayVisualState.HoverHit, viz.State);
                viz.HideRay();
                Assert.IsFalse(viz.LineVisible);
                viz.showRay = false; // the documented off-switch
                viz.ShowRay(Vector3.zero, Vector3.forward, new Vector3(0f, 0f, 2f), 1f,
                    PinchRayVisualizer.RayVisualState.Pinching);
                Assert.IsFalse(viz.LineVisible, "disabled ray must stay hidden even at full pinch");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ---- Left pinch must not fire/steal the right hand (device report) ----
        [Test]
        public void PinchPerHand_LeftPinchDoesNotCountForRight()
        {
            Assert.AreEqual(0.02f, PinchFallbackSelector.OwnTipDistance(
                HandednessGuard.HandSide.Left, 0.02f, 0.12f), 0.001f);
            Assert.AreEqual(0.12f, PinchFallbackSelector.OwnTipDistance(
                HandednessGuard.HandSide.Right, 0.02f, 0.12f), 0.001f);
            Assert.IsTrue(PinchFallbackSelector.IsPinchingForHand(0.02f, float.MaxValue, 0f, false, 0.7f),
                "left hand at pinch distance is pinching");
            Assert.IsFalse(PinchFallbackSelector.IsPinchingForHand(0.12f, float.MaxValue, 0f, false, 0.7f),
                "right hand must stay open when only the left pinches");
        }

        [Test]
        public void LibraryManager_EnterScene_ResolvesUnwiredReplayController()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-enter-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            string sdir = Path.Combine(rootDir, "cap1");
            Directory.CreateDirectory(sdir);
            Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out string e), e);
            File.WriteAllBytes(Path.Combine(sdir, "scene.remotion"), pkg);
            var libGo = new GameObject("enter-lib");
            var replayGo = new GameObject("enter-replay");
            try
            {
                replayGo.AddComponent<ReplayController>(); // present, but NOT wired to the library
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir;
                lib.replayController = null; // saved-scene state from device
                Assert.IsTrue(lib.EnterScene("cap1"), "Enter Virtual Scene must self-heal the unwired controller");
                Assert.IsNotNull(lib.replayController);
                Assert.IsTrue(lib.replayController.IsInReplay);
            }
            finally
            {
                Object.DestroyImmediate(libGo); Object.DestroyImmediate(replayGo);
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            }
        }

        [Test]
        public void MainUI_ShowVideo_HidesLibraryCardsBehindVideo()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-video-hide-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            Directory.CreateDirectory(Path.Combine(rootDir, "cap1"));
            var root = new GameObject("video-hide-root");
            try
            {
                var capture = new GameObject("CapturePanel"); capture.transform.SetParent(root.transform);
                var libraryPanel = new GameObject("LibraryPanel"); libraryPanel.transform.SetParent(root.transform);
                var libGo = new GameObject("video-hide-lib");
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir;
                var playerGo = new GameObject("video-hide-player");
                var player = playerGo.AddComponent<VideoPlaybackController>();
                player.panel = new GameObject("VideoPlayerPanel");
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.capturePanel = capture; ctrl.libraryPanel = libraryPanel;
                ctrl.library = lib; ctrl.videoPlayer = player;
                ctrl.ShowVideo("cap1");
                Assert.AreEqual(ReMotionMainUIController.AppMode.Video, ctrl.CurrentMode);
                Assert.IsFalse(libraryPanel.activeSelf, "library cards/Delete must not peek behind the video");
                Assert.IsTrue(player.panel.activeSelf, "Watch Video panel must open");
                Object.DestroyImmediate(libGo); Object.DestroyImmediate(playerGo);
                Object.DestroyImmediate(player.panel);
            }
            finally { Object.DestroyImmediate(root); if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); }
        }

        [Test]
        public void SceneCard_WatchEnterVisibleAndDangerRowFitsCard()
        {
            var parent = new GameObject("card-fit-parent");
            try
            {
                var card = SceneCardFactory.CreateCard(parent.transform);
                Assert.IsNotNull(card.watchVideoButton, "card must offer Watch Video");
                Assert.IsNotNull(card.enterSceneButton, "card must offer Enter Virtual Scene");
                Assert.IsFalse(card.detailsPanel.activeSelf, "Management actions are collapsed initially");
                card.SetDetailsVisible(true);
                var tile = (RectTransform)card.transform;
                var corners = new Vector3[4];
                foreach (var button in card.detailsPanel.GetComponentsInChildren<Button>(true))
                {
                    ((RectTransform)button.transform).GetWorldCorners(corners);
                    foreach (var corner in corners)
                        Assert.IsTrue(tile.rect.Contains(tile.InverseTransformPoint(corner)), button.name);
                }
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void MainUI_BootstrapOrder_RecordWiresWhenAssignedAfterAwake()
        {
            // Exact device sequence: AddComponent runs Awake/WireHandlers
            // while Record/Save are still null, Bootstrap assigns them
            // after. The old single _wired flag blocked wiring forever:
            // Record "clicked" in logs, recording never started.
            var canvasGo = new GameObject("LibraryCanvas_WorldSpace");
            var recorderGo = new GameObject("bootstrap-recorder");
            string recordedId = null;
            try
            {
                canvasGo.AddComponent<Canvas>();
                canvasGo.AddComponent<GraphicRaycaster>();
                var ctrl = new GameObject("bootstrap-ctrl").AddComponent<ReMotionMainUIController>();
                var recorder = recorderGo.AddComponent<SessionRecorder>();
                recorder.handRecorder = recorderGo.AddComponent<HandPoseRecorder>();
                var recordBtn = ReMotionUiTheme.Button(canvasGo.transform, "RecordButton", "Record",
                    ReMotionUiTheme.ButtonStyle.Record, 17);
                var saveBtn = ReMotionUiTheme.Button(canvasGo.transform, "SaveButton", "Save",
                    ReMotionUiTheme.ButtonStyle.Primary, 17);
                ctrl.recorder = recorder;
                ctrl.recordButton = recordBtn;
                ctrl.saveButton = saveBtn;
                ctrl.EnsureUi();
                ctrl.WireHandlers(); // what Bootstrap does after assigning fields
                recordBtn.onClick.Invoke();
                recorder.NotifyCameraReady();
                Assert.IsTrue(recorder.IsRecording,
                    "Record assigned after Awake must still start recording");
                recordedId = recorder.CurrentSessionId;
                saveBtn.onClick.Invoke();
                Assert.IsFalse(recorder.IsRecording);
                Object.DestroyImmediate(ctrl.gameObject);
            }
            finally
            {
                Object.DestroyImmediate(canvasGo); Object.DestroyImmediate(recorderGo);
                if (recordedId != null)
                {
                    string dir = SessionRecorder.SessionFolder(recordedId);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            }
        }

        [Test]
        public void RecordingUi_OnlyStopVisibleThenMenuReturns()
        {
            var root = new GameObject("recording-ui-root");
            var recorderGo = new GameObject("recording-ui-recorder");
            string recordedId = null;
            try
            {
                var capture = new GameObject("CapturePanel"); capture.transform.SetParent(root.transform);
                var libraryPanel = new GameObject("LibraryPanel"); libraryPanel.transform.SetParent(root.transform);
                var recordBtn = ReMotionUiTheme.Button(root.transform, "RecordButton", "Record",
                    ReMotionUiTheme.ButtonStyle.Record, 17);
                var saveBtn = ReMotionUiTheme.Button(root.transform, "SaveButton", "Save",
                    ReMotionUiTheme.ButtonStyle.Primary, 17);
                var capTab = ReMotionUiTheme.Button(root.transform, "CaptureTabButton", "Capture",
                    ReMotionUiTheme.ButtonStyle.Secondary, 15);
                var libTab = ReMotionUiTheme.Button(root.transform, "LibraryTabButton", "Library",
                    ReMotionUiTheme.ButtonStyle.Secondary, 15);
                var recorder = recorderGo.AddComponent<SessionRecorder>();
                recorder.handRecorder = recorderGo.AddComponent<HandPoseRecorder>();
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.capturePanel = capture; ctrl.libraryPanel = libraryPanel;
                ctrl.recordButton = recordBtn; ctrl.saveButton = saveBtn;
                ctrl.captureTabButton = capTab; ctrl.libraryTabButton = libTab;
                ctrl.recorder = recorder;
                ctrl.WireHandlers(); // Bootstrap wires after assigning fields
                ctrl.ShowCapture();
                recordBtn.onClick.Invoke();
                recorder.NotifyCameraReady();
                recordedId = recorder.CurrentSessionId;
                Assert.IsTrue(recorder.IsRecording);
                Assert.IsTrue(ctrl.IsRecordingUi, "Record must enter the recording-only UI");
                Assert.IsFalse(capture.activeSelf, "Capture panel hides while recording");
                Assert.IsFalse(recordBtn.gameObject.activeSelf, "Record hides while recording");
                Assert.IsFalse(capTab.gameObject.activeSelf, "tabs hide while recording");
                Assert.IsFalse(libTab.gameObject.activeSelf);
                Assert.IsTrue(saveBtn.gameObject.activeSelf, "Stop & Save is the only control left");
                saveBtn.onClick.Invoke();
                Assert.IsFalse(ctrl.IsRecordingUi, "Stop must restore the menu");
                Assert.IsTrue(capTab.gameObject.activeSelf, "menu chrome returns after Stop");
                // No playable MP4 in this fixture: the save fails
                // honestly and stays in Capture (no Library jump).
                Assert.AreEqual(SessionRecorder.CaptureState.Failed, recorder.State);
                Assert.AreEqual(ReMotionMainUIController.AppMode.Capture, ctrl.CurrentMode);
                Assert.IsFalse(string.IsNullOrEmpty(recorder.LastError), recorder.LastError);
            }
            finally
            {
                Object.DestroyImmediate(root); Object.DestroyImmediate(recorderGo);
                if (recordedId != null)
                {
                    string dir = SessionRecorder.SessionFolder(recordedId);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            }
        }

        [Test]
        public void MenuModes_RenderExactlyOneScreenPerMode()
        {
            var root = new GameObject("menu-modes-root");
            var recorderGo = new GameObject("menu-modes-recorder");
            try
            {
                var capture = new GameObject("CapturePanel"); capture.transform.SetParent(root.transform);
                var libraryPanel = new GameObject("LibraryPanel"); libraryPanel.transform.SetParent(root.transform);
                var recordBtn = ReMotionUiTheme.Button(root.transform, "RecordButton", "Record",
                    ReMotionUiTheme.ButtonStyle.Record, 17);
                var saveBtn = ReMotionUiTheme.Button(root.transform, "SaveButton", "Save",
                    ReMotionUiTheme.ButtonStyle.Primary, 17);
                var capTab = ReMotionUiTheme.Button(root.transform, "CaptureTabButton", "Capture",
                    ReMotionUiTheme.ButtonStyle.Secondary, 15);
                var libTab = ReMotionUiTheme.Button(root.transform, "LibraryTabButton", "Library",
                    ReMotionUiTheme.ButtonStyle.Secondary, 15);
                var recorder = recorderGo.AddComponent<SessionRecorder>();
                recorder.handRecorder = recorderGo.AddComponent<HandPoseRecorder>();
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.capturePanel = capture; ctrl.libraryPanel = libraryPanel;
                ctrl.recordButton = recordBtn; ctrl.saveButton = saveBtn;
                ctrl.captureTabButton = capTab; ctrl.libraryTabButton = libTab;
                ctrl.recorder = recorder;
                ctrl.ShowCapture();
                Assert.IsTrue(!capture.activeSelf && !libraryPanel.activeSelf,
                    "Capture mode: only Record/Stop controls, no panel or Library leaking in");
                Assert.IsTrue(recordBtn.gameObject.activeSelf, "Capture offers Record");
                // Shared navigation stays visible in menu modes; the
                // current tab is styled selected instead of hidden.
                Assert.IsTrue(libTab.gameObject.activeSelf, "Capture shows Library nav");
                Assert.IsTrue(capTab.gameObject.activeSelf, "Capture shows its selected tab");
                ctrl.ShowLibrary();
                Assert.IsTrue(libraryPanel.activeSelf && !capture.activeSelf,
                    "Library mode: Library screen only");
                Assert.IsFalse(recordBtn.gameObject.activeInHierarchy,
                    "Record is inside the inactive Capture root in Library");
                Assert.IsTrue(capTab.gameObject.activeSelf, "Library shows Capture nav");
                Assert.IsTrue(libTab.gameObject.activeSelf, "Library shows its selected tab");
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(recorderGo); }
        }

        [Test]
        public void MainUI_VersionLabelShowsCurrentBuild()
        {
            var canvasGo = new GameObject("LibraryCanvas_WorldSpace");
            try
            {
                canvasGo.AddComponent<Canvas>();
                canvasGo.AddComponent<GraphicRaycaster>();
                var ctrl = new GameObject("version-ctrl").AddComponent<ReMotionMainUIController>();
                ctrl.EnsureUi();
                var label = canvasGo.transform.Find("VersionLabel");
                Assert.IsNotNull(label, "menu must show a small version label");
                var text = label.GetComponent<Text>();
                Assert.IsNotNull(text);
                StringAssert.Contains("v" + BuildInfo.Version, text.text);
                StringAssert.Contains("build " + BuildInfo.BuildNumber, text.text);
                Object.DestroyImmediate(ctrl.gameObject);
            }
            finally { Object.DestroyImmediate(canvasGo); }
        }

        [Test]
        public void Dwell_MajorActionsNeverAutoFire()
        {
            Assert.IsTrue(DwellClick.IsDwellExcluded("EnterButton"));
            Assert.IsTrue(DwellClick.IsDwellExcluded("WatchButton"));
            Assert.IsTrue(DwellClick.IsDwellExcluded("DeleteEntireButton"));
            Assert.IsTrue(DwellClick.IsDwellExcluded("RecordButton"));
            Assert.IsTrue(DwellClick.IsDwellExcluded("SaveButton"));
            Assert.IsFalse(DwellClick.IsDwellExcluded("CaptureTabButton"),
                "minor navigation may still dwell for accessibility");
            Assert.IsFalse(DwellClick.SuppressAll, "dwell suppression is recording-only");
        }

        [Test]
        public void PinchRecordingGate_MidAirAndShortPinchesNeverClick()
        {
            Assert.IsTrue(PinchFallbackSelector.ShouldClickWhileRecording(false, false, 0.05f),
                "outside recording, normal pinch clicks apply");
            Assert.IsFalse(PinchFallbackSelector.ShouldClickWhileRecording(true, false, 2f),
                "mid-air action pinch must never click while recording");
            Assert.IsFalse(PinchFallbackSelector.ShouldClickWhileRecording(true, true, 0.2f),
                "a quick pinch on Stop must not end the recording");
            Assert.IsTrue(PinchFallbackSelector.ShouldClickWhileRecording(true, true, 0.9f),
                "aimed, held pinch on Stop ends the recording");
        }

        [Test]
        public void RecordingUi_LocksEveryOtherModeUntilStop()
        {
            var root = new GameObject("recording-lock-root");
            var recorderGo = new GameObject("recording-lock-recorder");
            string recordedId = null;
            try
            {
                var capture = new GameObject("CapturePanel"); capture.transform.SetParent(root.transform);
                var libraryPanel = new GameObject("LibraryPanel"); libraryPanel.transform.SetParent(root.transform);
                var recordBtn = ReMotionUiTheme.Button(root.transform, "RecordButton", "Record",
                    ReMotionUiTheme.ButtonStyle.Record, 17);
                var saveBtn = ReMotionUiTheme.Button(root.transform, "SaveButton", "Save",
                    ReMotionUiTheme.ButtonStyle.Primary, 17);
                var recorder = recorderGo.AddComponent<SessionRecorder>();
                recorder.handRecorder = recorderGo.AddComponent<HandPoseRecorder>();
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.capturePanel = capture; ctrl.libraryPanel = libraryPanel;
                ctrl.recordButton = recordBtn; ctrl.saveButton = saveBtn; ctrl.recorder = recorder;
                ctrl.WireHandlers();
                ctrl.ShowCapture();
                recordBtn.onClick.Invoke();
                recorder.NotifyCameraReady();
                recordedId = recorder.CurrentSessionId;
                Assert.IsTrue(ctrl.IsRecordingUi);
                // No other screen may pop up while recording.
                ctrl.ShowLibrary();
                Assert.IsFalse(libraryPanel.activeSelf, "Library must not pop up while recording");
                ctrl.ShowCapture();
                Assert.IsFalse(capture.activeSelf, "Capture panel must not pop up while recording");
                ctrl.ShowVideo("cap1");
                Assert.AreEqual(ReMotionMainUIController.AppMode.Capture, ctrl.CurrentMode,
                    "Video must not pop up while recording");
                ctrl.ShowReplay("cap1");
                Assert.AreEqual(ReMotionMainUIController.AppMode.Capture, ctrl.CurrentMode,
                    "Virtual scene must not pop up while recording");
                Assert.AreEqual("⏸  Stop Recording",
                    saveBtn.GetComponentInChildren<Text>().text);
                Assert.IsTrue(DwellClick.SuppressAll, "no dwell clicks while recording");
                saveBtn.onClick.Invoke();
                Assert.IsFalse(ctrl.IsRecordingUi);
                Assert.IsFalse(DwellClick.SuppressAll, "dwell returns after Stop");
                // No playable video here: menu returns in Capture, and
                // Library appears only after a successful save.
                Assert.IsFalse(libraryPanel.activeSelf);
                Assert.AreEqual(ReMotionMainUIController.AppMode.Capture, ctrl.CurrentMode);
            }
            finally
            {
                Object.DestroyImmediate(root); Object.DestroyImmediate(recorderGo);
                if (recordedId != null)
                {
                    string dir = SessionRecorder.SessionFolder(recordedId);
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                }
            }
        }

        [Test]
        public void ReplayScene_AnchorsRecordedStartToViewer()
        {
            string dir = Path.Combine(Path.GetTempPath(), "remotion-anchor-test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var timeline = new SessionTimeline { action_sequence = "recorded_action", duration_seconds = 1f };
            timeline.frames.Add(new FrameData { timestamp = 0f, headPosition = new Vector3(5f, 1.5f, 5f),
                headRotation = Quaternion.identity,
                leftHandJoints = new[] { Vector3.zero }, rightHandJoints = new[] { Vector3.one } });
            timeline.frames.Add(new FrameData { timestamp = 1f, headPosition = new Vector3(5f, 1.5f, 5f),
                headRotation = Quaternion.identity,
                leftHandJoints = new[] { Vector3.zero }, rightHandJoints = new[] { Vector3.one } });
            Assert.IsTrue(ScenePackage.TryCreatePackage(timeline, out byte[] pkg, out string e), e);
            File.WriteAllBytes(Path.Combine(dir, "scene.remotion"), pkg);
            var cameraGo = new GameObject("anchor-camera") { tag = "MainCamera" };
            var ctrlGo = new GameObject("anchor-ctrl");
            try
            {
                cameraGo.AddComponent<Camera>();
                cameraGo.transform.position = new Vector3(0f, 1.6f, 0f);
                cameraGo.transform.rotation = Quaternion.identity; // facing +Z... Unity camera faces +Z? forward is -Z? use transform forward
                var ctrl = ctrlGo.AddComponent<ReplayController>();
                ctrl.EnsureBuilt();
                Assert.IsTrue(ctrl.EnterSession("cap", dir, out string err), err);
                Vector3 mapped = ctrl.SceneRootForTests != null
                    ? ctrl.SceneRootForTests.TransformPoint(new Vector3(5f, 1.5f, 5f))
                    : Vector3.zero;
                // The recorded start (5,1.5,5) must land at the viewer
                // (Camera.main, wherever it is), not at the room origin.
                Assert.IsNotNull(Camera.main);
                Assert.Less(Vector3.Distance(
                    new Vector3(mapped.x, 0f, mapped.z),
                    new Vector3(Camera.main.transform.position.x, 0f,
                        Camera.main.transform.position.z)), 0.05f,
                    "scene must anchor to the viewer, not appear under/behind them");
            }
            finally
            {
                Object.DestroyImmediate(cameraGo); Object.DestroyImmediate(ctrlGo);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Test]
        public void ServerSettingsPanel_PrefilledEndpointAndSavesKeyOnDeviceOnly()
        {
            PlayerPrefs.DeleteKey("remotion_endpoint");
            PlayerPrefs.DeleteKey("remotion_api_key");
            var canvasGo = new GameObject("LibraryCanvas_WorldSpace");
            var processorGo = new GameObject("settings-processor");
            try
            {
                canvasGo.AddComponent<Canvas>();
                canvasGo.AddComponent<GraphicRaycaster>();
                var settings = ScriptableObject.CreateInstance<ServerSettings>();
                settings.gatewayUrl = "https://gateway.example/v1/test-endpoint";
                var processor = processorGo.AddComponent<AutoProcessor>();
                processor.settings = settings;
                var ctrl = new GameObject("settings-ctrl").AddComponent<ReMotionMainUIController>();
                ctrl.processor = processor;
                ctrl.EnsureUi();
                Assert.IsNotNull(ctrl.serverSettingsButton, "menu must offer ⚙ Server");
                Assert.IsNotNull(ctrl.serverSettingsPanel);
                ctrl.ShowServerSettings();
                Assert.IsTrue(ctrl.serverSettingsPanel.activeSelf);
                StringAssert.Contains("gateway.example/v1/test-endpoint", ctrl.endpointInput.text,
                    "gateway must be pre-filled so only the key is typed");
                Assert.AreEqual(InputField.ContentType.Password, ctrl.apiKeyInput.contentType,
                    "API key field must be masked");
                ctrl.apiKeyInput.text = "test-key-not-a-secret";
                ctrl.SaveServerSettings();
                Assert.IsTrue(ctrl.serverSettingsPanel.activeSelf,
                    "saving settings stays in Server; the root is never hidden");
                Assert.AreEqual(ReMotionMainUIController.AppMode.Server, ctrl.CurrentMode);
                Assert.AreEqual("test-key-not-a-secret", PlayerPrefs.GetString("remotion_api_key"),
                    "key must persist on-device (PlayerPrefs) only");
                Assert.AreEqual("test-key-not-a-secret", settings.remotionApiKey);
                Object.DestroyImmediate(ctrl.gameObject);
                Object.DestroyImmediate(settings);
            }
            finally
            {
                PlayerPrefs.DeleteKey("remotion_endpoint");
                PlayerPrefs.DeleteKey("remotion_api_key");
                Object.DestroyImmediate(canvasGo); Object.DestroyImmediate(processorGo);
            }
        }

        [Test]
        public void VideoImage_TintMustNotBlackOutFrames()
        {
            var go = new GameObject("video-tint-test");
            try
            {
                var player = go.AddComponent<VideoPlaybackController>();
                player.EnsureBuilt(go.transform);
                Assert.IsNotNull(player.videoImage);
                // RawImage.color multiplies the texture: black = black video
                // even while VideoPlayer reports playing (device bug).
                Assert.AreEqual(Color.white, player.videoImage.color,
                    "video tint must be white or every frame renders black");
                Assert.IsNotNull(player.videoImage.texture,
                    "video image must be bound to the render texture");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void SamGate_CardIndicatorAndImmersiveLock()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-sam-gate-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            string sdir = Path.Combine(rootDir, "cap1");
            Directory.CreateDirectory(sdir);
            File.WriteAllText(Path.Combine(sdir, "original_video.txt"), "placeholder");
            Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out string e), e);
            File.WriteAllBytes(Path.Combine(sdir, "scene.remotion"), pkg);
            var parent = new GameObject("sam-card-parent");
            var libGo = new GameObject("sam-lib");
            try
            {
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir;
                var processing = new SessionMetadata
                { sessionId = "cap1", displayName = "Cap", status = "Ready",
                  samStatus = "Processing", samProgress = 40 };
                File.WriteAllText(Path.Combine(sdir, "metadata.json"),
                    JsonUtility.ToJson(processing, true));
                var card = SceneCardFactory.CreateCard(parent.transform);
                card.Bind(processing, lib);
                Assert.IsFalse(card.enterSceneButton.interactable,
                    "immersive must stay locked while SAM runs");
                Assert.IsTrue(card.watchVideoButton.interactable,
                    "original video stays watchable while SAM runs");
                StringAssert.Contains("SAM 40%", card.statusText.text,
                    "processing indicator must show on the card");
                Assert.IsFalse(lib.EnterScene("cap1"),
                    "Enter must refuse before the SAM model exists");
                var ready = new SessionMetadata
                { sessionId = "cap1", displayName = "Cap", status = "Ready",
                  samStatus = "Ready", samProgress = 100 };
                card.Bind(ready, lib);
                Assert.IsTrue(card.enterSceneButton.interactable);
                Assert.AreEqual("Ready", card.statusText.text,
                    "SAM indicator is removed once the scene is created");
            }
            finally
            {
                Object.DestroyImmediate(parent); Object.DestroyImmediate(libGo);
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            }
        }

        // ---- Validate EVERY menu button before the user tests again ----
        [Test]
        public void PinchSelector_ClickDataNeverNull()
        {
            var esGo = new GameObject("clickdata-es");
            var selectorGo = new GameObject("clickdata-selector");
            var buttonGo = new GameObject("clickdata-button");
            try
            {
                esGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
                var selector = selectorGo.AddComponent<PinchFallbackSelector>();
                // Device bug: Record "clicked" in the log but never fired,
                // because release-frame data was null and execution was skipped.
                Assert.IsNotNull(selector.ResolveClickData(null, null, buttonGo),
                    "a pinch click must always carry event data");
                var gaze = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                Assert.AreSame(gaze, selector.ResolveClickData(null, gaze, buttonGo));
                Assert.AreSame(gaze, selector.ResolveClickData(gaze, null, buttonGo));
            }
            finally
            {
                Object.DestroyImmediate(esGo); Object.DestroyImmediate(selectorGo);
                Object.DestroyImmediate(buttonGo);
            }
        }

        [Test]
        public void AllMenuButtons_ValidateEndToEnd()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-buttons-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            string sdir = Path.Combine(rootDir, "cap1");
            Directory.CreateDirectory(sdir);
            File.WriteAllText(Path.Combine(sdir, "metadata.json"), JsonUtility.ToJson(
                new SessionMetadata { sessionId = "cap1", displayName = "My Capture", status = "Ready", durationSeconds = 2f }));
            Assert.IsTrue(ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out string pe), pe);
            File.WriteAllBytes(Path.Combine(sdir, "scene.remotion"), pkg);
            File.WriteAllText(Path.Combine(sdir, "original_video.txt"), "placeholder");

            var canvasGo = new GameObject("LibraryCanvas_WorldSpace");
            var contentGo = new GameObject("SceneList");
            var recorderGo = new GameObject("button-recorder");
            var processorGo = new GameObject("button-processor");
            var libraryGo = new GameObject("button-library");
            var replayGo = new GameObject("button-replay");
            var ctrlGo = new GameObject("button-controller");
            string recordedId = null;
            try
            {
                var canvas = canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvasGo.AddComponent<GraphicRaycaster>();
                canvasGo.GetComponent<RectTransform>().sizeDelta = new Vector2(1200f, 700f);
                contentGo.transform.SetParent(canvasGo.transform, false);
                contentGo.AddComponent<RectTransform>();

                var recorder = recorderGo.AddComponent<SessionRecorder>();
                recorder.handRecorder = recorderGo.AddComponent<HandPoseRecorder>();
                var processor = processorGo.AddComponent<AutoProcessor>();
                var library = libraryGo.AddComponent<LibraryManager>();
                library.LibraryRootOverride = rootDir;
                library.cardParent = contentGo.transform;
                library.autoProcessor = processor;
                var replay = replayGo.AddComponent<ReplayController>();
                library.replayController = replay;

                var recordBtn = ReMotionUiTheme.Button(canvasGo.transform, "RecordButton", "Record",
                    ReMotionUiTheme.ButtonStyle.Record, 17);
                var saveBtn = ReMotionUiTheme.Button(canvasGo.transform, "SaveButton", "Save",
                    ReMotionUiTheme.ButtonStyle.Primary, 17);
                var statusGo = new GameObject("StatusText");
                statusGo.transform.SetParent(canvasGo.transform, false);
                var status = statusGo.AddComponent<Text>();

                var ctrl = ctrlGo.AddComponent<ReMotionMainUIController>();
                ctrl.recorder = recorder; ctrl.processor = processor; ctrl.library = library;
                ctrl.replayController = replay; ctrl.recordButton = recordBtn;
                ctrl.saveButton = saveBtn; ctrl.statusText = status;
                ctrl.EnsureUi(); ctrl.WireHandlers();

                // Record -> Save: a real session lands in the Library.
                ctrl.ShowCapture();
                recordBtn.onClick.Invoke();
                recorder.NotifyCameraReady();
                Assert.IsTrue(recorder.IsRecording, "Record button must start recording");
                saveBtn.onClick.Invoke();
                Assert.IsFalse(recorder.IsRecording, "Save button must stop recording");
                recordedId = recorder.CurrentSessionId;
                Assert.IsTrue(File.Exists(Path.Combine(
                    SessionRecorder.SessionFolder(recordedId), "metadata.json")),
                    "Save must write the session");

                // Tabs swap modes.
                ctrl.captureTabButton.onClick.Invoke();
                Assert.AreEqual(ReMotionMainUIController.AppMode.Capture, ctrl.CurrentMode);
                Assert.IsTrue(ctrl.capturePanel == null || !ctrl.capturePanel.activeSelf,
                    "Capture tab is Record/Stop only — no legacy panel");
                Assert.IsTrue(ctrl.recordButton.gameObject.activeSelf,
                    "Record must be the Capture tab's primary control");
                ctrl.libraryTabButton.onClick.Invoke();
                Assert.AreEqual(ReMotionMainUIController.AppMode.Library, ctrl.CurrentMode);
                var card = contentGo.GetComponentInChildren<SceneCardUI>();
                Assert.IsNotNull(card, "Library must show the capture card");

                card.openDetailsButton.onClick.Invoke();
                card = library.ActiveDetailsCard;
                // Watch Video opens the player; Close returns to Library.
                card.watchVideoButton.onClick.Invoke();
                Assert.AreEqual(ReMotionMainUIController.AppMode.Video, ctrl.CurrentMode,
                    "Watch Video must open the video mode");
                Assert.IsTrue(ctrl.videoPlayer.IsOpen, "Watch Video must open the player panel");
                ctrl.videoPlayer.closeButton.onClick.Invoke();
                Assert.AreEqual(ReMotionMainUIController.AppMode.Library, ctrl.CurrentMode,
                    "Video Close must return to Library");

                // Enter Virtual Scene loads the replay; HUD Back returns.
                card = contentGo.GetComponentInChildren<SceneCardUI>();
                Assert.IsNotNull(card);
                card.openDetailsButton.onClick.Invoke();
                card = library.ActiveDetailsCard;
                card.enterSceneButton.onClick.Invoke();
                Assert.IsTrue(replay.IsInReplay, "Enter Virtual Scene must load the replay");
                Assert.AreEqual(ReMotionMainUIController.AppMode.Replay, ctrl.CurrentMode);
                ctrl.replayHud.backButton.onClick.Invoke();
                Assert.AreEqual(ReMotionMainUIController.AppMode.Library, ctrl.CurrentMode,
                    "Replay Back must return to Library");

                // Delete asks first, then deletes on confirm.
                card = contentGo.GetComponentInChildren<SceneCardUI>();
                Assert.IsNotNull(card);
                card.openDetailsButton.onClick.Invoke();
                card = library.ActiveDetailsCard;
                card.deleteEntireButton.onClick.Invoke();
                Assert.IsNotNull(library.confirmPanel);
                Assert.IsTrue(library.confirmPanel.activeSelf, "Delete must ask for confirmation");
                Assert.IsTrue(Directory.Exists(sdir), "Delete must wait for confirmation");
                library.confirmDeleteButton.onClick.Invoke();
                Assert.IsFalse(Directory.Exists(sdir), "Confirm Delete must remove the scene");
            }
            finally
            {
                Object.DestroyImmediate(canvasGo); Object.DestroyImmediate(recorderGo);
                Object.DestroyImmediate(processorGo); Object.DestroyImmediate(libraryGo);
                Object.DestroyImmediate(replayGo); Object.DestroyImmediate(ctrlGo);
                if (recordedId != null)
                {
                    string recDir = SessionRecorder.SessionFolder(recordedId);
                    if (Directory.Exists(recDir)) Directory.Delete(recDir, true);
                }
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            }
        }

        // ---- Accessibility: no white-on-white buttons, AA contrast ----
        [Test]
        public void UiTheme_ApplyStyleFixesWhiteOnWhiteAndMeetsContrast()
        {
            var go = new GameObject("white-on-white");
            try
            {
                var image = go.AddComponent<Image>();
                image.color = Color.white; // saved-scene Record button state
                var button = go.AddComponent<Button>();
                var labelGo = new GameObject("Label");
                labelGo.transform.SetParent(go.transform);
                var label = labelGo.AddComponent<Text>();
                label.text = "Record";
                label.color = Color.white;
                ReMotionUiTheme.ApplyStyle(button, ReMotionUiTheme.ButtonStyle.Record, 18);
                Assert.AreEqual(ReMotionUiTheme.RecordRed, image.color,
                    "white Record background must become the themed red");
                Assert.AreEqual(ReMotionUiTheme.TextPrimary, label.color);
                foreach (var style in new[] { ReMotionUiTheme.ButtonStyle.Primary,
                    ReMotionUiTheme.ButtonStyle.Record, ReMotionUiTheme.ButtonStyle.Danger,
                    ReMotionUiTheme.ButtonStyle.Secondary })
                {
                    float ratio = ReMotionUiTheme.ContrastRatio(
                        ReMotionUiTheme.TextPrimary, ReMotionUiTheme.BackgroundForStyle(style));
                    Assert.GreaterOrEqual(ratio, 4.5f, $"white label on {style} must meet WCAG AA");
                }
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ---- Capture visibility, delete dialog, real-hand replay ----
        [Test]
        public void HandPoseRecorder_TipDistanceUsesXrTipIndices()
        {
            int count = HandPoseRecorder.JointIndexOf(UnityEngine.XR.Hands.XRHandJointID.LittleTip) + 1;
            var joints = new Vector3[Mathf.Max(count, 30)];
            int index = HandPoseRecorder.JointIndexOf(UnityEngine.XR.Hands.XRHandJointID.IndexTip);
            int thumb = HandPoseRecorder.JointIndexOf(UnityEngine.XR.Hands.XRHandJointID.ThumbTip);
            joints[index] = new Vector3(0.02f, 0f, 0f);
            joints[thumb] = Vector3.zero;
            Assert.AreEqual(0.02f, HandPoseRecorder.TipDistance(joints), 0.001f);
            Assert.AreEqual(float.MaxValue, HandPoseRecorder.TipDistance(new Vector3[3]),
                "short stub arrays must not invent a pinch/grab");
        }

        [Test]
        public void Replay_GhostShowsAllJointsAndObjectFollowsHand()
        {
            string dir = Path.Combine(Path.GetTempPath(), "remotion-ghost-test");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            var timeline = new SessionTimeline { action_sequence = "recorded_action", duration_seconds = 1f };
            timeline.frames.Add(new FrameData { timestamp = 0f, headPosition = new Vector3(0f, 1.5f, 0f),
                leftHandJoints = new[] { Vector3.zero, Vector3.one * 0.1f },
                rightHandJoints = new[] { new Vector3(0.3f, 1.2f, 0.5f), new Vector3(0.35f, 1.25f, 0.5f), new Vector3(0.4f, 1.3f, 0.5f) } });
            timeline.frames.Add(new FrameData { timestamp = 1f, headPosition = new Vector3(0f, 1.5f, 0f),
                leftHandJoints = new[] { Vector3.zero, Vector3.one * 0.1f },
                rightHandJoints = new[] { new Vector3(0.8f, 1.2f, 0.5f), new Vector3(0.85f, 1.25f, 0.5f), new Vector3(0.9f, 1.3f, 0.5f) } });
            // The object follows the hand only with recorded grab evidence.
            timeline.grab_events.Add(new GrabEvent
                { timestamp = 0f, hand = "right", objectId = "capture_object", type = "grab" });
            Assert.IsTrue(ScenePackage.TryCreatePackage(timeline, out byte[] pkg, out string e), e);
            File.WriteAllBytes(Path.Combine(dir, "scene.remotion"), pkg);
            var go = new GameObject("ghost-ctrl");
            try
            {
                var ctrl = go.AddComponent<ReplayController>();
                ctrl.EnsureBuilt();
                Assert.IsTrue(ctrl.EnterSession("cap", dir, out string err), err);
                var visual = ctrl.Player.rightHandGhost.GetComponent<HandGhostVisual>();
                Assert.IsNotNull(visual, "ghost hand must carry the full-joint visual");
                Assert.IsTrue(visual.UsingHandModel,
                    "ghost must show the blue hand model, not ball-per-joint markers");
                Assert.IsNotNull(ctrl.Player.grabbedObject.Find("ObjectLabel"),
                    "the important object must be labelled in the scene");
                float toHand = Vector3.Distance(ctrl.Player.grabbedObject.position,
                    ctrl.Player.rightHandGhost.position);
                Assert.Less(toHand, 0.3f, "object must ride with the recorded hand, not sit at the origin");
                ctrl.Player.Scrub(1f);
                Assert.Greater(ctrl.Player.grabbedObject.position.x, 0.5f,
                    "object must follow the hand across the recorded motion");
            }
            finally { Object.DestroyImmediate(go); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Test]
        public void LibraryManager_DeleteBuildsConfirmPanelAndWaitsForConfirm()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-delete-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            string sdir = Path.Combine(rootDir, "keep1");
            Directory.CreateDirectory(sdir);
            File.WriteAllText(Path.Combine(sdir, "scene.remotion"), "x");
            var canvasGo = new GameObject("delete-canvas");
            canvasGo.AddComponent<Canvas>();
            var libGo = new GameObject("delete-lib");
            try
            {
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir;
                var cardParentGo = new GameObject("delete-card-parent");
                lib.cardParent = cardParentGo.transform; // assigned in the real scene
                lib.RequestDeleteEntireScene("keep1"); // confirmPanel unwired in saved scenes
                Assert.IsNotNull(lib.confirmPanel, "delete must build its confirmation panel");
                Assert.IsTrue(lib.confirmPanel.activeSelf);
                Assert.IsTrue(Directory.Exists(sdir), "nothing deletes before the user confirms");
                lib.confirmDeleteButton.onClick.Invoke();
                Assert.IsFalse(Directory.Exists(sdir), "confirming must delete the scene");
                Object.DestroyImmediate(cardParentGo);
            }
            finally
            {
                Object.DestroyImmediate(libGo); Object.DestroyImmediate(canvasGo);
                if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            }
        }

        [Test]
        public void MainUI_ShowCapture_FrontsRecordButton()
        {
            var root = new GameObject("capture-front-root");
            try
            {
                var capture = new GameObject("CapturePanel"); capture.transform.SetParent(root.transform);
                var libraryPanel = new GameObject("LibraryPanel"); libraryPanel.transform.SetParent(root.transform);
                var recordGo = new GameObject("RecordButton"); recordGo.transform.SetParent(root.transform);
                recordGo.AddComponent<UnityEngine.UI.Image>();
                var recordBtn = recordGo.AddComponent<Button>();
                var saveGo = new GameObject("SaveButton"); saveGo.transform.SetParent(root.transform);
                saveGo.AddComponent<UnityEngine.UI.Image>();
                var saveBtn = saveGo.AddComponent<Button>();
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.capturePanel = capture; ctrl.libraryPanel = libraryPanel;
                ctrl.recordButton = recordBtn; ctrl.saveButton = saveBtn;
                ctrl.ShowCapture();
                Assert.IsTrue(recordGo.activeSelf, "Record must be visible in Capture mode");
                Assert.Greater(recordGo.transform.GetSiblingIndex(), capture.transform.GetSiblingIndex(),
                    "Record must render in front of the Capture panel");
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ---- Pink replay plane: runtime materials must never get a null shader ----
        [Test]
        public void SceneMaterials_ResolvesShaderAndTintsRenderer()
        {
            Assert.IsNotNull(SceneMaterials.ResolveShader(),
                "a usable scene shader must resolve (Standard/Unlit/Sprites)");
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                SceneMaterials.ApplyColor(go.GetComponent<Renderer>(), new Color(0.16f, 0.2f, 0.26f));
                var mat = go.GetComponent<Renderer>().sharedMaterial;
                Assert.IsNotNull(mat);
                Assert.IsNotNull(mat.shader, "null-shader material renders magenta on Quest");
                Assert.AreEqual(0.16f, mat.color.r, 0.01f);
                Assert.AreEqual(0.26f, mat.color.b, 0.01f);
            }
            finally { Object.DestroyImmediate(go); }
            string graphics = File.ReadAllText(Path.Combine(Application.dataPath, "../ProjectSettings/GraphicsSettings.asset"));
            StringAssert.Contains("fileID: 46", graphics,
                "Standard (46) must be always-included or runtime scene materials strip to pink");
        }

        [Test]
        public void ServerSettings_NoHardcodedSecretsByDefault()
        {
            var settings = ScriptableObject.CreateInstance<ServerSettings>();
            try
            {
                Assert.IsTrue(string.IsNullOrEmpty(settings.serverlessEndpointUrl),
                    "endpoint must be user-entered, never hardcoded");
                Assert.IsTrue(string.IsNullOrEmpty(settings.remotionApiKey),
                    "API key must be user-entered, never hardcoded/committed");
                Assert.IsFalse(settings.HasServerless);
            }
            finally { Object.DestroyImmediate(settings); }
        }

        // ---- SAM models in replay + mode purity (device-reported) ----

        private static byte[] BuildTestGlb()
        {
            // Mirror of Server reconstruction.build_glb: POSITION f32
            // VEC3 + uint32 indices + one PBR baseColorFactor.
            float[] pos = { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f };
            uint[] idx = { 0u, 1u, 2u };
            var bin = new MemoryStream();
            foreach (float f in pos) bin.Write(System.BitConverter.GetBytes(f), 0, 4);
            foreach (uint i in idx) bin.Write(System.BitConverter.GetBytes(i), 0, 4);
            string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0," +
                "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1,\"material\":0}]}]," +
                "\"materials\":[{\"pbrMetallicRoughness\":{\"baseColorFactor\":[0.2,0.45,1.0,1.0]}}]," +
                "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":36}," +
                "{\"buffer\":0,\"byteOffset\":36,\"byteLength\":12}]," +
                "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"}," +
                "{\"bufferView\":1,\"componentType\":5125,\"count\":3,\"type\":\"SCALAR\"}],\"buffers\":[{\"byteLength\":48}]}";
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - jsonBytes.Length % 4) % 4;
            byte[] binBytes = bin.ToArray();
            var glb = new MemoryStream();
            glb.Write(System.BitConverter.GetBytes(0x46546C67u), 0, 4);
            glb.Write(System.BitConverter.GetBytes(2u), 0, 4);
            glb.Write(System.BitConverter.GetBytes(12 + 8 + jsonBytes.Length + jsonPad + 8 + binBytes.Length), 0, 4);
            glb.Write(System.BitConverter.GetBytes(jsonBytes.Length + jsonPad), 0, 4);
            glb.Write(System.BitConverter.GetBytes(0x4E4F534Au), 0, 4);
            glb.Write(jsonBytes, 0, jsonBytes.Length);
            for (int i = 0; i < jsonPad; i++) glb.WriteByte(0x20);
            glb.Write(System.BitConverter.GetBytes(binBytes.Length), 0, 4);
            glb.Write(System.BitConverter.GetBytes(0x004E4942u), 0, 4);
            glb.Write(binBytes, 0, binBytes.Length);
            return glb.ToArray();
        }

        [Test]
        public void GlbMeshLoader_LoadsServerStyleGlb()
        {
            var mesh = GlbMeshLoader.LoadMesh(BuildTestGlb(), out Color color, out string err);
            Assert.IsNotNull(mesh, err);
            Assert.AreEqual(3, mesh.vertexCount);
            Assert.AreEqual(3, mesh.GetIndices(0).Length);
            Assert.AreEqual(1f, mesh.bounds.max.x, 0.001f);
            Assert.AreEqual(1f, mesh.bounds.max.y, 0.001f);
            Assert.AreEqual(0.2f, color.r, 0.01f);
            Assert.AreEqual(0.45f, color.g, 0.01f);
            Assert.AreEqual(1f, color.b, 0.01f);
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void GlbMeshLoader_FlipsInwardMeshRightSideOut()
        {
            // Tetrahedron with deliberately inward winding (the old
            // server sphere bug): invisible from outside if unflipped.
            float[] pos = { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
            uint[] idx = { 0, 1, 2, 0, 3, 1, 0, 2, 3, 1, 3, 2 };
            var bin = new MemoryStream();
            foreach (float f in pos) bin.Write(System.BitConverter.GetBytes(f), 0, 4);
            foreach (uint i in idx) bin.Write(System.BitConverter.GetBytes(i), 0, 4);
            string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0," +
                "\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
                "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0},\"indices\":1}]}]," +
                "\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":48}," +
                "{\"buffer\":0,\"byteOffset\":48,\"byteLength\":48}]," +
                "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":4,\"type\":\"VEC3\"}," +
                "{\"bufferView\":1,\"componentType\":5125,\"count\":12,\"type\":\"SCALAR\"}],\"buffers\":[{\"byteLength\":96}]}";
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int jsonPad = (4 - jsonBytes.Length % 4) % 4;
            byte[] binBytes = bin.ToArray();
            var glb = new MemoryStream();
            glb.Write(System.BitConverter.GetBytes(0x46546C67u), 0, 4);
            glb.Write(System.BitConverter.GetBytes(2u), 0, 4);
            glb.Write(System.BitConverter.GetBytes(12 + 8 + jsonBytes.Length + jsonPad + 8 + binBytes.Length), 0, 4);
            glb.Write(System.BitConverter.GetBytes(jsonBytes.Length + jsonPad), 0, 4);
            glb.Write(System.BitConverter.GetBytes(0x4E4F534Au), 0, 4);
            glb.Write(jsonBytes, 0, jsonBytes.Length);
            for (int i = 0; i < jsonPad; i++) glb.WriteByte(0x20);
            glb.Write(System.BitConverter.GetBytes(binBytes.Length), 0, 4);
            glb.Write(System.BitConverter.GetBytes(0x004E4942u), 0, 4);
            glb.Write(binBytes, 0, binBytes.Length);

            var mesh = GlbMeshLoader.LoadMesh(glb.ToArray(), out _, out string err);
            Assert.IsNotNull(mesh, err);
            var verts = mesh.vertices;
            var indices = mesh.GetIndices(0);
            var center = Vector3.zero;
            foreach (var v in verts) center += v;
            center /= verts.Length; // bounds.center lies outside a slanted face
            int outward = 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = verts[indices[i]]; var b = verts[indices[i + 1]]; var c = verts[indices[i + 2]];
                var n = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(n, (a + b + c) / 3f - center) > 0f) outward++;
            }
            Assert.AreEqual(indices.Length / 3, outward,
                "every face must point outward or the mesh renders invisible");
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void ScenePackage_ParseAssets_ReturnsSamModels()
        {
            byte[] glb = BuildTestGlb();
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] timelinePkg, out _);
                // Rebuild a full server-style package: scene.json + timeline + GLBs.
                using (var src = new ZipArchive(new MemoryStream(timelinePkg), ZipArchiveMode.Read))
                {
                    var entry = zip.CreateEntry("timeline.json");
                    using (var w = entry.Open())
                    using (var r = src.GetEntry("timeline.json").Open()) r.CopyTo(w);
                }
                var scene = zip.CreateEntry("scene.json");
                using (var w = new StreamWriter(scene.Open()))
                    w.Write("{\"format\":\"remotion-1\",\"assets\":[" +
                        "{\"track_id\":\"person_01\",\"kind\":\"person\",\"model\":\"assets/person_01.glb\",\"sam_backend\":\"sam2\",\"reconstruction_backend\":\"trajectory-mesh\"}," +
                        "{\"track_id\":\"capture_object\",\"kind\":\"object\",\"model\":\"assets/capture_object.glb\",\"sam_backend\":\"sam2\",\"reconstruction_backend\":\"trajectory-mesh\"}]}");
                foreach (var name in new[] { "assets/person_01.glb", "assets/capture_object.glb" })
                {
                    var e = zip.CreateEntry(name);
                    using (var w = e.Open()) w.Write(glb, 0, glb.Length);
                }
            }
            byte[] pkg = ms.ToArray();
            Assert.IsTrue(ScenePackage.TryParseAssets(pkg, out var assets, out string err), err);
            Assert.AreEqual(2, assets.Count);
            Assert.AreEqual("person", assets[0].Info.kind);
            Assert.AreEqual(glb.Length, assets[0].GlbBytes.Length);
            // On-device packages (no assets) parse to an empty list.
            ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] plain, out _);
            Assert.IsTrue(ScenePackage.TryParseAssets(plain, out var none, out _));
            Assert.AreEqual(0, none.Count);
        }

        [Test]
        public void Replay_EnterSession_ShowsSamMeshesNotPlaceholders()
        {
            string dir = Path.Combine(Path.GetTempPath(), "remotion-sam-replay-test", "sam1");
            if (Directory.Exists(dir)) Directory.Delete(Path.GetDirectoryName(dir), true);
            Directory.CreateDirectory(dir);
            var root = new GameObject("sam-replay-root");
            try
            {
                // Server-style package for a plain recorded capture.
                var capture = new SessionTimeline { action_sequence = "recorded_action", duration_seconds = 1f };
                capture.frames.Add(new FrameData { timestamp = 0f, headPosition = new Vector3(0f, 1.5f, 0f),
                    leftHandJoints = new[] { Vector3.zero }, rightHandJoints = new[] { new Vector3(0.3f, 1.2f, 0.5f) } });
                var ms = new MemoryStream();
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    ScenePackage.TryCreatePackage(capture, out byte[] tp, out _);
                    using (var src = new ZipArchive(new MemoryStream(tp), ZipArchiveMode.Read))
                    {
                        var entry = zip.CreateEntry("timeline.json");
                        using (var w = entry.Open())
                        using (var r = src.GetEntry("timeline.json").Open()) r.CopyTo(w);
                    }
                    var scene = zip.CreateEntry("scene.json");
                    using (var w = new StreamWriter(scene.Open()))
                        w.Write("{\"format\":\"remotion-1\",\"assets\":[" +
                            "{\"track_id\":\"person_01\",\"kind\":\"person\",\"model\":\"assets/person_01.glb\"}," +
                            "{\"track_id\":\"capture_object\",\"kind\":\"object\",\"label\":\"Mouse\",\"model\":\"assets/capture_object.glb\"}," +
                            "{\"track_id\":\"keyboard_02\",\"kind\":\"object\",\"label\":\"Keyboard\",\"model\":\"assets/keyboard_02.glb\"}]}");
                    byte[] glb = BuildTestGlb();
                    foreach (var name in new[] { "assets/person_01.glb", "assets/capture_object.glb", "assets/keyboard_02.glb" })
                    {
                        var e = zip.CreateEntry(name);
                        using (var w = e.Open()) w.Write(glb, 0, glb.Length);
                    }
                }
                File.WriteAllBytes(Path.Combine(dir, "scene.remotion"), ms.ToArray());

                var ctrl = root.AddComponent<ReplayController>();
                Assert.IsTrue(ctrl.EnterSession("sam1", dir, out string err), err);
                Assert.IsNull(FindDeep(ctrl.SceneRootForTests, "Person"),
                    "no Person in a capture scene — only the important object");
                Assert.IsNotNull(ctrl.Player.grabbedObject.Find("SamObjectMesh"),
                    "SAM object mesh must ride the grabbed-object rig");
                var extras = ctrl.SceneRootForTests.Find("IdentifiedObjects");
                Assert.IsNotNull(extras, "extra identified objects must stand in the scene");
                Assert.IsNotNull(extras.Find("Keyboard"),
                    "identified objects keep their AI labels in the scene");
                var placeholder = ctrl.Player.grabbedObject.GetComponent<Renderer>();
                Assert.IsFalse(placeholder.enabled, "placeholder cube must hide when SAM mesh exists");
            }
            finally { Object.DestroyImmediate(root); if (Directory.Exists(dir)) Directory.Delete(Path.GetDirectoryName(dir), true); }
        }

        [Test]
        public void MainUI_ShowReplay_ClosesVideoPanel()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-video-replay-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            Directory.CreateDirectory(Path.Combine(rootDir, "cap1"));
            var root = new GameObject("video-replay-root");
            try
            {
                var libGo = new GameObject("vr-lib");
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir;
                var playerGo = new GameObject("vr-player");
                var player = playerGo.AddComponent<VideoPlaybackController>();
                player.panel = new GameObject("VideoPlayerPanel");
                var hudGo = new GameObject("vr-hud");
                var hud = hudGo.AddComponent<ReplayHudController>();
                hud.panel = new GameObject("ReplayHudPanel");
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.library = lib; ctrl.videoPlayer = player; ctrl.replayHud = hud;
                ctrl.ShowVideo("cap1");
                Assert.IsTrue(player.panel.activeSelf, "video must open in Video mode");
                ctrl.ShowReplay("cap1");
                Assert.AreEqual(ReMotionMainUIController.AppMode.Replay, ctrl.CurrentMode);
                Assert.IsFalse(player.panel.activeSelf,
                    "video must never leak into the virtual scene (Replay mode)");
                Object.DestroyImmediate(libGo); Object.DestroyImmediate(playerGo);
                Object.DestroyImmediate(hudGo); Object.DestroyImmediate(player.panel);
                Object.DestroyImmediate(hud.panel);
            }
            finally { Object.DestroyImmediate(root); if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); }
        }

        [Test]
        public void MainUI_DeviceHierarchy_LibraryGridHidesOutsideLibrary()
        {
            // Exact device hierarchy: SceneList hangs straight off the
            // canvas (no SceneListViewport exists in the saved scene) and
            // the controller starts with libraryPanel unassigned. The old
            // name lookup missed, so the video grid showed in Capture.
            var canvasGo = new GameObject("LibraryCanvas_WorldSpace");
            var sceneList = new GameObject("SceneList");
            var libGo = new GameObject("device-lib");
            var root = new GameObject("device-ctrl-root");
            try
            {
                canvasGo.AddComponent<Canvas>();
                sceneList.transform.SetParent(canvasGo.transform, false);
                var lib = libGo.AddComponent<LibraryManager>();
                lib.cardParent = sceneList.transform;
                var recordBtn = ReMotionUiTheme.Button(canvasGo.transform, "RecordButton", "Record",
                    ReMotionUiTheme.ButtonStyle.Record, 17);
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.library = lib; ctrl.recordButton = recordBtn;
                Assert.IsNull(ctrl.libraryPanel, "precondition: panel unresolved like on device");
                ctrl.ShowCapture();
                Assert.IsNotNull(ctrl.libraryPanel, "controller must resolve the real card grid");
                Assert.IsFalse(sceneList.activeSelf, "video grid must be hidden in Capture");
                ctrl.ShowLibrary();
                Assert.IsTrue(sceneList.activeSelf, "video grid belongs to Library mode");
                ctrl.ShowCapture();
                Assert.IsFalse(sceneList.activeSelf, "returning to Capture hides the grid again");
                Object.DestroyImmediate(libGo);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(canvasGo); }
        }

        [Test]
        public void ReplayMode_OnlyLibraryAndGodModeVisible()
        {
            string rootDir = Path.Combine(Path.GetTempPath(), "remotion-replay-chrome-test");
            if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true);
            Directory.CreateDirectory(Path.Combine(rootDir, "cap1"));
            var root = new GameObject("replay-chrome-root");
            try
            {
                var libGo = new GameObject("rc-lib");
                var lib = libGo.AddComponent<LibraryManager>();
                lib.LibraryRootOverride = rootDir;
                var playerGo = new GameObject("rc-player");
                var player = playerGo.AddComponent<VideoPlaybackController>();
                player.panel = new GameObject("VideoPlayerPanel");
                var hudGo = new GameObject("rc-hud");
                var hud = hudGo.AddComponent<ReplayHudController>();
                hud.panel = new GameObject("ReplayHudPanel");
                hud.backButton = ReMotionUiTheme.Button(hud.panel.transform, "BackButton", "Library",
                    ReMotionUiTheme.ButtonStyle.Secondary, 13);
                hud.godModeButton = ReMotionUiTheme.Button(hud.panel.transform, "GodModeButton", "God Mode",
                    ReMotionUiTheme.ButtonStyle.Secondary, 13);
                hud.playPauseButton = ReMotionUiTheme.Button(hud.panel.transform, "PlayPauseButton", "Pause",
                    ReMotionUiTheme.ButtonStyle.Secondary, 13);
                hud.retrainButton = ReMotionUiTheme.Button(hud.panel.transform, "RetrainButton", "Retrain",
                    ReMotionUiTheme.ButtonStyle.Secondary, 13);
                var capTab = ReMotionUiTheme.Button(root.transform, "CaptureTabButton", "Capture",
                    ReMotionUiTheme.ButtonStyle.Secondary, 13);
                var libTab = ReMotionUiTheme.Button(root.transform, "LibraryTabButton", "Library",
                    ReMotionUiTheme.ButtonStyle.Secondary, 13);
                var serverBtn = ReMotionUiTheme.Button(root.transform, "ServerSettingsButton", "Server",
                    ReMotionUiTheme.ButtonStyle.Secondary, 13);
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.library = lib; ctrl.videoPlayer = player; ctrl.replayHud = hud;
                ctrl.captureTabButton = capTab; ctrl.libraryTabButton = libTab;
                ctrl.serverSettingsButton = serverBtn;
                ctrl.ShowReplay("cap1");
                Assert.AreEqual(ReMotionMainUIController.AppMode.Replay, ctrl.CurrentMode);
                Assert.IsTrue(hud.panel.activeSelf, "in-scene menu must exist");
                Assert.IsTrue(hud.backButton.gameObject.activeSelf, "Library exit must be in the scene");
                Assert.IsTrue(hud.godModeButton.gameObject.activeSelf, "God Mode must be in the scene");
                Assert.IsFalse(hud.playPauseButton.gameObject.activeSelf, "transport controls stay out of the scene");
                Assert.IsFalse(hud.retrainButton.gameObject.activeSelf, "only Library + God Mode in the scene");
                Assert.IsFalse(capTab.gameObject.activeSelf, "menu tabs must not float in the scene");
                Assert.IsFalse(libTab.gameObject.activeSelf, "menu tabs must not float in the scene");
                Assert.IsFalse(serverBtn.gameObject.activeSelf, "Server settings belong to the menus");
                Object.DestroyImmediate(libGo); Object.DestroyImmediate(playerGo);
                Object.DestroyImmediate(hudGo); Object.DestroyImmediate(player.panel);
            }
            finally { Object.DestroyImmediate(root); if (Directory.Exists(rootDir)) Directory.Delete(rootDir, true); }
        }

        [Test]
        public void HandRotations_RoundTrip_AndDriveExactBonePose()
        {
            // Timeline JSON must carry joint orientations end to end.
            var frame = new FrameData
            {
                timestamp = 0f,
                rightHandJoints = new[] { Vector3.zero, Vector3.one },
                rightHandRotations = new[] { Quaternion.identity, Quaternion.Euler(0f, 45f, 0f) },
            };
            var timeline = new SessionTimeline { action_sequence = "recorded_action", duration_seconds = 1f };
            timeline.frames.Add(frame);
            var back = JsonUtility.FromJson<SessionTimeline>(JsonUtility.ToJson(timeline));
            Assert.AreEqual(2, back.frames[0].rightHandRotations.Length);
            Assert.AreEqual(45f, back.frames[0].rightHandRotations[1].eulerAngles.y, 0.5f);
            // Old sessions without the field still parse (empty arrays).
            var legacy = JsonUtility.FromJson<SessionTimeline>(
                "{\"action_sequence\":\"recorded_action\",\"duration_seconds\":1,\"frames\":[" +
                "{\"timestamp\":0,\"rightHandJoints\":[{\"x\":0,\"y\":0,\"z\":0}]}]}");
            Assert.AreEqual(0, legacy.frames[0].rightHandRotations.Length);

            // Recorded joint motion is retained with the Meta mesh's axis conversion.
            var prefab = Resources.Load<GameObject>("Hands/OpenXRLeftHand");
            var frameRoot = new GameObject("rot-frame-root");
            var instance = Object.Instantiate(prefab, frameRoot.transform);
            try
            {
                var driver = instance.AddComponent<HandModelDriver>();
                driver.Bind(instance);
                int count = 27;
                var joints = new Vector3[count];
                var rots = new Quaternion[count];
                for (int i = 0; i < count; i++) { joints[i] = Vector3.zero; rots[i] = Quaternion.Euler(0f, 45f, 0f); }
                Assert.IsTrue(driver.Pose(joints, rots, frameRoot.transform));
                Transform wristBone = null;
                foreach (var t in instance.GetComponentsInChildren<Transform>())
                    if (t.name == "XRHand_Wrist") wristBone = t;
                Assert.IsNotNull(wristBone);
                Assert.AreEqual(225f, wristBone.rotation.eulerAngles.y, 1f,
                    "Meta mesh uses the opposite local X/Z axes from Unity XR Hands");
            }
            finally { Object.DestroyImmediate(frameRoot); }
        }

        [Test]
        public void HandModelDriver_BindsBones_AndPosesFromJoints()
        {
            var prefab = Resources.Load<GameObject>("Hands/OpenXRLeftHand");
            Assert.IsNotNull(prefab, "shipped OpenXR left hand model must be in Resources");
            var frameRoot = new GameObject("hand-frame-root");
            var instance = Object.Instantiate(prefab, frameRoot.transform);
            try
            {
                var driver = instance.AddComponent<HandModelDriver>();
                driver.Bind(instance);
                Assert.IsTrue(driver.IsReady, "hand model must expose XRHand bones");
                Assert.GreaterOrEqual(driver.BoneCount, 20);
                // Identity frame root: bone world positions must land on joints.
                var timeline = DemoTimeline.Create();
                var joints = timeline.frames[0].leftHandJoints;
                Assert.IsTrue(driver.Pose(joints, null, frameRoot.transform));
                int wrist = HandPoseRecorder.JointIndexOf(UnityEngine.XR.Hands.XRHandJointID.Wrist);
                Transform wristBone = null;
                foreach (var t in instance.GetComponentsInChildren<Transform>())
                    if (t.name == "XRHand_Wrist") wristBone = t;
                Assert.IsNotNull(wristBone);
                Assert.Less(Vector3.Distance(wristBone.position, joints[wrist]), 0.01f,
                    "wrist bone must sit on the recorded wrist joint");
            }
            finally { Object.DestroyImmediate(frameRoot); }
        }

        [Test]
        public void Dwell_UnconfiguredDefaultsOff_ExplicitOptInPreserved()
        {
            PlayerPrefs.DeleteKey("remotion_dwell_enabled");
            Assert.IsFalse(AccessibilitySettings.DwellEnabled,
                "unconfigured installs must use native pinch, not dwell");
            Assert.IsFalse(AccessibilitySettings.DwellConfigured);
            AccessibilitySettings.DwellEnabled = true;
            try
            {
                Assert.IsTrue(AccessibilitySettings.DwellEnabled,
                    "an explicit accessibility opt-in is preserved");
                Assert.IsTrue(AccessibilitySettings.DwellConfigured);
            }
            finally { AccessibilitySettings.DwellEnabled = false; }
        }

        [Test]
        public void Theme_NormalizeButtonLabelStretchesAndDisablesRaycast()
        {
            var go = new GameObject("label-normalize");
            try
            {
                var button = ReMotionUiTheme.Button(go.transform, "B", "Go",
                    ReMotionUiTheme.ButtonStyle.Primary, 15);
                var label = button.GetComponentInChildren<Text>();
                label.raycastTarget = true;
                var rt = label.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(700f, 60f); // legacy oversized
                ReMotionUiTheme.NormalizeButtonLabel(button);
                Assert.IsFalse(label.raycastTarget,
                    "decorative label text must not capture the ray");
                Assert.AreEqual(Vector2.zero, rt.anchorMin);
                Assert.AreEqual(Vector2.one, rt.anchorMax);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void MainScene_RecordButtonIsTopRaycastTargetAndWired()
        {
            // Real Main hierarchy route (no onClick bypass): the visible
            // Record must be the controller's button and the top uGUI
            // raycast hit at its centre, or a native pinch cannot land.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/Scenes/Main.unity",
                UnityEditor.SceneManagement.OpenSceneMode.Single);
            try
            {
                var recordGo = GameObject.Find("RecordButton");
                Assert.IsNotNull(recordGo, "Main must contain RecordButton");
                var recordBtn = recordGo.GetComponent<Button>();
                Assert.IsNotNull(recordBtn);
                var canvas = recordGo.GetComponentInParent<Canvas>();
                Assert.IsNotNull(canvas, "Record must live under the menu canvas");

                var recorder = Object.FindFirstObjectByType<SessionRecorder>();
                Assert.IsNotNull(recorder, "Main must contain the SessionRecorder");
                var ctrlGo = new GameObject("RouteCheckController");
                var ctrl = ctrlGo.AddComponent<ReMotionMainUIController>();
                ctrl.recorder = recorder;
                ctrl.recordButton = recordBtn;
                ctrl.saveButton = GameObject.Find("SaveButton")?.GetComponent<Button>();
                var statusGo = GameObject.Find("StatusText");
                ctrl.statusText = statusGo != null ? statusGo.GetComponent<Text>() : null;
                ctrl.EnsureUi();
                ctrl.WireHandlers();
                ctrl.ShowCapture();
                Assert.AreSame(recordBtn, ctrl.recordButton,
                    "controller must wire the visible Record instance");

                var raycaster = canvas.GetComponent<GraphicRaycaster>();
                Assert.IsNotNull(raycaster);
                // Deterministic event camera placed in front of the
                // canvas (the route under test is uGUI raycast order,
                // not the headset's head pose).
                var camGo = new GameObject("RouteCheckCamera");
                var cam = camGo.AddComponent<Camera>();
                cam.transform.position = canvas.transform.position
                    + canvas.transform.forward * -1.5f + canvas.transform.up * 0.1f;
                cam.transform.LookAt(canvas.transform.position);
                canvas.worldCamera = cam;
                var rt = recordGo.GetComponent<RectTransform>();
                Vector3[] corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                Vector3 centre = (corners[0] + corners[2]) * 0.5f;
                Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, centre);
                // GraphicRaycaster filters on rendered depth (-1 in
                // EditMode), so validate at the Graphic level: Record's
                // own graphic must hit-test true at its centre, and no
                // graphic later in hierarchy (render) order may also
                // hit there — that later graphic is what a pinch ray
                // would strike first on device.
                var recordGraphic = recordGo.GetComponent<Graphic>();
                Assert.IsNotNull(recordGraphic);
                Assert.IsTrue(recordGraphic.Raycast(screen, cam),
                    "Record graphic does not hit-test at its own centre");
                // Deterministic containment (canvas-local): does another
                // graphic's rect actually contain Record's centre?
                System.Func<RectTransform, bool> ContainsCentre = other =>
                {
                    Vector3 local = other.InverseTransformPoint(centre);
                    Rect r = other.rect;
                    return local.x >= r.xMin && local.x <= r.xMax
                        && local.y >= r.yMin && local.y <= r.yMax;
                };
                Assert.IsTrue(ContainsCentre(rt), "Record centre outside its own rect");
                System.Func<Transform, string> orderKey = t =>
                {
                    var parts = new System.Collections.Generic.List<string>();
                    for (var x = t; x != null; x = x.parent)
                        parts.Add(x.GetSiblingIndex().ToString("D6"));
                    parts.Reverse();
                    return string.Join("/", parts);
                };
                string recordOrder = orderKey(recordGo.transform);
                foreach (var g in canvas.GetComponentsInChildren<Graphic>(false))
                {
                    if (g == recordGraphic || !g.raycastTarget) continue;
                    if (g.transform.IsChildOf(recordGo.transform)) continue;
                    var grt = g.transform as RectTransform;
                    if (grt == null || !ContainsCentre(grt)) continue;
                    Assert.Less(string.CompareOrdinal(orderKey(g.transform), recordOrder), 0,
                        g.gameObject.name + " renders after Record and contains its centre — it would intercept the pinch");
                }
                Object.DestroyImmediate(ctrlGo);
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
            }
        }

        [Test]
        public void RecordStop_IdleStopHidden_DuplicateStart_NoVideoNoUpload()
        {
            var root = new GameObject("record-stop-root");
            var recorderGo = new GameObject("record-stop-recorder");
            try
            {
                var statusGo = new GameObject("StatusText");
                statusGo.transform.SetParent(root.transform);
                var status = statusGo.AddComponent<Text>();
                var recordBtn = ReMotionUiTheme.Button(root.transform, "RecordButton", "Record",
                    ReMotionUiTheme.ButtonStyle.Record, 17);
                var saveBtn = ReMotionUiTheme.Button(root.transform, "SaveButton", "Save",
                    ReMotionUiTheme.ButtonStyle.Primary, 17);
                var recorder = recorderGo.AddComponent<SessionRecorder>();
                var ctrl = root.AddComponent<ReMotionMainUIController>();
                ctrl.recordButton = recordBtn; ctrl.saveButton = saveBtn;
                ctrl.statusText = status; ctrl.recorder = recorder;
                ctrl.EnsureUi(); ctrl.WireHandlers();

                // Idle Capture: Record available, Stop does not exist.
                ctrl.ShowCapture();
                Assert.IsTrue(recordBtn.gameObject.activeInHierarchy);
                Assert.IsFalse(saveBtn.gameObject.activeInHierarchy,
                    "Stop must be hidden before recording starts");
                string idleStatus = status.text;
                saveBtn.onClick.Invoke(); // cannot fire while hidden, but guard anyway
                Assert.AreEqual(SessionRecorder.CaptureState.Idle, recorder.State);

                // Duplicate Record during Starting changes nothing.
                recordBtn.onClick.Invoke();
                string firstId = recorder.CurrentSessionId;
                Assert.AreEqual(SessionRecorder.CaptureState.Starting, recorder.State);
                recordBtn.onClick.Invoke();
                Assert.AreEqual(firstId, recorder.CurrentSessionId,
                    "duplicate start must not replace the session");

                // Delayed readiness: still Starting, Stop still hidden.
                Assert.IsFalse(saveBtn.gameObject.activeInHierarchy);
                recorder.NotifyCameraReady();
                Assert.AreEqual(SessionRecorder.CaptureState.Recording, recorder.State);
                Assert.IsTrue(saveBtn.gameObject.activeInHierarchy,
                    "Stop appears only in Recording");

                // No playable video: Failed, stays Capture, no Upload.
                saveBtn.onClick.Invoke();
                Assert.AreEqual(SessionRecorder.CaptureState.Failed, recorder.State);
                Assert.IsFalse(recorder.LastSaveSucceeded);
                Assert.AreEqual(ReMotionMainUIController.AppMode.Capture, ctrl.CurrentMode);
                StringAssert.Contains("Capture failed", status.text);
                var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(
                    Path.Combine(SessionRecorder.SessionFolder(firstId), "metadata.json")));
                Assert.AreEqual("Failed", meta.status,
                    "a capture with no playable video must never enter Processing/upload");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(recorderGo);
            }
        }

        [Test]
        public void ViewRoots_ExactlyOneActiveAcrossTransitionMatrix()
        {
            var canvasGo = new GameObject("matrix-canvas");
            canvasGo.AddComponent<Canvas>();
            canvasGo.AddComponent<GraphicRaycaster>();
            var ctrlGo = new GameObject("matrix-ctrl");
            try
            {
                var recorder = ctrlGo.AddComponent<SessionRecorder>();
                var statusGo = new GameObject("StatusText");
                statusGo.transform.SetParent(canvasGo.transform);
                var status = statusGo.AddComponent<Text>();
                var recordGo = new GameObject("RecordButton");
                recordGo.transform.SetParent(canvasGo.transform);
                var recordBtn = recordGo.AddComponent<Button>();
                var saveGo = new GameObject("SaveButton");
                saveGo.transform.SetParent(canvasGo.transform);
                var saveBtn = saveGo.AddComponent<Button>();
                var ctrl = ctrlGo.AddComponent<ReMotionMainUIController>();
                ctrl.recorder = recorder;
                ctrl.recordButton = recordBtn;
                ctrl.saveButton = saveBtn;
                ctrl.statusText = status;
                ctrl.EnsureUi();
                Assert.IsNotNull(ctrl.captureRoot);
                Assert.IsNotNull(ctrl.libraryRoot);
                Assert.IsNotNull(ctrl.serverRoot);

                void AssertOnly(GameObject only, string step)
                {
                    foreach (var root in new[] { ctrl.captureRoot, ctrl.libraryRoot, ctrl.serverRoot })
                        Assert.AreEqual(root == only, root.activeSelf, step + ": " + root.name);
                }

                ctrl.ShowCapture(); AssertOnly(ctrl.captureRoot, "capture");
                Assert.IsFalse(recordBtn.gameObject.activeInHierarchy == false,
                    "record lives in the active Capture root");
                ctrl.ShowServer(); AssertOnly(ctrl.serverRoot, "capture->server");
                Assert.IsFalse(recordBtn.gameObject.activeInHierarchy,
                    "capture controls must be inactive in Server");
                ctrl.ShowCapture(); AssertOnly(ctrl.captureRoot, "server->capture");
                ctrl.ShowLibrary(); AssertOnly(ctrl.libraryRoot, "capture->library");
                ctrl.ShowCapture(); AssertOnly(ctrl.captureRoot, "library->capture");
                ctrl.ShowServer(); AssertOnly(ctrl.serverRoot, "capture->server 2");
                ctrl.ShowLibrary(); AssertOnly(ctrl.libraryRoot, "server->library");
                ctrl.ShowServer(); AssertOnly(ctrl.serverRoot, "library->server");
                // Repeated selection of the current tab never toggles off.
                ctrl.ShowServer(); AssertOnly(ctrl.serverRoot, "server repeat");
                Assert.IsTrue(ctrl.serverSettingsPanel.activeInHierarchy);
                // Server save stays in Server.
                ctrl.SaveServerSettings();
                AssertOnly(ctrl.serverRoot, "server save");
                // Content ownership: record under Capture, panel under Server.
                Assert.AreEqual("CaptureRoot", recordBtn.transform.parent.name);
                Assert.AreEqual("ServerRoot", ctrl.serverSettingsPanel.transform.parent.name);
                // Idempotent EnsureUi: still exactly one root of each name.
                ctrl.EnsureUi();
                int roots = 0;
                foreach (Transform child in canvasGo.transform)
                    if (child.name.EndsWith("Root")) roots++;
                Assert.AreEqual(3, roots, "EnsureUi must not duplicate view roots");
            }
            finally
            {
                Object.DestroyImmediate(ctrlGo);
                Object.DestroyImmediate(canvasGo);
                // EnsureUi spawns these as separate scene objects; do
                // not leak them into other fixtures.
                var vp = GameObject.Find("VideoPlaybackController");
                if (vp != null) Object.DestroyImmediate(vp);
                var hud = GameObject.Find("ReplayHudController");
                if (hud != null) Object.DestroyImmediate(hud);
            }
        }

        [Test]
        public void PinchChainValidation_FailsOnNullSurfaceNotCounts()
        {
            var go = new GameObject("pinch-validation-canvas");
            try
            {
                var canvas = go.AddComponent<Canvas>();
                go.AddComponent<GraphicRaycaster>();
                // No RayInteractable/surface: the runtime readiness check
                // must fail on the null surface, never pass on counts.
                // (The editor validator names each missing reference and
                // is exercised by the pinch setup run on Main.)
                Assert.IsFalse(PinchMenuInteraction.CanvasSurfaceBound(canvas),
                    "a canvas with no surface must not report pinch-ready");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void RetrainingTrainer_ScoresMatchAndMismatch()
        {
            var go = new GameObject("retrain-test");
            try
            {
                var trainer = go.AddComponent<RetrainingTrainer>();
                var reference = new FrameData
                {
                    rightHandJoints = new[] { new Vector3(0.3f, 1.2f, 0.5f), new Vector3(0.35f, 1.25f, 0.5f) },
                    leftHandJoints = System.Array.Empty<Vector3>(),
                };
                var matching = new FrameData
                {
                    rightHandJoints = new[] { new Vector3(0.3f, 1.2f, 0.5f), new Vector3(0.35f, 1.25f, 0.5f) },
                    leftHandJoints = System.Array.Empty<Vector3>(),
                };
                Assert.Greater(trainer.Evaluate(matching, reference), 0.9f,
                    "identical joints must score near perfect");
                StringAssert.Contains("Excellent", trainer.LastFeedback);
                var far = new FrameData
                {
                    rightHandJoints = new[] { new Vector3(1.3f, 1.2f, 0.5f), new Vector3(1.35f, 1.25f, 0.5f) },
                    leftHandJoints = System.Array.Empty<Vector3>(),
                };
                Assert.Less(trainer.Evaluate(far, reference), 0.2f,
                    "a metre off must score near zero");
                var untracked = new FrameData
                {
                    rightHandJoints = System.Array.Empty<Vector3>(),
                    leftHandJoints = System.Array.Empty<Vector3>(),
                };
                Assert.AreEqual(0f, trainer.Evaluate(untracked, reference));
                StringAssert.Contains("not tracked", trainer.LastFeedback);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void GodMode_ApplyInputRotatesAndResetRestores()
        {
            var root = new GameObject("god-root");
            var ctrlGo = new GameObject("god-ctrl");
            try
            {
                var ctrl = ctrlGo.AddComponent<GodModeController>();
                ctrl.sceneRoot = root.transform;
                ctrl.SetGodMode(true);
                ctrl.ApplyInput(new Vector2(1f, 0f), 0.5f);
                Assert.Greater(Mathf.Abs(root.transform.rotation.eulerAngles.y), 1f,
                    "stick input must rotate the diorama");
                ctrl.ResetView();
                Assert.AreEqual(0f, root.transform.rotation.eulerAngles.y, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(ctrlGo);
            }
        }

        [Test]
        public void Dwell_ConfirmDeleteIsExcludedByNameAndPolicy()
        {
            // Regression: "ConfirmDeleteButton" does not start with
            // "Delete", so the old name check let dwell fire deletes.
            Assert.IsTrue(DwellClick.IsDwellExcluded("ConfirmDeleteButton"));
            Assert.IsTrue(DwellClick.IsDwellExcluded("DeleteButton"));
            Assert.IsFalse(DwellClick.IsDwellExcluded("LibraryTabButton"));
        }

        [Test]
        public void ReplayPlayer_SeekRebuildsGrabStateDeterministically()
        {
            var go = new GameObject("replay-seek");
            try
            {
                var player = go.AddComponent<ReplayPlayer>();
                var obj = new GameObject("obj");
                var hand = new GameObject("hand");
                player.grabbedObject = obj.transform;
                player.rightHandGhost = hand.transform;
                var timeline = new SessionTimeline { duration_seconds = 1f };
                for (int i = 0; i <= 10; i++)
                    timeline.frames.Add(new FrameData
                    {
                        timestamp = i * 0.1f,
                        rightHandJoints = new[] { new Vector3(i * 0.1f, 0f, 0f) },
                        rightTracked = true,
                    });
                timeline.grab_events.Add(new GrabEvent
                    { timestamp = 0.2f, hand = "right", objectId = "o", type = "grab" });
                timeline.grab_events.Add(new GrabEvent
                    { timestamp = 0.6f, hand = "right", objectId = "o", type = "release" });
                player.Load(timeline);
                player.Scrub(0.3f);
                Assert.AreSame(hand.transform, obj.transform.parent,
                    "seek past grab must hold the object");
                player.Scrub(0.8f);
                Assert.AreNotSame(hand.transform, obj.transform.parent,
                    "seek past release must release the object");
                player.Scrub(0.3f);
                Assert.AreSame(hand.transform, obj.transform.parent,
                    "seeking back must rebuild the held state");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
