using UnityEngine;
using UnityEngine.UI;
using ReMotionXR.Core;

namespace ReMotionXR.Library
{
    /// <summary>
    /// One Library card: thumbnail, name, duration, status and actions.
    /// Delete buttons route through LibraryManager's confirmation dialog
    /// (Cancel / Delete, "This cannot be undone."). If the original video
    /// is deleted, Watch Original Video disables. If the virtual scene is
    /// deleted, Enter Virtual Scene disables and Rebuild appears.
    /// </summary>
    public class SceneCardUI : MonoBehaviour
    {
        public Button openDetailsButton;
        public GameObject detailsPanel;
        public Button moreButton;
        public Button closeDetailsButton;
        private Texture2D _posterTexture;
        private Sprite _posterSprite;
        public void SetDetailsVisible(bool visible)
        {
            if (detailsPanel != null) detailsPanel.SetActive(visible);
            if (thumbnail != null) thumbnail.gameObject.SetActive(!visible);
        }
        private void OnDisable() => SetDetailsVisible(false);
        private void OnDestroy() => ClearPoster();
        private void ClearPoster()
        {
            if (thumbnail != null) thumbnail.sprite = null;
            if (_posterSprite != null)
            {
                if (Application.isPlaying) Destroy(_posterSprite); else DestroyImmediate(_posterSprite);
            }
            if (_posterTexture != null)
            {
                if (Application.isPlaying) Destroy(_posterTexture); else DestroyImmediate(_posterTexture);
            }
            _posterSprite = null;
            _posterTexture = null;
        }
        public Text nameText;
        public Text durationText;
        public Text statusText;
        public Image thumbnail;
        public Text thumbnailLabel;
        public Button watchVideoButton;
        public Button enterSceneButton;
        public Button retrainButton;
        public Button godModeButton;
        public Button rebuildButton;
        public Button renameButton;
        [Header("Delete")]
        public Button deleteVirtualButton;
        public Button deleteOriginalButton;
        public Button deleteEntireButton;

        private SessionMetadata _meta;
        private LibraryManager _library;

