using System.IO;
using UnityEngine;
using UnityEngine.UI;
using ReMotionXR.Capture;
using ReMotionXR.Library;
using ReMotionXR.Replay;

namespace ReMotionXR.Core
{
    /// <summary>
    /// App shell with four modes the user switches between with the top tabs:
    /// Capture (name any action, Record, perform, Stop &amp; Save), Library
    /// (Netflix-style poster grid; each capture offers Watch Video or Enter
    /// Virtual Scene), Video (the regular recording), and Replay (the 3D
    /// virtual scene with its playback HUD). Capture is free-form — there
    /// are no predefined sport or lesson types.
    /// All wiring happens in WireHandlers/UnwireHandlers — never via
    /// editor-time AddListener lambdas, which Unity does not persist.
    /// Missing panels/tabs/players are built procedurally in EnsureUi so
    /// already-saved scenes gain the modes without a scene rebuild.
    /// </summary>
    public class ReMotionMainUIController : MonoBehaviour
    {
        // Explicit values: serialized consumers depend on them.
        public enum AppMode { Capture = 0, Library = 1, Video = 2, Replay = 3, Server = 4 }

        public SessionRecorder recorder;
        public AutoProcessor processor;
        public LibraryManager library;
        public ReplayController replayController;
        public Button recordButton;
        public Button saveButton;
        public Text statusText;

        [Header("Modes")]
        public GameObject capturePanel;
        public GameObject libraryPanel;
        public Button captureTabButton;
        public Button libraryTabButton;
        public InputField captureTitleInput;
        public Text captureInstructionText;
        public VideoPlaybackController videoPlayer;
        public ReplayHudController replayHud;
        public Button serverSettingsButton;
        public GameObject serverSettingsPanel;
        public InputField endpointInput;
        public InputField apiKeyInput;

        [Header("View roots (exactly one active in menu modes)")]
        public GameObject serverRoot;
        public GameObject captureRoot;
        public GameObject libraryRoot;
        public Text serverStatusText;
        public Text libraryStatusText;
        private GameObject _headerRow;

        public AppMode CurrentMode { get; private set; } = AppMode.Capture;

        private bool _wired;
        private Canvas _canvas;
        private Text _versionLabel;

        private void Awake()
        {
            // Device logs showed two live controllers (saved-scene + bootstrap)
            // building duplicate panels that covered Record/Capture and
            // double-fired Watch/Enter. Keep the first, retire the rest.
            var all = FindObjectsByType<ReMotionMainUIController>(FindObjectsSortMode.None);
            if (all.Length > 1 && all[0] != this)
            {
                Debug.LogWarning("[ReMotion] Duplicate MainUIController retired.");
                enabled = false;
                return;
            }
            XrUiInputSetup.EnsureEventSystem();
            EnsureUi();
        }

        private void Start() => ShowCapture();

        private void OnEnable() => WireHandlers();
        private void OnDisable() => UnwireHandlers();

        // ---------- Mode switching ----------

        /// <summary>Tabs and Record must never sit behind a panel: front them on every mode switch.</summary>
        private void BringNavigationToFront()
        {
            if (captureTabButton != null) captureTabButton.transform.SetAsLastSibling();
            if (libraryTabButton != null) libraryTabButton.transform.SetAsLastSibling();
            if (recordButton != null) recordButton.transform.SetAsLastSibling();
            if (saveButton != null) saveButton.transform.SetAsLastSibling();
            if (capturePanel != null && CurrentMode == AppMode.Capture)
                capturePanel.transform.SetAsLastSibling();
            // Tabs/Record last: frontmost, always tappable.
            if (captureTabButton != null) captureTabButton.transform.SetAsLastSibling();
            if (libraryTabButton != null) libraryTabButton.transform.SetAsLastSibling();
            if (recordButton != null) recordButton.transform.SetAsLastSibling();
            if (saveButton != null) saveButton.transform.SetAsLastSibling();
        }

