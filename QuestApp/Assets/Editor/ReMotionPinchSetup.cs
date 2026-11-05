#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using ReMotionXR.Core;

namespace ReMotionXR.Editor
{
    /// <summary>
    /// Connects Meta's own building blocks for pinch menu interaction:
    /// [BB] Interactions (OVRCameraRigRef + TrackingToWorldTransformerOVR),
    /// [BB] Hands (ISDK Hand + FromOVRHandDataSource per side), and
    /// [BB] Hand Ray (RayInteractor + pinch selector) per hand, with the
    /// blocks' null serialized refs wired to the rig. Plus PointableCanvas
    /// on the Library canvas and PointableCanvasModule on the EventSystem.
    /// Menu: ReMotion XR / Setup Pinch Menu Interaction (saves Main).
    /// Meta types via SerializedObject/reflection: compiles without SDK.
    /// </summary>
    public static class ReMotionPinchSetup
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string BlocksRoot =
            "Packages/com.meta.xr.sdk.interaction.ovr/Editor/Blocks/";
        private const string InteractionsPrefab = BlocksRoot +
            "Interactions/Prefabs/[BB] Interactions.prefab";
        private const string HandsPrefab = BlocksRoot +
            "HandTracking/Prefabs/[BB] Hands.prefab";
        private const string HandRayPrefab = BlocksRoot +
            "Interactors/Prefabs/[BB] Hand Ray.prefab";

