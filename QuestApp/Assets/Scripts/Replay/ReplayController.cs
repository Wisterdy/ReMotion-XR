using System.IO;
using UnityEngine;
using ReMotionXR.Core;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Owns the replay rig: ghost hands + grabbed-object placeholder,
    /// ReplayPlayer, KeyPoseManager and GodModeController under one root.
    /// EnterSession is the real implementation behind the Library card's
    /// "Enter Virtual Scene" button: read scene.remotion, validate it as a
    /// ScenePackage, load the timeline, show the rig, and start playback.
    /// The tracked camera is never parented or scaled; God Mode scales
    /// only sceneRoot (the ghosts), never the viewer.
    /// </summary>
    public class ReplayController : MonoBehaviour
    {
        public ReplayPlayer player;
        public KeyPoseManager keyPoseManager;
        public GodModeController godMode;
        public GameObject replayRoot;
        public Transform sceneRoot;

        public bool IsInReplay { get; private set; }
        public Transform SceneRootForTests => sceneRoot;
        public bool RetrainActive { get; private set; }
        public string CurrentSessionId { get; private set; }
        public RetrainingTrainer Trainer { get; private set; }
        public string LastRetrainFeedback => Trainer != null ? Trainer.LastFeedback : "";

        private void Awake() => EnsureBuilt();

        /// <summary>Creates any missing rig pieces (idempotent).</summary>
        public void EnsureBuilt()
        {
            if (replayRoot == null)
            {
                replayRoot = new GameObject("ReplayRoot");
                replayRoot.transform.SetParent(transform, false);
            }
            if (sceneRoot == null)
            {
                var rootGo = new GameObject("ReplaySceneRoot");
                rootGo.transform.SetParent(replayRoot.transform, false);
                sceneRoot = rootGo.transform;
            }
            if (keyPoseManager == null)
                keyPoseManager = replayRoot.GetComponent<KeyPoseManager>()
                                 ?? replayRoot.AddComponent<KeyPoseManager>();
            if (player == null)
            {
                player = replayRoot.GetComponent<ReplayPlayer>()
                         ?? replayRoot.AddComponent<ReplayPlayer>();
                player.keyPoseManager = keyPoseManager;
                player.sceneRoot = sceneRoot;
                // Blue hand models, same as the live tracked hands.
                player.leftHandGhost = MakeGhost(sceneRoot, "LeftHandGhost", new Color(0.35f, 0.55f, 1f));
                player.rightHandGhost = MakeGhost(sceneRoot, "RightHandGhost", new Color(0.35f, 0.55f, 1f));
                player.grabbedObject = MakeGhost(sceneRoot, "GrabbedObject", new Color(1f, 0.55f, 0f));
                player.grabbedObject.localScale = Vector3.one * 0.12f;
                AddObjectLabel(player.grabbedObject);
            }
            if (godMode == null)
            {
                godMode = replayRoot.GetComponent<GodModeController>()
                          ?? replayRoot.AddComponent<GodModeController>();
                godMode.sceneRoot = sceneRoot;
            }
            replayRoot.SetActive(IsInReplay);
        }

        /// <summary>The controller currently showing a scene, whichever instance entered it.</summary>
        public static ReplayController ActiveInstance { get; private set; }

        /// <summary>Clear any visible replay scene, whichever instance owns it.</summary>
        public static void ExitAnyActiveScene()
        {
            if (ActiveInstance != null && ActiveInstance.IsInReplay)
                ActiveInstance.ExitSession();
        }

        public bool EnterSession(string sessionId, string sessionFolder, out string error)
        {
            error = null;
            EnsureBuilt();
            ActiveInstance = this;
            string packagePath = Path.Combine(sessionFolder ?? "", "scene.remotion");
            if (!File.Exists(packagePath))
            {
                error = "No scene.remotion in session folder: " + sessionFolder;
                Debug.LogError("[ReMotion] Enter failed: " + error);
                return false;
            }
            byte[] packageBytes = File.ReadAllBytes(packagePath);
            if (!ScenePackage.TryParseTimeline(packageBytes,
                    out SessionTimeline timeline, out error))
            {
                Debug.LogError("[ReMotion] Enter failed: " + error);
                return false;
            }
            // SAM-built object models from the server package. The
            // immersive scene for a capture contains only the important
            // object(s) the AI found plus the recorded hands — no
            // generic Person/Target stage (user-directed). Absent on
            // on-device packages: the placeholder stays hidden too, so
            // a capture scene never invents scenery.
            Mesh objectMesh = null; Color objectColor = default; Texture2D objectTexture = null;
            ScenePackage.SceneAssetInfo objectInfo = null;
            var extraObjects = new System.Collections.Generic.List<(Mesh, Color, Texture2D, ScenePackage.SceneAssetInfo)>();
            if (ScenePackage.TryParseAssets(packageBytes, out var assets, out string assetError))
            {
                foreach (var asset in assets)
                {
                    if (asset.Info.kind != "object") continue;
                    var mesh = GlbMeshLoader.LoadMesh(asset.GlbBytes, out Color color, out Texture2D texture, out string glbError);
                    if (mesh == null)
                    {
                        Debug.LogWarning($"[ReMotion] SAM model {asset.Info.model} unusable: {glbError}");
                        continue;
                    }
                    Debug.Log($"[ReMotion] SAM model loaded: {asset.Info.track_id} ({asset.Info.kind}, " +
                              $"{asset.Info.sam_backend}/{asset.Info.reconstruction_backend})");
                    if (objectMesh == null) { objectMesh = mesh; objectColor = color; objectTexture = texture; objectInfo = asset.Info; }
                    else extraObjects.Add((mesh, color, texture, asset.Info));
                }
            }
            else Debug.LogWarning("[ReMotion] SAM assets unreadable: " + assetError);
            bool basketball = BasketballSceneBuilder.IsBasketball(timeline);
            ApplyObjectMesh(objectMesh, objectColor, basketball, objectTexture, objectInfo);
            player.grabbedObjectId = objectInfo?.track_id;
            var firstMotion = player.grabbedObject.GetComponent<ObservedObjectMotion>();
            if (firstMotion == null) firstMotion = player.grabbedObject.gameObject.AddComponent<ObservedObjectMotion>();
            firstMotion.Configure(objectInfo?.object_poses, player);
            if (objectMesh != null) ReplayAssetLayout.Place(player.grabbedObject, objectMesh, objectInfo, timeline, 0);
            BuildIdentifiedObjects(extraObjects, timeline);
            player.Load(timeline);
            var motionTimes = new System.Collections.Generic.SortedSet<float>();
            foreach (var motion in sceneRoot.GetComponentsInChildren<ObservedObjectMotion>(true))
                foreach (var t in motion.Timestamps) motionTimes.Add(t);
            if (motionTimes.Count > 0) keyPoseManager.UseObservedTimes(motionTimes);
            // The bundled basketball sample keeps its authored court;
            // free-form captures get no stage at all — object + hands only.
            if (basketball)
                BasketballSceneBuilder.Build(sceneRoot, player.grabbedObject,
                    player.leftHandGhost, player.rightHandGhost);
            else RemoveGenericStage();
            AnchorSceneToViewer(timeline);
            player.Scrub(0f); // re-apply frame 0 with the styled object in hand
            CurrentSessionId = sessionId;
            IsInReplay = true;
            replayRoot.SetActive(true);
            player.Play();
            Debug.Log($"[ReMotion] Entered virtual scene '{sessionId}' ({timeline.frames.Count} frames).");
            return true;
        }

        /// <summary>
        /// Place the recorded wearer's start spot where the viewer is
        /// standing now (yaw-aligned). Without this the stage sits at the
        /// recording's room origin — the Person/Target appear under or
        /// behind whoever opens the scene from a different spot.
        /// </summary>
        public void AnchorSceneToViewer(SessionTimeline timeline)
        {
            if (sceneRoot == null || timeline == null || timeline.frames.Count == 0) return;
            var camera = Camera.main;
            if (camera == null) return;
            Vector3 recordedHead = timeline.frames[0].headPosition;
            if (recordedHead == Vector3.zero) return;
            Vector3 viewerForward = camera.transform.forward;
            viewerForward.y = 0f;
            Vector3 recordedForward = timeline.frames[0].headRotation * Vector3.forward;
            recordedForward.y = 0f;
            if (viewerForward.sqrMagnitude < 0.001f || recordedForward.sqrMagnitude < 0.001f) return;
            float yaw = Vector3.SignedAngle(recordedForward.normalized, viewerForward.normalized, Vector3.up);
            sceneRoot.rotation = Quaternion.Euler(0f, yaw, 0f);
            sceneRoot.position = camera.transform.position - sceneRoot.rotation * recordedHead;
        }

        public void ExitSession()
        {
            EnsureBuilt();
            player.Pause();
            RetrainActive = false;
            if (Trainer != null) Trainer.SetActive(false);
            if (godMode != null) godMode.SetGodMode(false);
            IsInReplay = false;
            CurrentSessionId = null;
            if (ActiveInstance == this) ActiveInstance = null;
            replayRoot.SetActive(false);
        }

        public void SetRetrain(bool on)
        {
            RetrainActive = on && IsInReplay;
            if (Trainer == null && replayRoot != null)
            {
                Trainer = replayRoot.GetComponent<RetrainingTrainer>()
                          ?? replayRoot.AddComponent<RetrainingTrainer>();
                Trainer.player = player;
                Trainer.liveRecorder = replayRoot.GetComponent<Capture.HandPoseRecorder>()
                                       ?? replayRoot.AddComponent<Capture.HandPoseRecorder>();
            }
            if (Trainer != null)
            {
                Trainer.player = player;
                Trainer.SetActive(RetrainActive);
            }
        }

        public void SetGodMode(bool on)
        {
            EnsureBuilt();
            if (godMode != null) godMode.SetGodMode(on && IsInReplay);
        }

        /// <summary>Exposed so tests/UI can assert playback without reflection.</summary>
        public ReplayPlayer Player { get { EnsureBuilt(); return player; } }

        private static Transform MakeGhost(Transform parent, string name, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localScale = Vector3.one * 0.07f;
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                if (Application.isPlaying) Destroy(collider);
                else DestroyImmediate(collider);
            }
            SceneMaterials.ApplyColor(go.GetComponent<Renderer>(), color);
            if (name == "LeftHandGhost" || name == "RightHandGhost")
                go.AddComponent<HandGhostVisual>().Configure(color);
            return go.transform;
        }

        /// <summary>
        /// Show the SAM object model on the grabbed-object rig instead of
        /// the placeholder cube (or restore the placeholder when the
        /// package has no object model).
        /// </summary>
        /// <summary>
        /// Additional AI-identified objects (beyond the hand-held one)
        /// stand in the scene in front of the viewer, labelled, so the
        /// user can try the action again among the real key objects —
        /// e.g. monitor, keyboard and mouse from a desk capture.
        /// </summary>
        private void BuildIdentifiedObjects(
            System.Collections.Generic.List<(Mesh mesh, Color color, Texture2D texture, ScenePackage.SceneAssetInfo info)> objects,
            SessionTimeline timeline)
        {
            if (sceneRoot == null) return;
            var old = sceneRoot.Find("IdentifiedObjects");
            if (old != null)
            {
                old.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(old.gameObject);
                else DestroyImmediate(old.gameObject);
            }
            if (objects == null || objects.Count == 0) return;
            var container = new GameObject("IdentifiedObjects");
            container.transform.SetParent(sceneRoot, false);
            for (int i = 0; i < objects.Count; i++)
            {
                var (mesh, color, texture, info) = objects[i];
                string label = info.label;
                var go = new GameObject(string.IsNullOrEmpty(label) ? $"Object{i}" : label);
                go.transform.SetParent(container.transform, false);
                ReplayAssetLayout.Place(go.transform, mesh, info, timeline, i + 1);
                ReplayAssetLayout.AddModel(go.transform, mesh, color, texture, info);
                go.AddComponent<ObservedObjectMotion>().Configure(info.object_poses, player);
                var labelGo = new GameObject("ObjectLabel");
                labelGo.transform.SetParent(go.transform, false);
                labelGo.transform.localPosition = new Vector3(0f,
                    mesh.bounds.size.y * ReplayAssetLayout.EstimatedWidth(info) / Mathf.Max(mesh.bounds.size.x, .001f) * .5f + .035f, 0f);
                var tm = labelGo.AddComponent<TextMesh>();
                tm.text = label;
                tm.fontSize = 48;
                tm.characterSize = 0.004f;
                tm.anchor = TextAnchor.MiddleCenter;
                tm.color = Color.white;
            }
        }

        /// <summary>Drop any generic stage built earlier in the session.</summary>
        private void RemoveGenericStage()
        {
            var stage = sceneRoot != null ? sceneRoot.Find("CaptureStage") : null;
            if (stage != null) stage.gameObject.SetActive(false);
        }

        private void ApplyObjectMesh(Mesh objectMesh, Color objectColor, bool keepPlaceholder = false,
                                     Texture2D texture = null, ScenePackage.SceneAssetInfo info = null)
        {
            if (player == null || player.grabbedObject == null) return;
            var target = player.grabbedObject;
            target.SetParent(sceneRoot, false);
            var oldLabel = target.Find("ObjectLabel");
            if (oldLabel != null) oldLabel.gameObject.SetActive(false);
            var old = target.Find("SamObjectMesh");
            if (old != null)
            {
                old.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(old.gameObject);
                else DestroyImmediate(old.gameObject);
            }
            var placeholder = target.GetComponent<Renderer>();
            if (objectMesh == null)
            {
                // No SAM object: a capture scene shows nothing invented.
                // (The basketball sample keeps its authored ball.)
                target.gameObject.SetActive(keepPlaceholder);
                if (placeholder != null) placeholder.enabled = keepPlaceholder;
                target.localScale = Vector3.one * 0.12f;
                return;
            }
            target.gameObject.SetActive(true);
            if (placeholder != null) placeholder.enabled = false;
            target.localScale = Vector3.one;
            ReplayAssetLayout.AddModel(target, objectMesh, objectColor, texture, info);
        }

        /// <summary>Floating label so the important object is identifiable in the scene.</summary>
        private static void AddObjectLabel(Transform target)
        {
            if (target == null || target.Find("ObjectLabel") != null) return;
            var go = new GameObject("ObjectLabel");
            go.transform.SetParent(target, false);
            go.transform.localPosition = new Vector3(0f, 1.4f, 0f);
            var tm = go.AddComponent<TextMesh>();
            tm.text = "Object";
            tm.fontSize = 48;
            tm.characterSize = 0.5f;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.color = new Color(1f, 0.75f, 0.3f);
        }
    }
}