        /// <summary>
        /// THE one place screen visibility is decided (presentation layer
        /// owns state; events only request mode changes). Exactly one
        /// screen is visible per mode, tabs are present in every menu
        /// mode, and recording shows Stop alone. Every Show* method sets
        /// CurrentMode and calls this — no method toggles panels itself.
        /// </summary>
        private void ApplyModeVisibility()
        {
            bool recording = IsRecordingUi;
            // THE invariant: exactly one content root in Server/Capture/
            // Library mode; none of the three in Video/Replay. Inactive
            // roots are SetActive(false) — not rendered, not raycastable.
            SetActive(serverRoot, CurrentMode == AppMode.Server);
            SetActive(captureRoot, CurrentMode == AppMode.Capture);
            SetActive(libraryRoot, CurrentMode == AppMode.Library);
            SetActive(libraryPanel, CurrentMode == AppMode.Library);
            SetActive(serverSettingsPanel, CurrentMode == AppMode.Server);
            SetActive(capturePanel, false); // legacy panel stays retired

            bool menuMode = !recording &&
                (CurrentMode == AppMode.Capture || CurrentMode == AppMode.Library
                 || CurrentMode == AppMode.Server);
            // Shared navigation stays visible in menu modes; the current
            // tab is styled selected so the view is unambiguous.
            if (_headerRow != null)
            {
                _headerRow.SetActive(menuMode);
                if (menuMode) _headerRow.transform.SetAsLastSibling();
            }
            if (captureTabButton != null) captureTabButton.gameObject.SetActive(menuMode);
            if (libraryTabButton != null) libraryTabButton.gameObject.SetActive(menuMode);
            if (serverSettingsButton != null) serverSettingsButton.gameObject.SetActive(menuMode);
            StyleTab(captureTabButton, CurrentMode == AppMode.Capture);
            StyleTab(libraryTabButton, CurrentMode == AppMode.Library);
            StyleTab(serverSettingsButton, CurrentMode == AppMode.Server);
            if (_versionLabel != null)
                _versionLabel.gameObject.SetActive(menuMode);
            // While recording, Capture shows Stop alone (Record hidden,
            // not merely disabled) — the root itself stays active.
            if (recordButton != null) recordButton.gameObject.SetActive(!recording);
            UpdateRecordButtons(); // Stop visibility is recorder-owned
            if (CurrentMode != AppMode.Video && videoPlayer != null && videoPlayer.IsOpen)
                videoPlayer.Close();
            if (CurrentMode != AppMode.Replay)
            {
                if (replayHud != null && replayHud.IsOpen) replayHud.Hide();
                if (!recording) ReplayController.ExitAnyActiveScene();
            }
        }

        private static void StyleTab(Button tab, bool selected)
        {
            if (tab == null) return;
            ReMotionUiTheme.ApplyStyle(tab,
                selected ? ReMotionUiTheme.ButtonStyle.Primary
                         : ReMotionUiTheme.ButtonStyle.Secondary, 15);
        }

        /// <summary>
        /// The one transition path for Server/Capture/Library. Guards
        /// first (no state change when rejected), outgoing transient
        /// state cancelled, then the destination root alone activates.
        /// </summary>
        public bool TransitionTo(AppMode destination)
        {
            // Busy lock covers the whole attempt, not just Recording:
            // startup/finalization retain Capture (bounded by watchdog).
            if (IsRecordingUi || IsRecorderBusy) return false;
            if (destination != AppMode.Capture && destination != AppMode.Library
                && destination != AppMode.Server)
                return false;
            CancelTransientState();
            CurrentMode = destination;
            ApplyModeVisibility();
            return true;
        }

        private void CancelTransientState()
        {
            if (library != null) library.CancelPendingDelete();
            if (_canvas != null)
                foreach (var dwell in _canvas.GetComponentsInChildren<ReMotionXR.Accessibility.DwellClick>(true))
                    dwell.EndDwell();
        }

        public void ShowCapture()
        {
            EnsureUi();
            if (!TransitionTo(AppMode.Capture)) return;
            if (videoPlayer != null) videoPlayer.Close();
            if (replayHud != null) replayHud.Hide();
            ReplayController.ExitAnyActiveScene(); // clean world, any instance
            // Pre-warm the passthrough camera now so Record starts instantly.
            if (recorder != null) recorder.EnsureCameraCapture().PreWarm();
            BringNavigationToFront();
            SetStatus("Capture: press Record, perform any action.");
        }

        public void ShowLibrary()
        {
            EnsureUi();
            if (!TransitionTo(AppMode.Library)) return;
            ReplayController.ExitAnyActiveScene(); // Library is a menu, not a stage
            if (videoPlayer != null && videoPlayer.IsOpen) videoPlayer.Close();
            if (replayHud != null) replayHud.Hide();
            if (library != null) library.Refresh();
        }

        /// <summary>Enters Server mode (a real view, never an overlay).</summary>
        public void ShowServer()
        {
            EnsureUi();
            if (!TransitionTo(AppMode.Server)) return;
            var settings = ResolveServerSettings();
            if (settings != null)
            {
                settings.LoadRuntimeOverrides();
                if (endpointInput != null) endpointInput.text = settings.GatewayUrl;
                if (apiKeyInput != null) apiKeyInput.text = settings.remotionApiKey;
            }
        }

        public void ShowVideo(string sessionId)
        {
            if (IsRecordingUi) return; // no video popup while recording
            EnsureUi();
            if (videoPlayer == null || library == null) return;
            CurrentMode = AppMode.Video;
            ApplyModeVisibility();
            string folder = library.SessionFolder(sessionId);
            string name = sessionId;
            string metaPath = Path.Combine(folder, "metadata.json");
            if (File.Exists(metaPath))
            {
                var meta = JsonUtility.FromJson<SessionMetadata>(File.ReadAllText(metaPath));
                if (meta != null && !string.IsNullOrEmpty(meta.displayName)) name = meta.displayName;
            }
            videoPlayer.Show(sessionId, folder, name);
        }

        public void ShowReplay(string sessionId)
        {
            if (IsRecordingUi) return; // no virtual scene popup while recording
            EnsureUi();
            if (replayHud == null) return;
            CurrentMode = AppMode.Replay;
            ApplyModeVisibility();
            replayHud.Show();
            SetStatus("Virtual scene: " + sessionId);
        }

        private static void SetActive(GameObject go, bool on)
        { if (go != null) go.SetActive(on); }

        // ---------- Procedural UI (saved scenes gain modes at runtime) ----------

