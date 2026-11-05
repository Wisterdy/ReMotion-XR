using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using ReMotionXR.Library;
namespace ReMotionXR.Tests
{
    public class LibraryTileLayoutTests
    {
        [Test]
        public void HiddenLegacyContentBecomesVisibleWhenItsViewportIsShown()
        {
            var canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            var content = new GameObject("SceneList", typeof(RectTransform));
            content.transform.SetParent(canvas.transform, false);
            content.SetActive(false); // Capture mode hid the old, unwrapped content.
            var owner = new GameObject("library");
            try
            {
                var library = owner.AddComponent<LibraryManager>(); library.cardParent = content.transform;
                library.EnsureGridLayout();
                var viewport = content.transform.parent.gameObject;
                Assert.IsNotNull(viewport.GetComponent<ScrollRect>());
                Assert.IsFalse(viewport.activeSelf, "wrapping must not expose Library in Capture mode");
                Assert.IsTrue(content.activeSelf, "visibility ownership moved from content to viewport");
                viewport.SetActive(true);
                Assert.IsTrue(content.activeInHierarchy, "opening Library must reveal its tiles");
                viewport.SetActive(false);
                Assert.IsFalse(content.activeInHierarchy);
                viewport.SetActive(true);
                Assert.IsTrue(content.activeInHierarchy, "returning to Library must also work");
            }
            finally { Object.DestroyImmediate(canvas); Object.DestroyImmediate(owner); }
        }

        [Test]
        public void TileActionsStayInsideTheirCardAndDetailsCanClose()
        {
            var card = SceneCardFactory.CreateCard(null);
            try
            {
                card.SetDetailsVisible(true);
                var root = (RectTransform)card.transform;
                foreach (var button in card.GetComponentsInChildren<Button>(true))
                {
                    var corners = new Vector3[4]; ((RectTransform)button.transform).GetWorldCorners(corners);
                    foreach (var corner in corners)
                        Assert.IsTrue(root.rect.Contains(root.InverseTransformPoint(corner)), button.name);
                }
                card.SetDetailsVisible(false);
                Assert.IsFalse(card.detailsPanel.activeSelf);
                Assert.IsFalse(card.thumbnail.raycastTarget);
                foreach (var label in card.GetComponentsInChildren<Text>(true)) Assert.IsFalse(label.raycastTarget);
            }
            finally { Object.DestroyImmediate(card.gameObject); }
        }
    }
}