        [MenuItem("ReMotion XR/Setup Pinch Menu Interaction")]
        public static void SetupPinchInMainScene()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            int interactors = SetupPinchInteraction();
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[ReMotion] Pinch menu setup saved. Ray interactors: {interactors}.");
        }

        /// <summary>Idempotent; returns the number of hand ray interactors.</summary>
        public static int SetupPinchInteraction()
        {
            var eventSystem = XrUiInputSetup.EnsureEventSystem();
            var moduleType = PinchMenuInteraction.FindLoadedType("PointableCanvasModule");
            if (moduleType != null && eventSystem.GetComponent(moduleType) == null)
            {
                var standalone = eventSystem.GetComponent<StandaloneInputModule>();
                if (standalone != null) standalone.enabled = false;
                eventSystem.gameObject.AddComponent(moduleType);
            }

            // Explicit canvas: the named Library canvas, never the first
            // arbitrary world-space canvas found in the scene.
            var named = GameObject.Find("LibraryCanvas_WorldSpace");
            var canvas = named != null ? named.GetComponent<Canvas>()
                                       : PinchMenuInteraction.FindLibraryCanvas();
            if (canvas != null)
            {
                PinchMenuInteraction.EnsureCanvasPointable(canvas);
                EnsureRayInteractable(canvas);
            }
            else Debug.LogWarning("[ReMotion] Pinch setup: LibraryCanvas_WorldSpace not found.");

            var rig = FindCameraRigRoot();
            if (rig == null)
            {
                Debug.LogWarning("[ReMotion] Pinch setup: OVRCameraRig root not found.");
                return 0;
            }

            // 1) Rig refs block (camera rig ref + tracking transformer).
            if (FindComponentIn(rig, "OVRCameraRigRef") == null)
                InstantiateBlock(InteractionsPrefab, rig, "[BB] Interactions");

            // 2) ISDK hands block (Hand + data source per side).
            if (FindComponentIn(rig, "FromOVRHandDataSource") == null)
                InstantiateBlock(HandsPrefab, rig, "[BB] Hands");

            // 3) Pinch ray per side, under that side's interactor root.
            foreach (string side in new[] { "Left", "Right" })
            {
                var interactorRoot = FindInScene("HandInteractors" + side) ?? rig;
                if (FindComponentIn(interactorRoot, "RayInteractor") == null)
                    InstantiateBlock(HandRayPrefab, interactorRoot,
                                     $"[BB] Hand Ray {side}");
            }

            // 4) Wire the blocks' null refs (rig ref, transformer, hands).
            WireBlockReferences(rig);

            // 5) Native beam per existing RayInteractor (no second rig).
            EnsureHandRayVisuals(rig);
            EnsureJointPinchSelectors(rig);

            // Final gate: fail loudly on any null in the whole chain.
            if (canvas != null)
            {
                var finalErrors = ValidatePinchChain(canvas);
                if (finalErrors.Count == 0)
                    Debug.Log("[ReMotion] Pinch chain FINAL validation: PASS");
                else
                    foreach (var e in finalErrors)
                        Debug.LogWarning("[ReMotion] Pinch chain FINAL INCOMPLETE: " + e);
            }
            return CountIn(rig, "RayInteractor");
        }

        /// <summary>
        /// Builds the complete receiving chain on the canvas:
        /// RayInteractable._pointableElement -> PointableCanvas (InjectCanvas),
        /// RayInteractable._surface -> ClippedPlaneSurface bounded to the
        /// canvas rectangle (PlaneSurface + BoundsClipper, mirroring the
        /// installed FlatUnityCanvas sample). Returns validation errors;
        /// an empty list means the chain is actually wired â€” presence of
        /// components alone never counts as ready.
        /// </summary>
        public static System.Collections.Generic.List<string> EnsureRayInteractable(Canvas canvas)
        {
            var errors = new System.Collections.Generic.List<string>();
            var rayType = PinchMenuInteraction.FindLoadedType("Oculus.Interaction.RayInteractable") ??
                          PinchMenuInteraction.FindLoadedType("RayInteractable");
            var pointableType = PinchMenuInteraction.FindLoadedType("Oculus.Interaction.PointableCanvas") ??
                                PinchMenuInteraction.FindLoadedType("PointableCanvas");
            var planeType = PinchMenuInteraction.FindLoadedType("Oculus.Interaction.Surfaces.PlaneSurface") ??
                            PinchMenuInteraction.FindLoadedType("PlaneSurface");
            var clippedType = PinchMenuInteraction.FindLoadedType("Oculus.Interaction.Surfaces.ClippedPlaneSurface") ??
                              PinchMenuInteraction.FindLoadedType("ClippedPlaneSurface");
            var clipperType = PinchMenuInteraction.FindLoadedType("Oculus.Interaction.Surfaces.BoundsClipper") ??
                              PinchMenuInteraction.FindLoadedType("BoundsClipper");
            if (rayType == null) { errors.Add("RayInteractable type not found in installed SDK"); return errors; }
            if (pointableType == null) { errors.Add("PointableCanvas type not found"); return errors; }
            if (planeType == null || clippedType == null || clipperType == null)
            { errors.Add("Surface types (PlaneSurface/ClippedPlaneSurface/BoundsClipper) not found"); return errors; }

            // PointableCanvas bound to THIS canvas via the public injector.
            var pointable = canvas.GetComponent(pointableType) ?? canvas.gameObject.AddComponent(pointableType);
            pointableType.GetMethod("InjectCanvas")?.Invoke(pointable, new object[] { canvas });

            // Bounded surface: Surface child, scaled to the canvas rect
            // (sample pattern: clipper size 1x1, transform carries size).
            var surfaceGo = canvas.transform.Find("Surface")?.gameObject;
            if (surfaceGo == null)
            {
                surfaceGo = new GameObject("Surface");
                surfaceGo.transform.SetParent(canvas.transform, false);
            }
            var plane = surfaceGo.GetComponent(planeType) ?? surfaceGo.AddComponent(planeType);
            var clipped = surfaceGo.GetComponent(clippedType) ?? surfaceGo.AddComponent(clippedType);
            var clipper = surfaceGo.GetComponent(clipperType) ?? surfaceGo.AddComponent(clipperType);
            var rect = canvas.GetComponent<RectTransform>();
            Vector2 size = rect != null ? rect.sizeDelta : new Vector2(1200f, 700f);
            if (size.x <= 0f) size.x = 1200f;
            if (size.y <= 0f) size.y = 700f;
            surfaceGo.transform.localPosition = Vector3.zero;
            surfaceGo.transform.localRotation = Quaternion.identity;
            surfaceGo.transform.localScale = new Vector3(size.x, size.y, 0.01f);
            SetRef(clipped, "_planeSurface", plane);
            var clipSo = new SerializedObject(clipped as Object);
            var clipProp = clipSo.FindProperty("_clippers");
            if (clipProp != null)
            {
                clipProp.ClearArray();
                clipProp.InsertArrayElementAtIndex(0);
                clipProp.GetArrayElementAtIndex(0).objectReferenceValue = clipper as Object;
                clipSo.ApplyModifiedPropertiesWithoutUndo();
            }

            // RayInteractable: correct serialized fields are
            // _pointableElement (not _pointable) and _surface.
            var ray = canvas.GetComponent(rayType) ?? canvas.gameObject.AddComponent(rayType);
            SetRef(ray, "_pointableElement", pointable);
            SetRef(ray, "_surface", clipped);

            errors.AddRange(ValidatePinchChain(canvas));
            if (errors.Count == 0)
                Debug.Log("[ReMotion] Pinch chain validation: PASS " +
                          "(pointableElement + bounded surface + canvas bound)");
            else
                foreach (var e in errors)
                    Debug.LogWarning("[ReMotion] Pinch chain INCOMPLETE: " + e);
            return errors;
        }

        /// <summary>Fail-on-null validation of the whole receiving chain.</summary>
        public static System.Collections.Generic.List<string> ValidatePinchChain(Canvas canvas)
        {
            var errors = new System.Collections.Generic.List<string>();
            if (canvas == null) { errors.Add("canvas is null"); return errors; }
            var rayType = PinchMenuInteraction.FindLoadedType("RayInteractable");
            var pointableType = PinchMenuInteraction.FindLoadedType("PointableCanvas");
            var ray = rayType != null ? canvas.GetComponent(rayType) : null;
            if (ray == null) errors.Add("RayInteractable missing on canvas");
            else
            {
                var so = new SerializedObject(ray as Object);
                if (so.FindProperty("_pointableElement")?.objectReferenceValue == null)
                    errors.Add("RayInteractable._pointableElement is null");
                if (so.FindProperty("_surface")?.objectReferenceValue == null)
                    errors.Add("RayInteractable._surface is null (no bounded surface)");
            }
            var pointable = pointableType != null ? canvas.GetComponent(pointableType) : null;
            if (pointable == null) errors.Add("PointableCanvas missing on canvas");
            else if (pointableType.GetProperty("Canvas")?.GetValue(pointable) == null)
                errors.Add("PointableCanvas.Canvas is not bound");
            if (canvas.GetComponent<GraphicRaycaster>() == null)
                errors.Add("GraphicRaycaster missing on canvas");
            int interactors = 0, beams = 0;
            foreach (var rootGo in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
            {
                interactors += CountIn(rootGo.transform, "RayInteractor");
                beams += CountIn(rootGo.transform, "RayInteractorRayVisual");
            }
            if (interactors == 0) errors.Add("no RayInteractor in scene");
            if (beams == 0) errors.Add("no RayInteractorRayVisual beam in scene");
            var moduleType = PinchMenuInteraction.FindLoadedType("PointableCanvasModule");
            var es = Object.FindFirstObjectByType<EventSystem>();
            if (es == null) errors.Add("no EventSystem");
            else if (moduleType != null && es.GetComponent(moduleType) == null)
                errors.Add("PointableCanvasModule missing on EventSystem");

            // Per-hand selector chain: each RayInteractor needs a
            // selector, whose hand is the SAME side as the interactor.
            // A structural pass here still does not prove runtime
            // native selection â€” that needs the device trace.
            foreach (var rootGo in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
            {
                foreach (var comp in rootGo.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null || comp.GetType().Name != "RayInteractor") continue;
                    string side = SideName(comp.transform) ?? "?";
                    string who = $"RayInteractor[{side}]";
                    var so = new SerializedObject(comp);
                    var selector = so.FindProperty("_selector")?.objectReferenceValue as Component;
                    if (selector == null) { errors.Add(who + ": _selector is null"); continue; }
                    var selSo = new SerializedObject(selector);
                    var hand = selSo.FindProperty("_hand")?.objectReferenceValue as Component;
                    if (hand == null) errors.Add(who + ": selector._hand is null");
                    else if (SideName(hand.transform) != side)
                        errors.Add(who + $": selector hand side is {SideName(hand.transform)}, expected {side}");
                    bool beamBound = false;
                    foreach (var child in comp.GetComponentsInChildren<Component>(true))
                    {
                        if (child == null || child.GetType().Name != "RayInteractorRayVisual") continue;
                        var visSo = new SerializedObject(child);
                        if (visSo.FindProperty("_rayInteractor")?.objectReferenceValue == comp)
                            beamBound = true;
                    }
                    if (!beamBound) errors.Add(who + ": no beam bound to this interactor");
                }
            }

            // OVRHand identity: HandType must match hierarchy side
            // (0=Left, 1=Right). Both at 0 silently kills right pinch.
            foreach (var rootGo in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
            {
                foreach (var comp in rootGo.GetComponentsInChildren<Component>(true))
                {
                    if (comp == null || comp.GetType().Name != "OVRHand") continue;
                    string side = SideName(comp.transform);
                    if (side == null) continue;
                    var so = new SerializedObject(comp);
                    var prop = so.FindProperty("HandType");
                    int want = side == "Right" ? 1 : 0;
                    if (prop != null && prop.intValue != want)
                        errors.Add($"OVRHand[{side}]: HandType={prop.intValue}, expected {want} â€” pinch reads the wrong hand");
                }
            }
            return errors;
        }

        private static void EnsureJointPinchSelectors(Transform rig)
        {
            foreach (var comp in rig.GetComponentsInChildren<Component>(true))
            {
                if (comp == null || comp.GetType().Name != "RayInteractor") continue;
                var side = SideName(comp.transform);
                var hand = FindHandBySide(rig, "Hand", side);
                if (hand == null) throw new System.InvalidOperationException("Missing " + side + " hand");
                var selector = comp.GetComponent<JointPinchSelector>() ??
                    comp.gameObject.AddComponent<JointPinchSelector>();
                SetHandRef(selector, "_hand", hand);
                SetRef(selector, "_ray", comp);
                var serialized = new SerializedObject(comp);
                serialized.FindProperty("_selector").objectReferenceValue = selector;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log("[ReMotion] Joint pinch selector bound to existing " + side + " ray");
            }
        }

        private const string HandRayVisualPrefab =
            "Packages/com.meta.xr.sdk.interaction/Runtime/Prefabs/Ray/HandRayInteractor.prefab";

        /// <summary>
        /// Adds the SDK native beam (RayInteractorRayVisual subtree from
        /// HandRayInteractor.prefab) to each existing RayInteractor that
        /// lacks one, bound to that same interactor. Visible before pinch
        /// (hideWhenNoInteractable=false), 3.5m visual length. Idempotent:
        /// an interactor that already carries a beam is left alone.
        /// </summary>
        public static void EnsureHandRayVisuals(Transform rig)
        {
            var visualType = PinchMenuInteraction.FindLoadedType("RayInteractorRayVisual");
            if (visualType == null)
            { Debug.LogWarning("[ReMotion] Pinch setup: RayInteractorRayVisual type not found."); return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(HandRayVisualPrefab);
            if (prefab == null)
            { Debug.LogWarning("[ReMotion] Pinch setup: beam prefab not found: " + HandRayVisualPrefab); return; }
            var contents = PrefabUtility.LoadPrefabContents(HandRayVisualPrefab);
            Transform rayVisualAsset = null;
            try
            {
                foreach (var c in contents.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name == "RayInteractorRayVisual")
                    { rayVisualAsset = c.transform; break; }
                if (rayVisualAsset == null)
                { Debug.LogWarning("[ReMotion] Pinch setup: no RayVisual in beam prefab."); return; }

                var interactors = new System.Collections.Generic.List<Component>();
                foreach (var c in rig.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name == "RayInteractor") interactors.Add(c);

                foreach (var interactor in interactors)
                {
                    Component existingBeam = null;
                    foreach (var c in interactor.GetComponentsInChildren<Component>(true))
                        if (c != null && c.GetType().Name == "RayInteractorRayVisual") { existingBeam = c; break; }
                    if (existingBeam != null)
                    {
                        // Correct a beam bound to the wrong (prefab)
                        // interactor; identity is setup-owned here.
                        SetHandRef(existingBeam, "_rayInteractor", interactor);
                        continue;
                    }
                    var beam = Object.Instantiate(rayVisualAsset.gameObject);
                    beam.name = "RayVisual";
                    beam.transform.SetParent(interactor.transform, false);
                    var visual = beam.GetComponent(visualType);
                    // Identity-owned: the prefab subtree arrives bound
                    // to the prefab's own interactor; correct it to
                    // THIS scene interactor (SetRef would refuse).
                    SetHandRef(visual, "_rayInteractor", interactor);
                    var so = new SerializedObject(visual as Object);
                    var lenProp = so.FindProperty("_maxRayVisualLength");
                    if (lenProp != null) lenProp.floatValue = 3.5f;
                    var hideProp = so.FindProperty("_hideWhenNoInteractable");
                    if (hideProp != null) hideProp.boolValue = false;
                    so.ApplyModifiedPropertiesWithoutUndo();
                    Debug.Log("[ReMotion] Pinch setup: native beam added to " +
                              interactor.gameObject.name + " (3.5m, visible without hit).");
                }
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        /// <summary>
        /// Assigns null serialized refs by field name from scene candidates:
        /// _ovrCameraRig on OVRCameraRigRef from the scene rig,
        /// _cameraRigRef/_trackingToWorldTransformer from the Interactions
        /// block, _hand from the same side's ISDK Hand, _ovrHand from the
        /// same side's OVRHand (side read from the hierarchy).
        /// </summary>
        public static void WireBlockReferences(Transform rig)
        {
            var cameraRig = FindComponentIn(rig, "OVRCameraRig");
            var rigRef = FindComponentIn(rig, "OVRCameraRigRef");
            var transformer = FindComponentIn(rig, "TrackingToWorldTransformerOVR");
            foreach (var component in rig.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                string side = SideName(component.transform); // "Left" | "Right" | null
                SetRef(component, "_ovrCameraRig", cameraRig);
                SetRef(component, "_cameraRigRef", rigRef);
                SetRef(component, "_trackingToWorldTransformer", transformer);
                // OVRHand.HandType is setup-owned identity: the saved
                // scene had BOTH hands at 0 (HandLeft), so the right
                // hand read the left hand's pinch state and right-hand
                // pinch could never fire. Correct it by hierarchy side.
                if (side != null && component.GetType().Name == "OVRHand")
                {
                    var so = new SerializedObject(component);
                    var prop = so.FindProperty("HandType");
                    if (prop != null)
                    {
                        int want = side == "Right" ? 1 : 0;
                        if (prop.intValue != want)
                        {
                            prop.intValue = want;
                            so.ApplyModifiedPropertiesWithoutUndo();
                            Debug.Log($"[ReMotion] Pinch setup: corrected OVRHand.HandType -> {side} on {component.gameObject.name}");
                        }
                    }
                }
                if (side != null)
                {
                    // Only setup-owned EXTERNAL references are corrected:
                    // the pinch selector's hand and the OVR data source's
                    // hand. SDK-internal prefab chains (cursors, pointer
                    // poses, wrappers/filters) are preserved as-is.
                    if (component.GetType().Name == "IndexPinchSelector")
                        SetHandRef(component, "_hand",
                                   FindHandBySide(rig, "Hand", side));
                    if (component.GetType().Name == "FromOVRHandDataSource")
                        SetHandRef(component, "_ovrHand",
                                   FindHandBySide(rig, "OVRHand", side));
                }
            }
        }

        private static Component FindHandBySide(Transform rig, string typeName, string side)
        {
            // Same side only. Returning the other hand to make the field
            // non-null would wire a selector to the wrong hand and hide
            // the defect; validation must fail loudly instead.
            foreach (var component in rig.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component.GetType().Name != typeName) continue;
                if (SideName(component.transform) == side) return component;
            }
            Debug.LogWarning($"[ReMotion] Pinch setup: no {typeName} found for side {side}; leaving unwired for validation to report.");
            return null;
        }

        /// <summary>
        /// Setup-owned hand reference: corrects a wrong-side/wrong
        /// instance instead of refusing to replace a same-typed value.
        /// </summary>
        private static void SetHandRef(Component target, string fieldName, Component value)
        {
            if (value == null) return;
            var so = new SerializedObject(target);
            var prop = so.FindProperty(fieldName);
            if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference) return;
            if (prop.objectReferenceValue == value) return;
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetRef(Component target, string fieldName, Object value)
        {
            if (value == null) return;
            var so = new SerializedObject(target);
            var prop = so.FindProperty(fieldName);
            if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference)
                return;
            if (prop.objectReferenceValue == value) return;
            // Never overwrite a reference the block already wired.
            if (prop.objectReferenceValue != null &&
                prop.objectReferenceValue is Component existing &&
                existing.GetType().Name == value.GetType().Name) return;
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string SideName(Transform node)
        {
            var side = HandednessGuard.SideFromName(node.name);
            if (side != HandednessGuard.HandSide.None)
                return side == HandednessGuard.HandSide.Left ? "Left" : "Right";
            return node.parent != null ? SideName(node.parent) : null;
        }

        private static void InstantiateBlock(string prefabPath, Transform parent, string name)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[ReMotion] Pinch setup: block prefab not found: " + prefabPath);
                return;
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            instance.name = name;
            Debug.Log("[ReMotion] Pinch setup: installed " + name);
        }

        private static Transform FindCameraRigRoot()
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
            {
                if (FindComponentIn(root.transform, "OVRManager") != null)
                    return root.transform;
            }
            return null;
        }

        private static int CountIn(Transform root, string typeName)
        {
            int count = 0;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().Name == typeName) count++;
            return count;
        }

        private static Component FindComponentIn(Transform root, string typeName)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
                if (component != null && component.GetType().Name == typeName)
                    return component;
            return null;
        }

        private static Transform FindInScene(string objectName)
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
            {
                var hit = FindRecursive(root.transform, objectName);
                if (hit != null) return hit;
            }
            return null;
        }

        private static Transform FindRecursive(Transform node, string objectName)
        {
            if (node.name == objectName) return node;
            for (int i = 0; i < node.childCount; i++)
            {
                var hit = FindRecursive(node.GetChild(i), objectName);
                if (hit != null) return hit;
            }
            return null;
        }
    }
}
#endif