        public void EnsureUi()
        {
            if (_canvas == null) _canvas = FindCanvas();
            if (_canvas == null)
            {
                // No canvas (minimal fixtures): roots still own the
                // loose controls so mode isolation holds everywhere.
                var fallback = recordButton?.transform?.parent ?? transform;
                EnsureViewRoots(fallback);
                WireHandlers();
                return;
            }
            Transform root = _canvas.transform;

            if (libraryPanel == null)
            {
                // The saved scene has no SceneListViewport: the card grid
                // (SceneList) hangs straight off the canvas, so the old
                // name lookup always missed on device and the Library
                // grid leaked into every mode. Resolve from the Library
                // itself — cardParent is what actually holds the cards.
                if (library == null) library = FindFirstObjectByType<LibraryManager>();
                if (library != null && library.cardParent != null)
                    libraryPanel = library.cardParent.GetComponentInParent<UnityEngine.UI.ScrollRect>()?.gameObject ?? library.cardParent.gameObject;
                if (libraryPanel == null)
                {
                    var viewport = root.Find("SceneListViewport") ?? root.Find("SceneList");
                    if (viewport != null) libraryPanel = viewport.gameObject;
                }
            }
            if (capturePanel != null) capturePanel.SetActive(false); // Capture tab is Record/Stop only
            if (captureTabButton == null)
                captureTabButton = BuildTab(root, "CaptureTabButton", "●  Capture", new Vector2(245f, 212f));
            if (libraryTabButton == null)
                libraryTabButton = BuildTab(root, "LibraryTabButton", "▦  Library", new Vector2(425f, 212f));
            if (videoPlayer == null)
            {
                videoPlayer = FindFirstObjectByType<VideoPlaybackController>();
                if (videoPlayer == null)
                {
                    var go = new GameObject("VideoPlaybackController");
                    videoPlayer = go.AddComponent<VideoPlaybackController>();
                }
                videoPlayer.EnsureBuilt(root);
                if (videoPlayer.panel != null) Place(videoPlayer.panel.transform, Vector2.zero, new Vector2(880f, 560f));
            }
            // Saved-scene Record/Save ship as white buttons with white text
            // (invisible, fails contrast). Restyle them in place every load.
            if (recordButton != null)
                ReMotionUiTheme.ApplyStyle(recordButton, ReMotionUiTheme.ButtonStyle.Record, 18);
            if (saveButton != null)
                ReMotionUiTheme.ApplyStyle(saveButton, ReMotionUiTheme.ButtonStyle.Primary, 18);
            if (captureTabButton != null)
                ReMotionUiTheme.ApplyStyle(captureTabButton, ReMotionUiTheme.ButtonStyle.Secondary, 15);
            if (libraryTabButton != null)
                ReMotionUiTheme.ApplyStyle(libraryTabButton, ReMotionUiTheme.ButtonStyle.Secondary, 15);
            // Save only makes sense while recording; label it as the stop action.
            var saveLabel = saveButton != null ? saveButton.GetComponentInChildren<Text>() : null;
            if (saveLabel != null) saveLabel.text = "■  Stop & Save";
            var recordLabel = recordButton != null ? recordButton.GetComponentInChildren<Text>() : null;
            if (recordLabel != null && string.IsNullOrWhiteSpace(recordLabel.text))
                recordLabel.text = "●  Record";
            if (library == null) library = FindFirstObjectByType<LibraryManager>();
            if (serverSettingsButton == null) BuildServerSettingsUi(root);
            // Small build label, bottom corner: which build is this headset
            // running? Auto-stamped at build time (BuildInfo).
            if (_versionLabel == null)
            {
                var existing = root.Find("VersionLabel");
                _versionLabel = existing != null ? existing.GetComponent<Text>() : null;
            }
            if (_versionLabel == null)
            {
                _versionLabel = ReMotionUiTheme.Label(root, "VersionLabel",
                    BuildInfo.Label, 11, ReMotionUiTheme.TextSecondary, TextAnchor.MiddleLeft);
                ReMotionUiTheme.SetSize(_versionLabel.transform, new Vector2(460f, 22f));
                var versionRt = _versionLabel.GetComponent<RectTransform>();
                versionRt.anchoredPosition = new Vector2(-360f, -332f);
            }
            if (replayController == null) replayController = FindFirstObjectByType<ReplayController>();
            // Saved scenes leave these cross-refs unwired, which killed
            // Watch Video / Enter Virtual Scene on device. Sync both ways.
            if (library != null)
            {
                if (library.replayController == null && replayController != null)
                    library.replayController = replayController;
                if (library.autoProcessor == null && processor != null)
                    library.autoProcessor = processor;
                if (replayController == null && library.replayController != null)
                    replayController = library.replayController;
            }
            EnsureHeaderRow(root);
            EnsureViewRoots(root);
            // Normalize every existing button label on this canvas
            // (saved-scene labels included) exactly once per build.
            foreach (var btn in _canvas.GetComponentsInChildren<Button>(true))
                ReMotionUiTheme.NormalizeButtonLabel(btn);
            if (replayHud == null)
            {
                replayHud = FindFirstObjectByType<ReplayHudController>();
                if (replayHud == null)
                {
                    var go = new GameObject("ReplayHudController");
                    replayHud = go.AddComponent<ReplayHudController>();
                }
                replayHud.EnsureBuilt(root, replayController);
                if (replayHud.panel != null) Place(replayHud.panel.transform, new Vector2(0f, -255f), new Vector2(880f, 150f));
            }
            WireHandlers();
        }

