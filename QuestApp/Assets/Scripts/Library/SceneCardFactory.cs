using UnityEngine;
using UnityEngine.UI;

namespace ReMotionXR.Library
{
    /// <summary>Compact media tiles; management actions live behind More.</summary>
    public static class SceneCardFactory
    {
        public static readonly Vector2 TileSize = new Vector2(340f, 322f);

        public static readonly Vector2 PosterSize = new Vector2(216f, 190f);

        public static SceneCardUI CreatePosterCard(Transform parent)
        {
            var panel = ReMotionUiTheme.MakePanel(parent, "CaptureTile", ReMotionUiTheme.Panel);
            ReMotionUiTheme.SetSize(panel.transform, PosterSize);
            var card = panel.gameObject.AddComponent<SceneCardUI>();
            card.openDetailsButton = panel.gameObject.AddComponent<Button>();
            card.openDetailsButton.targetGraphic = panel;
            var colors = card.openDetailsButton.colors;
            colors.highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1);
            colors.pressedColor = new Color(.7f, .7f, .7f, 1);
            card.openDetailsButton.colors = colors;
            card.thumbnail = ReMotionUiTheme.MakePanel(panel.transform, "Thumbnail", ReMotionUiTheme.PanelAlt);
            Place(card.thumbnail.transform, 6, 6, 204, 115);
            card.thumbnail.raycastTarget = false;
            card.thumbnailLabel = ReMotionUiTheme.Label(card.thumbnail.transform, "ThumbnailLabel",
                "CAPTURE", 13, ReMotionUiTheme.TextPrimary, TextAnchor.MiddleCenter);
            Stretch(card.thumbnailLabel.transform);
            card.nameText = ReMotionUiTheme.Label(panel.transform, "NameText", "Capture", 16, ReMotionUiTheme.TextPrimary);
            Place(card.nameText.transform, 6, 126, 204, 36);
            card.statusText = ReMotionUiTheme.Label(panel.transform, "StatusText", "Processing", 12, ReMotionUiTheme.TextSecondary);
            Place(card.statusText.transform, 6, 168, 204, 16);
            return card;
        }

        public static SceneCardUI CreateCard(Transform parent)
        {
            var panel = ReMotionUiTheme.MakePanel(parent, "SceneCard", ReMotionUiTheme.Panel);
            panel.raycastTarget = false;
            var root = panel.gameObject;
            ReMotionUiTheme.SetSize(root.transform, TileSize);
            var card = root.AddComponent<SceneCardUI>();

            card.thumbnail = ReMotionUiTheme.MakePanel(root.transform, "Thumbnail", ReMotionUiTheme.PanelAlt);
            Place(card.thumbnail.transform, 12, 12, 316, 168);
            card.thumbnail.raycastTarget = false;
            card.thumbnailLabel = ReMotionUiTheme.Label(card.thumbnail.transform, "ThumbnailLabel",
                "REMOTION\nCAPTURE", 21, ReMotionUiTheme.TextSecondary, TextAnchor.MiddleCenter);
            Stretch(card.thumbnailLabel.transform);
            card.nameText = ReMotionUiTheme.Label(root.transform, "NameText", "Capture", 20,
                ReMotionUiTheme.TextPrimary);
            Place(card.nameText.transform, 12, 188, 316, 28);
            card.nameText.horizontalOverflow = HorizontalWrapMode.Wrap;

            card.statusText = ReMotionUiTheme.StatusPill(root.transform, "StatusPill", "Processing");
            Place(card.statusText.transform.parent, 12, 224, 146, 30);
            foreach (var graphic in card.statusText.transform.parent.GetComponentsInChildren<Graphic>())
                graphic.raycastTarget = false;
            card.durationText = ReMotionUiTheme.Label(root.transform, "DurationText", "0:00", 13,
                ReMotionUiTheme.TextSecondary, TextAnchor.MiddleRight);
            Place(card.durationText.transform, 160, 224, 72, 30);
            card.moreButton = ReMotionUiTheme.Button(root.transform, "MoreButton", "More",
                ReMotionUiTheme.ButtonStyle.Secondary, 13);
            Place(card.moreButton.transform, 240, 220, 88, 38);

            card.watchVideoButton = ReMotionUiTheme.Button(root.transform, "WatchButton", "Watch video",
                ReMotionUiTheme.ButtonStyle.Secondary, 16);
            Place(card.watchVideoButton.transform, 12, 266, 154, 44);
            card.enterSceneButton = ReMotionUiTheme.Button(root.transform, "EnterButton", "Enter scene",
                ReMotionUiTheme.ButtonStyle.Primary, 16);
            Place(card.enterSceneButton.transform, 174, 266, 154, 44);

            // An in-tile overlay replaces the poster, never expands into another tile.
            var details = ReMotionUiTheme.MakePanel(root.transform, "Details", ReMotionUiTheme.Background);
            Place(details.transform, 12, 12, 316, 168);
            card.detailsPanel = details.gameObject;
            card.retrainButton = Action(details.transform, "RetrainButton", "Retrain", 0);
            card.godModeButton = Action(details.transform, "GodModeButton", "God mode", 1);
            card.rebuildButton = Action(details.transform, "RebuildButton", "Rebuild scene", 2);
            card.renameButton = Action(details.transform, "RenameButton", "Rename", 3);
            card.deleteVirtualButton = Action(details.transform, "DeleteVirtualButton", "Delete scene", 4, true);
            card.deleteOriginalButton = Action(details.transform, "DeleteOriginalButton", "Delete video", 5, true);
            card.deleteEntireButton = Action(details.transform, "DeleteEntireButton", "Delete capture", 6, true);
            card.closeDetailsButton = Action(details.transform, "CloseDetailsButton", "Close", 7);
            details.gameObject.SetActive(false);
            return card;
        }

        private static Button Action(Transform parent, string name, string label, int index, bool danger = false)
        {
            var b = ReMotionUiTheme.Button(parent, name, label,
                danger ? ReMotionUiTheme.ButtonStyle.Danger : ReMotionUiTheme.ButtonStyle.Secondary, 13);
            Place(b.transform, 6 + (index % 2) * 155, 6 + (index / 2) * 40, 149, 36);
            return b;
        }

        private static void Place(Transform transform, float x, float y, float width, float height)
        {
            var rt = (RectTransform)transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(width, height);
        }

        private static void Stretch(Transform transform)
        {
            var rt = (RectTransform)transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }
    }
}
