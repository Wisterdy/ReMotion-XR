using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using ReMotionXR.Core;
using ReMotionXR.Replay;

namespace ReMotionXR.Library
{
    /// <summary>
    /// Loads the Library on start. First launch copies bundled demo scenes
    /// so judges can replay offline with no server.
    /// Demo Mode ON: show only bundled READY scenes, hide server errors.
    /// Deletes always ask first (Cancel / Delete, "This cannot be undone."),
    /// then delete locally, call the server if online, refresh, and toast.
    /// </summary>
    public class LibraryManager : MonoBehaviour
    {
        public Transform cardParent;
        public SceneCardUI cardPrefab;
        public AutoProcessor autoProcessor;
        public ReplayController replayController;
        public bool demoMode = false;

        /// <summary>Raised by Watch Original Video; the main UI shows status.</summary>
        public event Action<string> OnWatchRequested;

        /// <summary>Raised after Enter Virtual Scene succeeds; the main UI shows the replay HUD.</summary>
        public event Action<string> OnEnteredScene;

        /// <summary>Test seam: overrides the persistentDataPath Library root.</summary>
        public string LibraryRootOverride;

        /// <summary>
        /// Bundled-demo content version. Installs that set the old
        /// first-launch flag without copying anything (the flag was written
        /// even when no demo existed) recover automatically: a stored
        /// version below this re-runs installation.
        /// </summary>
        public const int DemoContentVersion = 2;
        private const string DemoVersionKey = "remotion_demo_version";
        private bool _warnedMissingPrefab;

        [Header("Delete confirmation")]
        public GameObject confirmPanel;
        public Text confirmText;
        public Button confirmDeleteButton;
        public Button confirmCancelButton;

        [Header("Toast")]
        public GameObject toastPanel;
        public Text toastText;
        public float toastSeconds = 2f;

        private Action _pendingDelete;
        private GameObject _captureDetails;
        public SceneCardUI ActiveDetailsCard { get; private set; }

        public string LibraryRoot => !string.IsNullOrEmpty(LibraryRootOverride)
            ? LibraryRootOverride
            : Path.Combine(Application.persistentDataPath, "Library");

        private IEnumerator Start()
        {
            Directory.CreateDirectory(LibraryRoot);
            if (confirmDeleteButton != null) confirmDeleteButton.onClick.AddListener(ConfirmPendingDelete);
            if (confirmCancelButton != null) confirmCancelButton.onClick.AddListener(CancelPendingDelete);
            yield return InstallDemosRoutine();
            Refresh();
        }