        /// <summary>Tabs + Server in one non-overlapping layout row (the old
        /// hand-placed centres overlapped by 47.5 units).</summary>
        private void EnsureHeaderRow(Transform root)
        {
            var row = root.Find("HeaderRow");
            GameObject rowGo;
            if (row == null)
            {
                rowGo = new GameObject("HeaderRow");
                rowGo.transform.SetParent(root, false);
                var rt = rowGo.AddComponent<RectTransform>();
                rt.anchoredPosition = new Vector2(0f, 212f);
                rt.sizeDelta = new Vector2(700f, 44f);
                var layout = rowGo.AddComponent<HorizontalLayoutGroup>();
                layout.spacing = 12f;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = false;
            }
            else rowGo = row.gameObject;
            _headerRow = rowGo;
            foreach (var btn in new[] { captureTabButton, libraryTabButton, serverSettingsButton })
            {
                if (btn == null || btn.transform.parent == rowGo.transform) continue;
                btn.transform.SetParent(rowGo.transform, false);
                var le = btn.GetComponent<LayoutElement>() ?? btn.gameObject.AddComponent<LayoutElement>();
                le.preferredHeight = 44f;
                le.preferredWidth = btn == serverSettingsButton ? 150f : 165f;
            }
        }

        /// <summary>
        /// Creates the three exclusive content roots (idempotent) and
        /// moves each view's existing objects under its owner: Capture
        /// controls/status, the complete Library panel, the Server panel.
        /// The receiving canvas, rig and beam components never move.
        /// </summary>
        private void EnsureViewRoots(Transform root)
        {
            captureRoot ??= FindOrCreateRoot(root, "CaptureRoot");
            libraryRoot ??= FindOrCreateRoot(root, "LibraryRoot");
            serverRoot ??= FindOrCreateRoot(root, "ServerRoot");

            Reparent(recordButton?.transform, captureRoot.transform);
            Reparent(saveButton?.transform, captureRoot.transform);
            Reparent(statusText?.transform, captureRoot.transform);
            // Decorative text must never capture the pinch ray.
            if (statusText != null) statusText.raycastTarget = false;
            if (_versionLabel != null) _versionLabel.raycastTarget = false;
            Reparent(captureTitleInput?.transform, captureRoot.transform);
            Reparent(captureInstructionText?.transform, captureRoot.transform);
            Reparent(capturePanel?.transform, captureRoot.transform);
            Reparent(libraryPanel?.transform, libraryRoot.transform);
            Reparent(serverSettingsPanel?.transform, serverRoot.transform);
            if (library != null)
            {
                // Saved scenes may carry unassigned dialog references;
                // a missing dialog must never break menu construction.
                try
                {
                    Reparent(library.confirmPanel?.transform, libraryRoot.transform);
                    Reparent(library.toastPanel?.transform, libraryRoot.transform);
                }
                catch (System.Exception e) { Debug.LogWarning("[ReMotion] Library dialog reparent skipped: " + e.Message); }
            }
            if (serverStatusText == null && serverRoot != null)
            {
                serverStatusText = ReMotionUiTheme.Label(serverRoot.transform,
                    "ServerStatus", "", 13, ReMotionUiTheme.TextSecondary);
                ReMotionUiTheme.SetSize(serverStatusText.transform, new Vector2(700f, 24f));
            }
            if (libraryStatusText == null && libraryRoot != null)
            {
                libraryStatusText = ReMotionUiTheme.Label(libraryRoot.transform,
                    "LibraryStatus", "", 13, ReMotionUiTheme.TextSecondary);
                ReMotionUiTheme.SetSize(libraryStatusText.transform, new Vector2(1100f, 24f));
                ((RectTransform)libraryStatusText.transform).anchoredPosition = new Vector2(0,-312);
            }
        }

        private static GameObject FindOrCreateRoot(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing != null) return existing.gameObject;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return go;
        }

        private static void Reparent(Transform child, Transform newParent)
        {
            if (child == null || newParent == null || child.parent == newParent) return;
            child.SetParent(newParent, false);
        }

        private Canvas FindCanvas()
        {
            foreach (var t in new[] { recordButton?.transform, saveButton?.transform, statusText?.transform })
                if (t != null && t.GetComponentInParent<Canvas>() is Canvas c) return c;
            var named = GameObject.Find("LibraryCanvas_WorldSpace");
            return named != null ? named.GetComponent<Canvas>() : FindFirstObjectByType<Canvas>();
        }

        // Capture is intentionally minimal (user-directed): the Capture
        // tab shows only Record / Stop & Save plus navigation. Naming,
        // instructions and preview panels were removed from this tab;
        // captures get automatic names and live in Library.

        // ---------- Server Settings (endpoint + API key, on-device only) ----------

        private ServerSettings ResolveServerSettings()
        {
            if (processor != null && processor.settings != null) return processor.settings;
            var found = FindFirstObjectByType<AutoProcessor>();
            if (found != null && found.settings != null) return found.settings;
            if (library != null && library.autoProcessor != null) return library.autoProcessor.settings;
            return null;
        }