        public void Bind(SessionMetadata meta, LibraryManager library)
        {
            _meta = meta;
            _library = library;
            ClearPoster();
            bool hasOriginal = !meta.originalVideoInvalid && library.HasOriginalVideo(meta.sessionId);
            bool hasVirtual = library.HasVirtualScene(meta.sessionId);
            if (thumbnailLabel != null)
            {
                thumbnailLabel.gameObject.SetActive(true);
                thumbnailLabel.text = hasOriginal ? "CAPTURE" : hasVirtual ? "VIRTUAL SCENE" : "VIDEO DELETED";
            }
            Wire(openDetailsButton, () => library.ShowCaptureDetails(meta));
            if (nameText != null) nameText.text = meta.displayName;
            if (durationText != null)
                durationText.text = System.TimeSpan.FromSeconds(Mathf.Max(0, meta.durationSeconds)).ToString(@"m\:ss");
            SetDetailsVisible(false);
            Wire(moreButton, () => SetDetailsVisible(detailsPanel != null && !detailsPanel.activeSelf));
            Wire(closeDetailsButton, () => SetDetailsVisible(false));
            if (statusText != null)
            {
                // SAM indicator: visible only while the pod job runs (or is
                // still pending); removed once the SAM scene is Ready.
                if (meta.originalVideoInvalid)
                    statusText.text = "Recording incomplete";
                else if (meta.samStatus == "Processing")
                    statusText.text = $"SAM {meta.samProgress}%";
                else if (meta.samStatus == "Unavailable")
                    statusText.text = "SAM pending";
                else
                    statusText.text = meta.status;
                statusText.color = ReMotionUiTheme.ColorForStatus(meta.status);
                var dot = statusText.transform.parent != null
                    ? statusText.transform.parent.Find("Dot") : null;
                if (dot != null && dot.TryGetComponent<Image>(out var dotImage))
                    dotImage.color = meta.samStatus == "Processing"
                        ? ReMotionUiTheme.Warning
                        : ReMotionUiTheme.ColorForStatus(meta.status);
            }

            // Poster tint varies per capture (grid tiles must be tellable apart).
            if (thumbnail != null)
            {
                var palette = new[]
                {
                    new Color(0.5f, 0.26f, 0.07f, 1f), new Color(0.13f, 0.28f, 0.45f, 1f),
                    new Color(0.16f, 0.38f, 0.22f, 1f), new Color(0.38f, 0.18f, 0.42f, 1f),
                    new Color(0.45f, 0.32f, 0.1f, 1f),
                };
                int pick = meta.sessionId != null
                    ? (int)((uint)meta.sessionId.GetHashCode() % (uint)palette.Length) : 0;
                thumbnail.color = palette[pick];
            }

            string poster = System.IO.Path.Combine(library.LibraryRoot, meta.sessionId, "thumbnail.jpg");
            if (hasOriginal && thumbnail != null && System.IO.File.Exists(poster))
            {
                try
                {
                    _posterTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
                    if (_posterTexture.LoadImage(System.IO.File.ReadAllBytes(poster)))
                    {
                        _posterSprite = Sprite.Create(_posterTexture,
                            new Rect(0, 0, _posterTexture.width, _posterTexture.height), new Vector2(.5f, .5f));
                        thumbnail.sprite = _posterSprite;
                        thumbnail.type = Image.Type.Simple;
                        thumbnail.preserveAspect = true;
                        thumbnail.color = Color.white;
                        if (thumbnailLabel != null) thumbnailLabel.gameObject.SetActive(false);
                    }
                }
                catch (System.Exception e) { Debug.LogWarning("[ReMotion] Thumbnail unavailable: " + e.Message); }
            }

            // Immersive (scene/retrain/god) unlocks only with a SAM model.
            bool immersive = hasVirtual && meta.SamReady;
            SetInteractable(watchVideoButton, hasOriginal);
            SetInteractable(enterSceneButton, immersive);
            SetInteractable(retrainButton, immersive);
            SetInteractable(godModeButton, immersive);
            SetInteractable(rebuildButton, !hasVirtual && hasOriginal);

            Wire(enterSceneButton, () => library.EnterScene(meta.sessionId));
            Wire(watchVideoButton, () => library.WatchVideo(meta.sessionId));
            Wire(retrainButton, () => library.EnterRetrain(meta.sessionId));
            Wire(godModeButton, () => library.EnterGodMode(meta.sessionId));
            Wire(rebuildButton, () => library.RebuildScene(meta.sessionId));
            Wire(deleteVirtualButton, () => library.RequestDeleteVirtualScene(meta.sessionId));
            Wire(deleteOriginalButton, () => library.RequestDeleteOriginalVideo(meta.sessionId));
            Wire(deleteEntireButton, () => library.RequestDeleteEntireScene(meta.sessionId));
            Wire(renameButton, () => library.RenameSession(meta.sessionId, meta.displayName));
        }

        private static void Wire(Button b, UnityEngine.Events.UnityAction a)
        {
            if (b == null) return;
            b.onClick.RemoveAllListeners();
            b.onClick.AddListener(a);
        }

        private static void SetInteractable(Button b, bool on)
        {
            if (b != null) b.interactable = on;
        }

        // Named handlers for persistent (serialized) button bindings.
        public void OnWatchOriginalVideo() { if (_library != null) _library.WatchVideo(_meta.sessionId); }
        public void OnEnterVirtualScene() { if (_library != null) _library.EnterScene(_meta.sessionId); }
        public void OnRetrain() { if (_library != null) _library.EnterRetrain(_meta.sessionId); }
        public void OnGodMode() { if (_library != null) _library.EnterGodMode(_meta.sessionId); }
        public void OnRebuild() { if (_library != null) _library.RebuildScene(_meta.sessionId); }
    }
}