        /// <summary>Creates one card per Library session; returns the count.</summary>
        public int Refresh()
        {
            if (cardParent == null)
            {
                Debug.LogError("[ReMotion] LibraryManager.cardParent is not assigned; Library cannot render.");
                return 0;
            }
            if (cardPrefab == null && !_warnedMissingPrefab)
            {
                _warnedMissingPrefab = true;
                Debug.LogWarning("[ReMotion] LibraryManager.cardPrefab is not assigned; " +
                                 "building cards procedurally. Assign the SceneCard prefab in the scene.");
            }
            CloseCaptureDetails();
            EnsureGridLayout();
            foreach (Transform child in cardParent)
            {
                if (Application.isPlaying) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
                else DestroyImmediate(child.gameObject);
            }
            int created = 0;
            foreach (string dir in Directory.GetDirectories(LibraryRoot).OrderByDescending(CaptureTimeUtc))
            {
                string metaPath = Path.Combine(dir, "metadata.json");
                if (!File.Exists(metaPath) && !File.Exists(Path.Combine(dir, "original_video.mp4")) && !File.Exists(Path.Combine(dir, "scene.remotion"))) continue;
                SessionMetadata meta = File.Exists(metaPath)
                    ? JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath))
                    : new SessionMetadata { sessionId = Path.GetFileName(dir), displayName = "Saved capture", status = "Saved", samStatus = "Unavailable" };
                // Saved captures stay visible regardless of cloud state or legacy demoMode.
                var card = SceneCardFactory.CreatePosterCard(cardParent);
                card.Bind(meta, this);
                created++;
            }
            Debug.Log($"[ReMotionGallery] cards={created} columns=5 contentSelf={cardParent.gameObject.activeSelf} hierarchy={cardParent.gameObject.activeInHierarchy} parent={cardParent.parent?.name}");
            return created;
        }

        private static DateTime CaptureTimeUtc(string folder)
        {
            string video = Path.Combine(folder, "original_video.mp4");
            return File.Exists(video) ? File.GetLastWriteTimeUtc(video) : Directory.GetCreationTimeUtc(folder);
        }

        private void EnsureLibraryViewport()
        {
            if (!(cardParent is RectTransform) || cardParent.GetComponentInParent<Canvas>() == null) return;
            var scroll = cardParent.parent != null ? cardParent.parent.GetComponent<ScrollRect>() : null;
            if (scroll == null)
            {
                var viewport = new GameObject("SceneListViewport", typeof(RectTransform), typeof(Image), typeof(RectMask2D), typeof(ScrollRect));
                viewport.transform.SetParent(cardParent.parent, false);
                viewport.SetActive(cardParent.gameObject.activeSelf);
                viewport.GetComponent<Image>().color = new Color(0,0,0,.03f);
                scroll = viewport.GetComponent<ScrollRect>();
                cardParent.SetParent(viewport.transform, false);
            }
            var viewportRect = (RectTransform)scroll.transform;
            viewportRect.anchorMin = viewportRect.anchorMax = viewportRect.pivot = new Vector2(.5f,.5f);
            viewportRect.sizeDelta = new Vector2(1160,430);
            viewportRect.anchoredPosition = new Vector2(0,-80);
            // Legacy mode switching hid SceneList itself. After wrapping,
            // the viewport owns visibility; its content must be locally active.
            cardParent.gameObject.SetActive(true);
            scroll.content = (RectTransform)cardParent; scroll.viewport = viewportRect;
            scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var controller = FindFirstObjectByType<ReMotionMainUIController>();
            if (controller != null && controller.library == this) controller.libraryPanel = scroll.gameObject;
        }

        // ---------- Card actions (real implementations, no log-only stubs) ----------

        /// <summary>Self-heal: saved scenes ship with this field unwired, which made Enter Virtual Scene dead on device.</summary>
        public ReplayController ResolveReplayController()
        {
            if (replayController != null) return replayController;
            replayController = FindFirstObjectByType<ReplayController>();
            if (replayController == null)
            {
                var go = new GameObject("ReplayController");
                replayController = go.AddComponent<ReplayController>();
            }
            replayController.EnsureBuilt();
            return replayController;
        }

        /// <summary>Immersive entry is SAM-gated: no SAM model, no scene.</summary>
        public bool IsSamReady(string sessionId)
        {
            string metaPath = Path.Combine(SessionFolder(sessionId), "metadata.json");
            if (!File.Exists(metaPath)) return true; // legacy/demo sessions
            var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath));
            return meta == null || meta.SamReady;
        }

        public bool EnterScene(string sessionId)
        {
            if (!IsSamReady(sessionId))
            {
                ShowToast("Immersive scene is waiting for the SAM model…");
                Debug.Log($"[ReMotion] Enter blocked: SAM model not ready for {sessionId}.");
                return false;
            }
            if (ResolveReplayController() == null)
            {
                Debug.LogError("[ReMotion] No ReplayController available; cannot enter virtual scene.");
                return false;
            }
            bool ok = replayController.EnterSession(sessionId, SessionFolder(sessionId), out string err)
                      || LogEnterError(err);
            if (ok) OnEnteredScene?.Invoke(sessionId);
            return ok;
        }

        /// <summary>Netflix-style grid: poster tiles, at least two per row.</summary>
        public void EnsureGridLayout()
        {
            // GridLayoutGroup needs a RectTransform; swapping one in would
            // destroy a plain Transform parent, so only real UI parents grid.
            if (cardParent == null || !(cardParent is RectTransform)) return;
            EnsureLibraryViewport();
            var vertical = cardParent.GetComponent<VerticalLayoutGroup>();
            if (vertical != null) vertical.enabled = false;
            var grid = cardParent.GetComponent<GridLayoutGroup>();
            if (grid == null) grid = cardParent.gameObject.AddComponent<GridLayoutGroup>();
            grid.cellSize = SceneCardFactory.PosterSize;
            grid.spacing = new Vector2(12f, 16f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            // Fixed five across: do not infer columns before Unity's first layout pass.
            grid.constraintCount = 5;
            var contentRect = (RectTransform)cardParent;
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(.5f, 1);
            contentRect.sizeDelta = new Vector2(0, contentRect.sizeDelta.y);
            contentRect.anchoredPosition = new Vector2(0, Mathf.Max(0, contentRect.anchoredPosition.y));
            var fitter = cardParent.GetComponent<ContentSizeFitter>();
            if (fitter == null) fitter = cardParent.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            grid.childAlignment = TextAnchor.UpperCenter;
            grid.padding = new RectOffset(12, 12, 12, 12);
        }

        public void CloseCaptureDetails()
        {
            ActiveDetailsCard = null;
            if (_captureDetails == null) return;
            _captureDetails.SetActive(false);
            if (Application.isPlaying) Destroy(_captureDetails); else DestroyImmediate(_captureDetails);
            _captureDetails = null;
        }

        public void ShowCaptureDetails(SessionMetadata meta)
        {
            CloseCaptureDetails();
            var parent = cardParent != null && cardParent.parent != null ? cardParent.parent : transform;
            var backdrop = ReMotionUiTheme.MakePanel(parent, "CaptureDetailsOverlay", new Color(.025f,.035f,.05f,.98f));
            _captureDetails = backdrop.gameObject;
            var rt = (RectTransform)backdrop.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            var card = SceneCardFactory.CreateCard(backdrop.transform);
            ((RectTransform)card.transform).anchoredPosition = new Vector2(0,-22);
            card.Bind(meta, this); ActiveDetailsCard = card;
            var close = ReMotionUiTheme.Button(backdrop.transform, "CloseCaptureDetails", "Back to library",
                ReMotionUiTheme.ButtonStyle.Secondary, 16);
            ((RectTransform)close.transform).sizeDelta = new Vector2(340,44);
            ((RectTransform)close.transform).anchoredPosition = new Vector2(0,168);
            close.onClick.AddListener(CloseCaptureDetails);
            backdrop.transform.SetAsLastSibling();
        }

        public bool EnterRetrain(string sessionId)
        {
            if (!EnterScene(sessionId)) return false;
            replayController.SetRetrain(true);
            return true;
        }

        public bool EnterGodMode(string sessionId)
        {
            if (!EnterScene(sessionId)) return false;
            replayController.SetGodMode(true);
            return true;
        }

        public void WatchVideo(string sessionId)
        {
            Debug.Log($"[ReMotion] Watch original video requested: {sessionId}");
            if (OnWatchRequested != null) { OnWatchRequested.Invoke(sessionId); return; }
            // No UI controller subscribed (saved-scene wiring gap): open via
            // the controller if present, else the video player directly.
            var controller = FindFirstObjectByType<ReMotionMainUIController>();
            if (controller != null) { controller.ShowVideo(sessionId); return; }
            var player = FindFirstObjectByType<VideoPlaybackController>();
            if (player != null)
            {
                string folder = SessionFolder(sessionId);
                player.Show(sessionId, folder, sessionId);
                return;
            }
            Debug.LogError("[ReMotion] Watch Video: no UI controller or video player in scene.");
        }

        public void RebuildScene(string sessionId)
        {
            if (autoProcessor == null)
            {
                Debug.LogError("[ReMotion] No AutoProcessor wired; cannot rebuild scene.");
                return;
            }
            autoProcessor.UploadSession(sessionId);
        }

        private static bool LogEnterError(string err)
        {
            Debug.LogError("[ReMotion] Enter virtual scene failed: " + err);
            return false;
        }

        public void SetDemoMode(bool on) { demoMode = on; Refresh(); }

        // ---------- Delete feature ----------

        public void RequestDeleteOriginalVideo(string id) =>
            ShowConfirm("Delete the original video only?\nThis cannot be undone.",
                        () => DeleteOriginalVideo(id));

        public void RequestDeleteVirtualScene(string id) =>
            ShowConfirm("Delete the virtual scene only?\nThis cannot be undone.",
                        () => DeleteVirtualScene(id));

        public void RequestDeleteEntireScene(string id) =>
            ShowConfirm("Delete the entire scene, including the original video?\nThis cannot be undone.",
                        () => DeleteEntireScene(id));

        /// <summary>Deletes Library/{id} original video files only.</summary>
        public void DeleteOriginalVideo(string sessionId)
        {
            string dir = SessionFolder(sessionId);
            DeleteIfExists(Path.Combine(dir, "original_video.mp4"));
            DeleteIfExists(Path.Combine(dir, "original_video.txt"));
            DeleteIfExists(Path.Combine(dir, "thumbnail.jpg"));
            if (autoProcessor != null) StartCoroutine(autoProcessor.DeleteOnServer(sessionId, "original"));
            AfterDelete("Original video deleted");
        }

        /// <summary>Deletes Library/{id} scene.remotion only; Rebuild stays available.</summary>
        public void DeleteVirtualScene(string sessionId)
        {
            DeleteIfExists(Path.Combine(SessionFolder(sessionId), "scene.remotion"));
            if (autoProcessor != null) StartCoroutine(autoProcessor.DeleteOnServer(sessionId, "virtual"));
            AfterDelete("Virtual scene deleted");
        }

        /// <summary>Deletes the whole Library/{id} folder.</summary>
        public void DeleteEntireScene(string sessionId)
        {
            string dir = SessionFolder(sessionId);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (autoProcessor != null) StartCoroutine(autoProcessor.DeleteOnServer(sessionId, "all"));
            AfterDelete("Scene deleted");
        }

        // Backward-compatible alias used by older cards.
        public void DeleteSession(string sessionId) => DeleteEntireScene(sessionId);

        public void RenameSession(string sessionId, string newName)
        {
            string metaPath = Path.Combine(SessionFolder(sessionId), "metadata.json");
            if (!File.Exists(metaPath)) return;
            var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath));
            meta.displayName = newName;
            File.WriteAllText(metaPath, JsonUtility.ToJson(meta, true));
            Refresh();
        }

        public bool HasOriginalVideo(string sessionId) =>
            File.Exists(Path.Combine(SessionFolder(sessionId), "original_video.mp4")) ||
            File.Exists(Path.Combine(SessionFolder(sessionId), "original_video.txt"));

        public bool HasVirtualScene(string sessionId) =>
            File.Exists(Path.Combine(SessionFolder(sessionId), "scene.remotion"));

        public string SessionFolder(string sessionId) => Path.Combine(LibraryRoot, sessionId);

        // ---------- Confirmation + toast ----------

        /// <summary>
        /// Saved scenes never wired the delete dialog (device: "Delete
        /// confirmation panel is not wired; delete NOT performed"). Build
        /// it under the world canvas on first use, frontmost.
        /// </summary>
        public void EnsureDeleteConfirmUi()
        {
            if (confirmPanel != null) return;
            // Owned by LibraryRoot (never the bare canvas), so the
            // dialog dies with the Library view and cannot leak.
            var parent = LibraryRootTransform();
            if (parent == null)
            {
                var canvas = FindFirstObjectByType<Canvas>();
                if (canvas == null) return;
                parent = canvas.transform;
            }
            var panel = ReMotionUiTheme.MakePanel(parent, "DeleteConfirmPanel", ReMotionUiTheme.Background);
            confirmPanel = panel.gameObject;
            ReMotionUiTheme.SetSize(confirmPanel.transform, new Vector2(560f, 240f));
            var layout = confirmPanel.AddComponent<UnityEngine.UI.VerticalLayoutGroup>();
            layout.childControlWidth = true; layout.childControlHeight = false;
            layout.spacing = 14f; layout.padding = new RectOffset(22, 22, 18, 18);
            confirmText = ReMotionUiTheme.Label(confirmPanel.transform, "ConfirmText",
                "Delete?\nThis cannot be undone.", 17, ReMotionUiTheme.TextPrimary, TextAnchor.MiddleCenter);
            ReMotionUiTheme.SetSize(confirmText.transform, new Vector2(500f, 70f));
            var rowGo = new GameObject("ConfirmActions");
            rowGo.transform.SetParent(confirmPanel.transform, false);
            var row = rowGo.AddComponent<UnityEngine.UI.HorizontalLayoutGroup>();
            row.childControlWidth = false; row.childControlHeight = false; row.spacing = 16f;
            row.childAlignment = TextAnchor.MiddleCenter;
            confirmCancelButton = ReMotionUiTheme.Button(rowGo.transform, "ConfirmCancelButton",
                "Cancel", ReMotionUiTheme.ButtonStyle.Secondary, 15);
            ReMotionUiTheme.SetSize(confirmCancelButton.transform, new Vector2(170f, 48f));
            confirmDeleteButton = ReMotionUiTheme.Button(rowGo.transform, "ConfirmDeleteButton",
                "Delete", ReMotionUiTheme.ButtonStyle.Danger, 15);
            ReMotionUiTheme.SetSize(confirmDeleteButton.transform, new Vector2(170f, 48f));
            confirmDeleteButton.onClick.AddListener(ConfirmPendingDelete);
            confirmCancelButton.onClick.AddListener(CancelPendingDelete);
            confirmPanel.SetActive(false);
        }

        private void ShowConfirm(string message, Action onConfirm)
        {
            EnsureDeleteConfirmUi();
            _pendingDelete = onConfirm;
            if (confirmText != null) confirmText.text = message;
            if (confirmPanel != null)
            {
                confirmPanel.transform.SetAsLastSibling(); // never behind video/cards
                confirmPanel.SetActive(true);
            }
            else
            {
                // Fail closed: an unwired dialog must never delete immediately.
                _pendingDelete = null;
                Debug.LogWarning("[ReMotion] Delete confirmation panel is not wired; delete NOT performed.");
            }
        }

        private void ConfirmPendingDelete()
        {
            if (confirmPanel != null) confirmPanel.SetActive(false);
            var action = _pendingDelete;
            _pendingDelete = null;
            action?.Invoke();
        }

        /// <summary>Cancels any pending delete + dialog (view transitions call this).</summary>
        public void CancelPendingDelete()
        {
            _pendingDelete = null;
            if (confirmPanel != null) confirmPanel.SetActive(false);
        }

        /// <summary>The Library content root that owns dialogs/toasts/cards.</summary>
        public Transform LibraryRootTransform()
        {
            for (var t = cardParent; t != null; t = t.parent)
                if (t.name == "LibraryRoot") return t;
            return cardParent;
        }

        private void AfterDelete(string message)
        {
            Refresh();
            ShowToast("Deleted: " + message);
        }

        private void ShowToast(string message)
        {
            if (toastText != null) toastText.text = message;
            if (toastPanel != null) StartCoroutine(ToastRoutine());
            Debug.Log("[ReMotion] " + message);
        }

        private IEnumerator ToastRoutine()
        {
            toastPanel.SetActive(true);
            yield return new WaitForSeconds(toastSeconds);
            toastPanel.SetActive(false);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private bool IsDemoScene(string dir) => File.Exists(Path.Combine(dir, "scene.remotion"));

        // ---------- Offline demo installation ----------
        // StreamingAssets cannot be enumerated with Directory/File APIs on
        // Android (it lives inside the APK), so the bundled package is read
        // through UnityWebRequest. If no package file ships, the built-in
        // deterministic DemoTimeline package is installed instead — the
        // offline demo therefore always exists. Completion flags are only
        // written after a validated install; failures retry next launch.

        private IEnumerator InstallDemosRoutine()
        {
            if (PlayerPrefs.GetInt(DemoVersionKey, 0) >= DemoContentVersion) yield break;

            bool installed = false;
            string bundledPath = Path.Combine(Application.streamingAssetsPath,
                                              "DemoScenes", DemoTimeline.FileName);
            using (var req = UnityWebRequest.Get(bundledPath))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.Success &&
                    ScenePackage.TryParseTimeline(req.downloadHandler.data, out _, out _))
                {
                    string destDir = Path.Combine(LibraryRoot, DemoTimeline.SessionId);
                    Directory.CreateDirectory(destDir);
                    File.WriteAllBytes(Path.Combine(destDir, "scene.remotion"), req.downloadHandler.data);
                    WriteDemoMetadata(destDir);
                    installed = true;
                }
            }
            if (!installed)
                installed = TryInstallBundledDemo(LibraryRoot, out string err)
                            || LogInstallError(err);

            if (installed)
            {
                PlayerPrefs.SetInt(DemoVersionKey, DemoContentVersion);
                PlayerPrefs.SetInt("remotion_first_launch_done", 1);
                PlayerPrefs.Save();
                Debug.Log("[ReMotion] Offline demo installed: " + DemoTimeline.SessionId);
            }
            else
            {
                Debug.LogWarning("[ReMotion] Offline demo install failed; will retry next launch.");
            }
        }

        private static bool LogInstallError(string err)
        {
            Debug.LogWarning("[ReMotion] Bundled demo install failed: " + err);
            return false;
        }

        /// <summary>
        /// Installs the built-in deterministic demo package + READY metadata
        /// under libraryRoot/{DemoTimeline.SessionId}. Pure file IO so it is
        /// unit-testable without a device or StreamingAssets.
        /// </summary>
        public static bool TryInstallBundledDemo(string libraryRoot, out string error)
        {
            error = null;
            if (!ScenePackage.TryCreatePackage(DemoTimeline.Create(), out byte[] pkg, out error))
                return false;
            try
            {
                string destDir = Path.Combine(libraryRoot, DemoTimeline.SessionId);
                Directory.CreateDirectory(destDir);
                File.WriteAllBytes(Path.Combine(destDir, "scene.remotion"), pkg);
                WriteDemoMetadata(destDir);
                return true;
            }
            catch (Exception e)
            {
                error = "Demo install failed: " + e.Message;
                return false;
            }
        }

        private static void WriteDemoMetadata(string destDir)
        {
            var timeline = DemoTimeline.Create();
            var meta = new SessionMetadata
            {
                sessionId = DemoTimeline.SessionId,
                displayName = DemoTimeline.DisplayName,
                status = "Ready",
                actionSequence = timeline.action_sequence,
                durationSeconds = timeline.duration_seconds,
            };
            File.WriteAllText(Path.Combine(destDir, "metadata.json"),
                              JsonUtility.ToJson(meta, true));
            // The demo is a sample capture with no camera footage; the file
            // enables Watch Video, which opens the player in Placeholder state.
            string videoNote = Path.Combine(destDir, "original_video.txt");
            if (!File.Exists(videoNote))
                File.WriteAllText(videoNote,
                    "Sample capture: no camera video. Enter the Virtual Scene to play it in 3D.");
        }
    }
}