        private void BuildServerSettingsUi(Transform root)
        {
            serverSettingsButton = ReMotionUiTheme.Button(root, "ServerSettingsButton", "⚙ Server",
                ReMotionUiTheme.ButtonStyle.Secondary, 14);
            Place(serverSettingsButton.transform, new Vector2(525f, 212f), new Vector2(130f, 44f));
            serverSettingsButton.onClick.AddListener(ShowServerSettings);

            var panel = ReMotionUiTheme.MakePanel(root, "ServerSettingsPanel", ReMotionUiTheme.Background);
            serverSettingsPanel = panel.gameObject;
            Place(serverSettingsPanel.transform, new Vector2(0f, -60f), new Vector2(760f, 400f));
            var layout = serverSettingsPanel.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true; layout.childControlHeight = false;
            layout.spacing = 10f; layout.padding = new RectOffset(22, 22, 18, 18);

            var title = ReMotionUiTheme.Label(serverSettingsPanel.transform, "ServerTitle",
                "SAM Server Settings", 20, ReMotionUiTheme.TextPrimary);
            ReMotionUiTheme.SetSize(title.transform, new Vector2(700f, 32f));
            var help = ReMotionUiTheme.Label(serverSettingsPanel.transform, "ServerHelp",
                "Enter the ReMotion gateway URL and your app key once — the\n" +
                "key authenticates this app to the gateway only. It is never\n" +
                "a RunPod key and is never sent to RunPod.", 14,
                ReMotionUiTheme.TextSecondary);
            ReMotionUiTheme.SetSize(help.transform, new Vector2(700f, 66f));

            var settings = ResolveServerSettings();
            endpointInput = BuildSettingsInput(serverSettingsPanel.transform, "EndpointInput",
                "https://gateway.example/v1", settings != null ? settings.GatewayUrl : "");
            apiKeyInput = BuildSettingsInput(serverSettingsPanel.transform, "ApiKeyInput",
                "Paste app (gateway) key…", "");
            apiKeyInput.contentType = InputField.ContentType.Password;

            var saveButton = ReMotionUiTheme.Button(serverSettingsPanel.transform, "ServerSaveButton",
                "Save on this headset", ReMotionUiTheme.ButtonStyle.Primary, 15);
            ReMotionUiTheme.SetSize(saveButton.transform, new Vector2(260f, 46f));
            saveButton.onClick.AddListener(SaveServerSettings);
            serverSettingsPanel.SetActive(false);
        }

        private InputField BuildSettingsInput(Transform parent, string name, string placeholder, string value)
        {
            var panel = ReMotionUiTheme.MakePanel(parent, name, ReMotionUiTheme.PanelAlt);
            ReMotionUiTheme.SetSize(panel.transform, new Vector2(700f, 46f));
            var text = ReMotionUiTheme.Label(panel.transform, "Text", value ?? "", 15,
                ReMotionUiTheme.TextPrimary);
            var textRt = text.GetComponent<RectTransform>();
            textRt.anchorMin = Vector2.zero; textRt.anchorMax = Vector2.one;
            textRt.offsetMin = new Vector2(12f, 4f); textRt.offsetMax = new Vector2(-12f, -4f);
            var hint = ReMotionUiTheme.Label(panel.transform, "Placeholder", placeholder, 14,
                ReMotionUiTheme.TextSecondary);
            var hintRt = hint.GetComponent<RectTransform>();
            hintRt.anchorMin = Vector2.zero; hintRt.anchorMax = Vector2.one;
            hintRt.offsetMin = new Vector2(12f, 4f); hintRt.offsetMax = new Vector2(-12f, -4f);
            var input = panel.gameObject.AddComponent<InputField>();
            input.textComponent = text;
            input.placeholder = hint;
            input.text = value ?? "";
            input.targetGraphic = panel;
            return input;
        }

        /// <summary>Back-compat alias: Server is a mode, not a toggle.</summary>
        public void ShowServerSettings() => ShowServer();

        public void SaveServerSettings()
        {
            var settings = ResolveServerSettings();
            if (settings == null) { SetStatus("Server processor not configured."); return; }
            settings.gatewayUrl = endpointInput != null ? endpointInput.text.Trim() : "";
            settings.remotionApiKey = apiKeyInput != null ? apiKeyInput.text.Trim() : "";
            settings.SaveRuntimeOverrides();
            // Saving stays in Server and reports there; the root is
            // never hidden from under the user.
            CurrentMode = AppMode.Server;
            ApplyModeVisibility();
            SetStatus(settings.HasGateway
                ? "Gateway saved on this headset."
                : "Server saved (no gateway — on-device preview only).");
            Debug.Log("[ReMotion] Server settings saved (gateway set: " + settings.HasGateway + ").");
        }



        private Button BuildTab(Transform root, string name, string label, Vector2 pos)
        {
            var button = ReMotionUiTheme.Button(root, name, label,
                ReMotionUiTheme.ButtonStyle.Secondary, 15);
            Place(button.transform, pos, new Vector2(165f, 44f));
            return button;
        }

        private static void Place(Transform t, Vector2 pos, Vector2 size)
        {
            var rt = t.GetComponent<RectTransform>();
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
        }

