#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using ReMotionXR.Capture;
using ReMotionXR.Core;
using ReMotionXR.Library;
using ReMotionXR.Replay;

namespace ReMotionXR.Editor
{
    /// <summary>
    /// Builds a runnable Main scene: VR camera rig + World Space Library
    /// Canvas with Record/Save buttons and a status label.
    /// Menu: ReMotion XR / Setup Main Scene.
    /// If Meta XR SDK (OVRCameraRig / XR Origin) is present it is used;
    /// otherwise a clearly-named VRCameraRig_Placeholder is created.
    /// Compiles in a clean Unity install — no Meta SDK required.
    /// All Meta/MRUK/Passthrough code stays behind #if REMOTION_META_SDK.
    /// </summary>
    public static class ReMotionSceneSetup
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("ReMotion XR/Setup Main Scene")]
        public static void SetupMainScene()
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects,
                                                    NewSceneMode.Single);

            var settings = ScriptableObject.CreateInstance<ServerSettings>();
            Directory.CreateDirectory("Assets/Settings");
            AssetDatabase.CreateAsset(settings, "Assets/Settings/ServerSettings.asset");

            var autoGo = new GameObject("AutoProcessor");
            var auto = autoGo.AddComponent<AutoProcessor>();
            auto.settings = settings;

            var recorderGo = new GameObject("SessionRecorder");
            var recorder = recorderGo.AddComponent<SessionRecorder>();
            recorder.handRecorder = recorderGo.AddComponent<HandPoseRecorder>();
            recorder.cameraCapture = recorderGo.AddComponent<QuestCameraCapture>();
            recorder.autoProcessor = auto;
            Transform head = CreateVrRig();
            if (head != null) recorder.headTransform = head;

            var libraryGo = new GameObject("LibraryManager");
            var library = libraryGo.AddComponent<LibraryManager>();
            library.autoProcessor = auto;

            // Replay rig (ghost hands + player + God Mode) for Enter Scene.
            var replayGo = new GameObject("ReplayController");
            var replay = replayGo.AddComponent<ReplayController>();
            replay.EnsureBuilt();
            library.replayController = replay;

            // World Space VR Canvas ~1.2m in front of the user.
            var canvasGo = new GameObject("LibraryCanvas_WorldSpace");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = head != null ? head.GetComponent<Camera>() : null;
            canvasGo.AddComponent<CanvasScaler>();
            canvasGo.AddComponent<GraphicRaycaster>();
            var canvasRt = canvasGo.GetComponent<RectTransform>();
            canvasRt.sizeDelta = new Vector2(1200f, 700f);
            canvasRt.position = new Vector3(0f, 1.5f, 1.2f);
            canvasRt.localScale = Vector3.one * 0.0012f; // VR-readable size

            // XR UI input path: an EventSystem with an input module.
            // A world-space Canvas + GraphicRaycaster alone receives nothing.
            XrUiInputSetup.EnsureEventSystem();

            // Clean themed shell: backdrop, header, actions, scrollable list.
            var backdrop = ReMotionUiTheme.MakePanel(canvasGo.transform, "Backdrop",
                ReMotionUiTheme.Background);
            var backdropRt = backdrop.GetComponent<RectTransform>();
            backdropRt.sizeDelta = new Vector2(1160f, 660f);
            backdropRt.anchoredPosition = Vector2.zero;
            backdrop.transform.SetAsFirstSibling();

            var title = ReMotionUiTheme.Label(canvasGo.transform, "TitleText",
                "ReMotion XR", 36, ReMotionUiTheme.TextPrimary, TextAnchor.MiddleLeft);
            Place(title.transform, new Vector2(-520f, 285f), new Vector2(500f, 48f));
            var subtitle = ReMotionUiTheme.Label(canvasGo.transform, "SubtitleText",
                "Record  ·  Replay  ·  Relearn", 16, ReMotionUiTheme.TextSecondary,
                TextAnchor.MiddleLeft);
            Place(subtitle.transform, new Vector2(-520f, 248f), new Vector2(500f, 26f));

            var status = MakeText(canvasGo.transform, "StatusText", "Ready", 16,
                                  new Vector2(330f, 275f));
            status.color = ReMotionUiTheme.TextSecondary;
            status.alignment = TextAnchor.MiddleRight;

            var recordBtn = ThemedButton(canvasGo.transform, "RecordButton", "●  Record",
                ReMotionUiTheme.ButtonStyle.Record, new Vector2(-390f, 175f),
                new Vector2(250f, 56f));
            var saveBtn = ThemedButton(canvasGo.transform, "SaveButton", "■  Stop & Save",
                ReMotionUiTheme.ButtonStyle.Primary, new Vector2(-110f, 175f),
                new Vector2(250f, 56f));

            var listRoot = MakeScrollList(canvasGo.transform);
            library.cardParent = listRoot;

            // Real SceneCard prefab asset (was never created/assigned, so
            // LibraryManager rendered nothing).
            library.cardPrefab = CreateCardPrefab();

            // Runtime controller owns all button/status wiring in
            // OnEnable/OnDisable. Editor-time AddListener lambdas are NOT
            // persisted by Unity — the previous scene saved empty onClick
            // lists and the buttons did nothing after reload.
            var uiGo = new GameObject("ReMotionMainUIController");
            var ui = uiGo.AddComponent<ReMotionMainUIController>();
            ui.recorder = recorder;
            ui.processor = auto;
            ui.library = library;
            ui.recordButton = recordBtn;
            ui.saveButton = saveBtn;
            ui.statusText = status;
            ui.replayController = replay;
            // Capture panel, mode tabs, video player and replay HUD are
            // built by the controller itself (same code path as the runtime
            // bootstrap for saved scenes), then wired above at play time.
            ui.EnsureUi();

            // Pinch-to-click menu interaction (best effort without SDK).
            try { ReMotionPinchSetup.SetupPinchInteraction(); }
            catch (System.Exception e)
            {
                Debug.LogWarning("[ReMotion] Pinch setup skipped: " + e.Message);
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[ReMotion] Main scene created at " + ScenePath);
        }

        /// <summary>
        /// VR rig from Meta XR Building Blocks when the SDK is imported:
        /// Camera Rig block (OVRCameraRig + FloorLevel), Hand Tracking block
        /// (OVRHand prefabs on both hand anchors), Passthrough block
        /// (underlay + transparent camera background => playable AR/MR).
        /// Falls back to VRCameraRig_Placeholder when the SDK is absent, so
        /// batchmode compile/setup passes in a clean Unity install.
        /// SDK types are reached via AssetDatabase + reflection: no hard
        /// Meta references, nothing breaks pre-import.
        /// </summary>
        private static Transform CreateVrRig()
        {
            var defaultCam = Camera.main;
            if (defaultCam != null) Object.DestroyImmediate(defaultCam.gameObject);

            Transform head = TryCreateBuildingBlocksRig();
            if (head != null) return head;

            Debug.LogWarning("[ReMotion] Meta XR SDK not found; using VRCameraRig_Placeholder.");
            var rigGo = new GameObject("VRCameraRig_Placeholder");
            var offset = new GameObject("CameraOffset");
            offset.transform.SetParent(rigGo.transform, false);
            offset.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            var headGo = new GameObject("Main Camera");
            headGo.tag = "MainCamera";
            headGo.transform.SetParent(offset.transform, false);
            var cam = headGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.05f;
            headGo.AddComponent<AudioListener>();
            MakeHand(offset.transform, "LeftHand_Placeholder", new Vector3(-0.25f, -0.25f, 0.35f));
            MakeHand(offset.transform, "RightHand_Placeholder", new Vector3(0.25f, -0.25f, 0.35f));
            return headGo.transform;
        }

        private const string CorePkg = "Packages/com.meta.xr.sdk.core";

        private static Transform TryCreateBuildingBlocksRig()
        {
            // --- Camera Rig building block (OVRCameraRig prefab) ---
            var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CorePkg + "/Prefabs/OVRCameraRig.prefab");
            if (rigPrefab == null) return null;
            var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
            rig.name = "[BuildingBlock] Camera Rig";
            PrefabUtility.UnpackPrefabInstance(rig, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            var manager = FindComponent(rig, "OVRManager");
            SetEnumProperty(manager, "trackingOriginType", "FloorLevel");
            SetFieldOrProperty(manager, "isInsightPassthroughEnabled", true); // AR playable

            Transform centerEye = FindChild(rig.transform, "CenterEyeAnchor");
            Transform leftAnchor = FindChild(rig.transform, "LeftHandAnchor");
            Transform rightAnchor = FindChild(rig.transform, "RightHandAnchor");
            var headCam = centerEye != null ? centerEye.GetComponent<Camera>() : null;
            if (headCam != null)
            {
                headCam.clearFlags = CameraClearFlags.SolidColor;
                headCam.backgroundColor = new Color(0f, 0f, 0f, 0f); // transparent => passthrough shows
            }

            // --- Hand Tracking building block (both hands) ---
            var handPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                CorePkg + "/Editor/BuildingBlocks/BlockData/HandTracking/Prefabs/OVRHandPrefabBuildingBlock.prefab");
            if (handPrefab != null && leftAnchor != null && rightAnchor != null)
            {
                AddBlockHand(handPrefab, leftAnchor, "Left");
                AddBlockHand(handPrefab, rightAnchor, "Right");
            }

            // --- Passthrough building block (underlay layer) ---
            var passPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                CorePkg + "/Editor/BuildingBlocks/BlockData/Passthrough/Prefabs/PassthroughUnderlay.prefab");
            if (passPrefab != null)
            {
                var layer = (GameObject)PrefabUtility.InstantiatePrefab(passPrefab, rig.transform);
                layer.name = "[BuildingBlock] Passthrough";
            }
            else if (FindComponent(rig, "OVRPassthroughLayer") == null)
            {
                var layerType = FindSdkType("OVRPassthroughLayer");
                if (layerType != null) rig.AddComponent(layerType);
            }

            Debug.Log("[ReMotion] Building Blocks rig installed: Camera Rig + Hand Tracking + Passthrough.");
            return centerEye;
        }

        private static void AddBlockHand(GameObject prefab, Transform anchor, string side)
        {
            var hand = (GameObject)PrefabUtility.InstantiatePrefab(prefab, anchor);
            hand.name = $"[BuildingBlock] Hand Tracking {side.ToLowerInvariant()}";
            // Handedness MUST be set per side or the right hand mirrors.
            // OVRHand.HandType is an internal [SerializeField] FIELD (not a
            // property) and SetSkeletonType/SetMeshType are internal, so
            // the original property/public-only reflection silently no-oped
            // and both hands kept the prefab default (None).
            var ovrHand = FindComponent(hand, "OVRHand");
            SetEnumFieldOrProperty(ovrHand, "HandType", "Hand" + side);
            // Skeleton/mesh family follows the configured hand skeleton
            // version (OpenXR -> XRHand*, classic -> Hand*). Picking the
            // wrong family mismatches bone count vs mesh bone weights.
            string typeName = SkeletonTypePrefix() + side;
            var skeleton = FindComponent(hand, "OVRSkeleton");
            InvokeEnumMethod(skeleton, "SetSkeletonType", typeName);
            var mesh = FindComponent(hand, "OVRMesh");
            InvokeEnumMethod(mesh, "SetMeshType", typeName);
        }

        /// <summary>"XRHand" for the OpenXR skeleton version, else "Hand".</summary>
        private static string SkeletonTypePrefix()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(
                "Assets/Resources/OculusRuntimeSettings.asset");
            if (assets != null && assets.Length > 0)
            {
                var prop = new SerializedObject(assets[0]).FindProperty("handSkeletonVersion");
                // OVRHandSkeletonVersion: OVR = 0, OpenXR = 1 (project default).
                if (prop != null && prop.intValue == 1) return "XRHand";
            }
            return "Hand";
        }

        /// <summary>
        /// Repairs handedness in the saved Main scene (both hand blocks,
        /// skeleton and mesh) and saves. Menu: ReMotion XR / Repair Handedness.
        /// Side is resolved from the anchor hierarchy, matching runtime.
        /// </summary>
        [MenuItem("ReMotion XR/Repair Handedness In Main Scene")]
        public static void RepairHandednessInMainScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int fixedCount = 0;
            foreach (var root in scene.GetRootGameObjects())
                fixedCount += FixHandHierarchy(root.transform);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[ReMotion] Handedness repair: set side on {fixedCount} component(s) in {ScenePath}.");
        }

        private static int FixHandHierarchy(Transform node)
        {
            int count = 0;
            string side = HandSideName(node);
            if (side != null)
            {
                foreach (var component in node.GetComponents<Component>())
                {
                    if (component == null) continue;
                    string field = component.GetType().Name switch
                    {
                        "OVRHand" => "HandType",
                        "OVRSkeleton" => "_skeletonType",
                        "OVRMesh" => "_meshType",
                        _ => null,
                    };
                    if (field == null) continue;
                    // HandType itself is always Hand*; skeleton/mesh use
                    // the configured skeleton family (XRHand* on OpenXR).
                    string enumName = field == "HandType"
                        ? "Hand" + side
                        : SkeletonTypePrefix() + side;
                    var so = new SerializedObject(component);
                    var prop = so.FindProperty(field);
                    if (prop == null) continue;
                    int index = System.Array.IndexOf(prop.enumNames, enumName);
                    if (index >= 0 && prop.enumValueIndex != index)
                    {
                        prop.enumValueIndex = index;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        count++;
                    }
                }
            }
            for (int i = 0; i < node.childCount; i++)
                count += FixHandHierarchy(node.GetChild(i));
            return count;
        }

        private static string HandSideName(Transform node)
        {
            for (var t = node; t != null; t = t.parent)
            {
                bool left = t.name.IndexOf("Left", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool right = t.name.IndexOf("Right", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (left && !right) return "Left";
                if (right && !left) return "Right";
            }
            return null;
        }

        private static void SetEnumFieldOrProperty(Component target, string name, string valueName)
        {
            if (target == null) return;
            var type = target.GetType();
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            var field = type.GetField(name, flags);
            if (field != null && field.FieldType.IsEnum)
            {
                try { field.SetValue(target, System.Enum.Parse(field.FieldType, valueName)); return; }
                catch (System.Exception e) { Debug.LogWarning($"[ReMotion] {name}: {e.Message}"); return; }
            }
            SetEnumProperty(target, name, valueName);
        }

        // --- reflection helpers (compile-safe without the Meta SDK) ---

        private static System.Type FindSdkType(string typeName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var t in asm.GetTypes())
                    if (t.Name == typeName) return t;
            }
            return null;
        }

        private static Component FindComponent(GameObject root, string typeName)
        {
            var type = FindSdkType(typeName);
            return type == null || root == null ? null : root.GetComponentInChildren(type, true);
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var hit = FindChild(root.GetChild(i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        private static void SetEnumProperty(Component target, string property, string valueName)
        {
            if (target == null) return;
            var prop = target.GetType().GetProperty(property);
            if (prop == null || !prop.CanWrite) return;
            try { prop.SetValue(target, System.Enum.Parse(prop.PropertyType, valueName)); }
            catch (System.Exception e) { Debug.LogWarning($"[ReMotion] {property}: {e.Message}"); }
        }

        private static void SetFieldOrProperty(Component target, string name, object value)
        {
            if (target == null) return;
            var field = target.GetType().GetField(name);
            if (field != null) { field.SetValue(target, value); return; }
            var prop = target.GetType().GetProperty(name);
            if (prop != null && prop.CanWrite) prop.SetValue(target, value);
        }

        private static void InvokeEnumMethod(Component target, string method, string side)
        {
            if (target == null) return;
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;
            foreach (var m in target.GetType().GetMethods(flags))
            {
                if (m.Name != method || m.GetParameters().Length != 1) continue;
                var enumType = m.GetParameters()[0].ParameterType;
                foreach (string name in System.Enum.GetNames(enumType))
                {
                    if (name.IndexOf(side, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        try { m.Invoke(target, new[] { System.Enum.Parse(enumType, name) }); } catch { /* prefab default */ }
                        return;
                    }
                }
            }
        }

        private static void MakeHand(Transform parent, string name, Vector3 pos)
        {
            var hand = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hand.name = name;
            hand.transform.SetParent(parent, false);
            hand.transform.localPosition = pos;
            hand.transform.localScale = Vector3.one * 0.07f;
            // Placeholder cube until hand tracking / SAM 3D hands land.
        }

        private static Text MakeText(Transform parent, string name, string content,
                                     int size, Vector2 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.text = content;
            text.fontSize = size;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(700f, 60f);
            return text;
        }

        private static RectTransform MakePanel(Transform parent, string name, Vector2 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(700f, 260f);
            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.spacing = 6f;
            return rt;
        }

        private static void Place(Transform t, Vector2 pos, Vector2 size)
        {
            var rt = t.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        private static Button ThemedButton(Transform parent, string name, string label,
                                           ReMotionUiTheme.ButtonStyle style,
                                           Vector2 pos, Vector2 size)
        {
            var button = ReMotionUiTheme.Button(parent, name, label, style, 17);
            Place(button.transform, pos, size);
            return button;
        }

        /// <summary>Scrollable, masked card list with a vertical layout.</summary>
        private static RectTransform MakeScrollList(Transform parent)
        {
            var viewportGo = new GameObject("SceneListViewport");
            viewportGo.transform.SetParent(parent, false);
            var viewportRt = viewportGo.AddComponent<RectTransform>();
            viewportRt.anchoredPosition = new Vector2(0f, -95f);
            viewportRt.sizeDelta = new Vector2(760f, 430f);
            viewportGo.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.12f);
            viewportGo.AddComponent<Mask>().showMaskGraphic = false;
            var scroll = viewportGo.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;

            var content = new GameObject("SceneList");
            content.transform.SetParent(viewportGo.transform, false);
            var contentRt = content.AddComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            // Netflix-style poster grid: two tiles per row, scrolls vertically.
            var layout = content.AddComponent<GridLayoutGroup>();
            layout.cellSize = new Vector2(340f, 404f);
            layout.spacing = new Vector2(16f, 16f);
            layout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            layout.constraintCount = 2;
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.padding = new RectOffset(12, 12, 12, 12);
            content.AddComponent<ContentSizeFitter>().verticalFit =
                ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = contentRt;
            scroll.viewport = viewportRt;
            return contentRt;
        }

        private static SceneCardUI CreateCardPrefab()
        {
            const string prefabPath = "Assets/Prefabs/SceneCard.prefab";
            var existing = AssetDatabase.LoadAssetAtPath<SceneCardUI>(prefabPath);
            if (existing != null) return existing;
            Directory.CreateDirectory("Assets/Prefabs");
            var card = SceneCardFactory.CreateCard(null);
            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(card.gameObject, prefabPath);
                return prefab.GetComponent<SceneCardUI>();
            }
            finally { Object.DestroyImmediate(card.gameObject); }
        }

        private static Button MakeButton(Transform parent, string name, string label, Vector2 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<Image>();
            var button = go.AddComponent<Button>();
            var rt = go.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(200f, 60f);
            MakeText(go.transform, "Label", label, 22, Vector2.zero);
            return button;
        }
    }
}
#endif