        // ---------- Wiring ----------

        // Per-reference wiring state. A single _wired flag was the device
        // bug: Bootstrap's AddComponent runs Awake (wiring with Record/Save
        // still null) before assigning those fields, and the flag then
        // blocked the real wiring forever — Record "clicked", never fired.
        private Button _wiredRecord, _wiredSave, _wiredCaptureTab, _wiredLibraryTab;
        private InputField _wiredTitleInput;
        private AutoProcessor _wiredProcessor;
        private LibraryManager _wiredLibrary;
        private ReplayHudController _wiredHud;
        private VideoPlaybackController _wiredVideoPlayer;
        private SessionRecorder _wiredRecorder;

        public void WireHandlers()
        {
            _wired = true;
            if (recordButton != null && _wiredRecord != recordButton)
            {
                if (_wiredRecord != null) _wiredRecord.onClick.RemoveListener(OnRecordClicked);
                recordButton.onClick.AddListener(OnRecordClicked);
                _wiredRecord = recordButton;
            }
            if (saveButton != null && _wiredSave != saveButton)
            {
                if (_wiredSave != null) _wiredSave.onClick.RemoveListener(OnSaveClicked);
                saveButton.onClick.AddListener(OnSaveClicked);
                _wiredSave = saveButton;
            }
            if (captureTabButton != null && _wiredCaptureTab != captureTabButton)
            {
                if (_wiredCaptureTab != null) _wiredCaptureTab.onClick.RemoveListener(ShowCapture);
                captureTabButton.onClick.AddListener(ShowCapture);
                _wiredCaptureTab = captureTabButton;
            }
            if (libraryTabButton != null && _wiredLibraryTab != libraryTabButton)
            {
                if (_wiredLibraryTab != null) _wiredLibraryTab.onClick.RemoveListener(ShowLibrary);
                libraryTabButton.onClick.AddListener(ShowLibrary);
                _wiredLibraryTab = libraryTabButton;
            }
            if (captureTitleInput != null && _wiredTitleInput != captureTitleInput)
            {
                if (_wiredTitleInput != null) _wiredTitleInput.onValueChanged.RemoveListener(OnTitleChanged);
                captureTitleInput.onValueChanged.AddListener(OnTitleChanged);
                _wiredTitleInput = captureTitleInput;
            }
            if (recorder != null && _wiredRecorder != recorder)
            {
                if (_wiredRecorder != null) _wiredRecorder.OnStateChanged -= HandleRecorderState;
                recorder.OnStateChanged += HandleRecorderState;
                _wiredRecorder = recorder;
            }
            if (processor != null && _wiredProcessor != processor)
            {
                if (_wiredProcessor != null)
                { _wiredProcessor.OnSceneReady -= HandleSceneReady; _wiredProcessor.OnFailed -= HandleFailed; }
                processor.OnSceneReady += HandleSceneReady;
                processor.OnFailed += HandleFailed;
                _wiredProcessor = processor;
            }
            if (library != null && _wiredLibrary != library)
            {
                if (_wiredLibrary != null)
                { _wiredLibrary.OnWatchRequested -= ShowVideo; _wiredLibrary.OnEnteredScene -= ShowReplay; }
                library.OnWatchRequested += ShowVideo;
                library.OnEnteredScene += ShowReplay;
                _wiredLibrary = library;
            }
            if (replayHud != null && _wiredHud != replayHud)
            {
                if (_wiredHud != null) _wiredHud.OnBack -= ShowLibrary;
                replayHud.OnBack += ShowLibrary;
                _wiredHud = replayHud;
            }
            if (videoPlayer != null && _wiredVideoPlayer != videoPlayer)
            {
                if (_wiredVideoPlayer != null) _wiredVideoPlayer.OnClosed -= HandleVideoClosed;
                videoPlayer.OnClosed += HandleVideoClosed;
                _wiredVideoPlayer = videoPlayer;
            }
            UpdateRecordButtons();
        }

        public void UnwireHandlers()
        {
            _wired = false;
            if (_wiredRecord != null) _wiredRecord.onClick.RemoveListener(OnRecordClicked);
            if (_wiredSave != null) _wiredSave.onClick.RemoveListener(OnSaveClicked);
            if (_wiredCaptureTab != null) _wiredCaptureTab.onClick.RemoveListener(ShowCapture);
            if (_wiredLibraryTab != null) _wiredLibraryTab.onClick.RemoveListener(ShowLibrary);
            if (_wiredTitleInput != null) _wiredTitleInput.onValueChanged.RemoveListener(OnTitleChanged);
            if (_wiredProcessor != null)
            { _wiredProcessor.OnSceneReady -= HandleSceneReady; _wiredProcessor.OnFailed -= HandleFailed; }
            if (_wiredLibrary != null)
            { _wiredLibrary.OnWatchRequested -= ShowVideo; _wiredLibrary.OnEnteredScene -= ShowReplay; }
            if (_wiredHud != null) _wiredHud.OnBack -= ShowLibrary;
            if (_wiredVideoPlayer != null) _wiredVideoPlayer.OnClosed -= HandleVideoClosed;
            if (_wiredRecorder != null) _wiredRecorder.OnStateChanged -= HandleRecorderState;
            _wiredRecord = _wiredSave = _wiredCaptureTab = _wiredLibraryTab = null;
            _wiredTitleInput = null; _wiredProcessor = null; _wiredLibrary = null;
            _wiredHud = null; _wiredVideoPlayer = null; _wiredRecorder = null;
        }

        private void OnTitleChanged(string value)
        { if (recorder != null) recorder.captureTitle = value; }

        private void HandleVideoClosed()
        { if (CurrentMode == AppMode.Video) ShowLibrary(); }

        /// <summary>
        /// Recording state: after Record, the whole menu goes away except
        /// Stop & Save (plus the status line). Nothing else can be pinched
        /// mid-action — the user's "virtual scene popped up" came from a
        /// live menu sitting behind their hands while recording.
        /// </summary>
        public bool IsRecordingUi { get; private set; }

        private void EnterRecordingUi()
        {
            IsRecordingUi = true;
            ReMotionXR.Accessibility.DwellClick.SuppressAll = true;
            // A replay scene left in the world must not sit behind the
            // user while they capture ("why does Record show a scene?").
            ReplayController.ExitAnyActiveScene();
            ApplyModeVisibility();
            if (videoPlayer != null && videoPlayer.IsOpen) videoPlayer.Close();
            if (replayHud != null) replayHud.Hide();
            if (saveButton != null)
            {
                saveButton.transform.SetAsLastSibling(); // the one control left
                var stopLabel = saveButton.GetComponentInChildren<Text>();
                if (stopLabel != null) stopLabel.text = "⏸  Stop Recording";
            }
            if (statusText != null) statusText.transform.SetAsLastSibling();
            UpdateRecordButtons();
        }

        private void ExitRecordingUi()
        {
            IsRecordingUi = false;
            ReMotionXR.Accessibility.DwellClick.SuppressAll = false;
            if (saveButton != null)
            {
                var label = saveButton.GetComponentInChildren<Text>();
                if (label != null) label.text = "■  Stop & Save";
            }
        }

        private static string Describe(Button b) =>
            b == null ? "<null>" : b.gameObject.name + " activeInHierarchy=" + b.gameObject.activeInHierarchy
                + " interactable=" + b.interactable + " enabled=" + b.enabled;

        public void OnRecordClicked()
        {
            Debug.Log("[ReMotion] Record handler: button=" + Describe(recordButton)
                      + " recorderState=" + (recorder != null ? recorder.State.ToString() : "<null>"));
            if (recorder == null) { SetStatus("Recorder not configured."); return; }
            if (recorder.State == SessionRecorder.CaptureState.Recording
                || recorder.State == SessionRecorder.CaptureState.Starting
                || recorder.State == SessionRecorder.CaptureState.RequestingPermission
                || recorder.State == SessionRecorder.CaptureState.Finalizing)
            {
                Debug.Log("[ReMotion] Record rejected: attempt already " + recorder.State);
                return;
            }
            if (captureTitleInput != null) recorder.captureTitle = captureTitleInput.text;
            recorder.StartRecording();
            // No status write here: StartRecording may already have
            // reached Failed/Recording synchronously. The state
            // renderer (via OnStateChanged) owns all capture feedback.
            RenderCaptureState();
        }

        /// <summary>
        /// THE capture state renderer: every recorder state maps to one
        /// status, one control set and one navigation lock. Called from
        /// state notifications and re-enable synchronization only.
        /// </summary>
        public void RenderCaptureState()
        {
            var state = recorder != null ? recorder.State : SessionRecorder.CaptureState.Idle;
            bool busy = IsRecorderBusy;
            if (state == SessionRecorder.CaptureState.Recording && !IsRecordingUi)
                EnterRecordingUi();
            if (state != SessionRecorder.CaptureState.Recording && IsRecordingUi)
                ExitRecordingUi();
            ApplyModeVisibility();
            UpdateRecordButtons();
            switch (state)
            {
                case SessionRecorder.CaptureState.Idle:
                    SetStatus("Capture: press Record, perform any action.");
                    break;
                case SessionRecorder.CaptureState.RequestingPermission:
                    SetStatus("Requesting camera permission… allow access to record.");
                    break;
                case SessionRecorder.CaptureState.Starting:
                    SetStatus("Starting camera…");
                    break;
                case SessionRecorder.CaptureState.Recording:
                    SetStatus("● Recording… perform the action, then pinch-and-release Stop.");
                    break;
                case SessionRecorder.CaptureState.Finalizing:
                    SetStatus("Saving… finalizing your video.");
                    break;
                case SessionRecorder.CaptureState.Saved:
                    SetStatus("Saved. Processing automatically — find it in Library.");
                    break;
                case SessionRecorder.CaptureState.Failed:
                    SetStatus("Capture failed: " + (recorder?.LastError ?? "unknown error"));
                    break;
            }
        }

        private bool IsRecorderBusy => recorder != null &&
            (recorder.State == SessionRecorder.CaptureState.RequestingPermission
             || recorder.State == SessionRecorder.CaptureState.Starting
             || recorder.State == SessionRecorder.CaptureState.Recording
             || recorder.State == SessionRecorder.CaptureState.Finalizing);

        /// <summary>Recorder state drives the capture UI (single authority).</summary>
        private void HandleRecorderState(SessionRecorder.CaptureState state)
        {
            RenderCaptureState();
            // Successful async finalization navigates exactly once.
            if (state == SessionRecorder.CaptureState.Saved && _navigatedForSave != recorder?.CurrentSessionId)
            {
                _navigatedForSave = recorder?.CurrentSessionId;
                ShowLibrary();
            }
        }

        private string _navigatedForSave;
        private int _lastElapsedShown = -1;

        private void Update()
        {
            // Advancing elapsed time while Recording (throttled to 1s).
            if (recorder == null || recorder.State != SessionRecorder.CaptureState.Recording) return;
            int elapsed = Mathf.FloorToInt((float)recorder.RecordingElapsedSeconds);
            if (elapsed == _lastElapsedShown) return;
            _lastElapsedShown = elapsed;
            SetStatus($"● Recording {elapsed / 60:00}:{elapsed % 60:00} — pinch-and-release Stop to finish.");
        }

        public void OnSaveClicked()
        {
            Debug.Log("[ReMotion] Stop handler: button=" + Describe(saveButton)
                      + " recorderState=" + (recorder != null ? recorder.State.ToString() : "<null>"));
            if (recorder == null) { SetStatus("Recorder not configured."); return; }
            if (recorder.State != SessionRecorder.CaptureState.Recording)
            {
                Debug.Log("[ReMotion] Stop rejected: not recording (" + recorder.State + ").");
                return;
            }
            // Finalization is asynchronous: the Saving state renders
            // now; Saved/Failed arrive through OnStateChanged and the
            // renderer navigates to Library exactly once on success.
            recorder.StopAndSaveAsync();
            RenderCaptureState();
        }

        /// <summary>Controls derive from the recorder state machine.</summary>
        private void UpdateRecordButtons()
        {
            var state = recorder != null ? recorder.State : SessionRecorder.CaptureState.Idle;
            bool busy = state == SessionRecorder.CaptureState.RequestingPermission
                        || state == SessionRecorder.CaptureState.Starting
                        || state == SessionRecorder.CaptureState.Recording
                        || state == SessionRecorder.CaptureState.Finalizing;
            if (recordButton != null)
            {
                recordButton.interactable = !busy;
                recordButton.gameObject.SetActive(!IsRecordingUi
                    && state != SessionRecorder.CaptureState.Recording);
            }
            if (saveButton != null)
            {
                // Stop exists only while a recording can be stopped.
                saveButton.gameObject.SetActive(
                    state == SessionRecorder.CaptureState.Recording || IsRecordingUi);
                saveButton.interactable = state == SessionRecorder.CaptureState.Recording;
            }
        }

        private void HandleSceneReady(string id)
        {
            // Async completion updates Library data only; it must never
            // reopen Library or overwrite another view's status.
            if (library != null) library.Refresh();
            if (CurrentMode == AppMode.Library)
                SetStatus("Scene READY in Library: " + id);
        }

        private void HandleFailed(string id, string error)
        {
            if (library != null) library.Refresh();
            if (CurrentMode == AppMode.Library)
                SetStatus("Processing failed: " + error);
            Debug.Log("[ReMotion] Processing failed for " + id + ": " + error);
        }

        /// <summary>Per-view status: writes only to the current view's text.</summary>
        private void SetStatus(string message)
        {
            var target = CurrentMode switch
            {
                AppMode.Server => serverStatusText ?? statusText,
                AppMode.Library => libraryStatusText ?? statusText,
                _ => statusText,
            };
            if (target != null) target.text = message;
            Debug.Log("[ReMotion] " + message);
        }

        /// <summary>
        /// Saved scenes created before this controller existed have no
        /// controller object. Find the recorder/library/buttons by their
        /// stable scene names and wire a controller dynamically.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Main-scene bootstrap only (diagnostic/other scenes excluded).
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main") return;
            // Include inactive objects: the saved scene's controller may be
            // on an inactive/disabled object at load. Missing it here spawned
            // a SECOND controller whose duplicate panels covered Record/Capture.
            if (FindFirstObjectByType<ReMotionMainUIController>(FindObjectsInactive.Include) != null) return;
            var recorder = FindFirstObjectByType<SessionRecorder>();
            if (recorder == null) return;
            var go = new GameObject("ReMotionMainUIController");
            var ctrl = go.AddComponent<ReMotionMainUIController>();
            ctrl.recorder = recorder;
            ctrl.processor = FindFirstObjectByType<AutoProcessor>();
            ctrl.library = FindFirstObjectByType<LibraryManager>();
            ctrl.replayController = FindFirstObjectByType<ReplayController>();
            ctrl.recordButton = FindButton("RecordButton");
            ctrl.saveButton = FindButton("SaveButton");
            var statusGo = GameObject.Find("StatusText");
            ctrl.statusText = statusGo != null ? statusGo.GetComponent<Text>() : null;
            ctrl.EnsureUi();
            ctrl.WireHandlers();
            ctrl.ShowCapture();
            Debug.Log("[ReMotion] Main UI controller bootstrapped for saved scene.");
        }

        private static Button FindButton(string objectName)
        {
            var go = GameObject.Find(objectName);
            return go != null ? go.GetComponent<Button>() : null;
        }
    }
}
