using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using ReMotionXR.Core;
using ReMotionXR.Library;

namespace ReMotionXR.Tests
{
    public class SavedCaptureVisibilityTests
    {
        [TestCase("Processing")]
        [TestCase("Failed")]
        public void RecordedVideoVisibleAndWatchableEvenWithLegacyDemoMode(string status)
        {
            string root = Path.Combine(Path.GetTempPath(), "remotion-capture-" + Guid.NewGuid());
            string folder = Path.Combine(root, "capture"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "original_video.mp4"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(folder, "metadata.json"), JsonUtility.ToJson(
                new SessionMetadata { sessionId = "capture", displayName = "My capture", status = status,
                                      samStatus = status }));
            var owner = new GameObject("library"); var parent = new GameObject("cards");
            try
            {
                var library = owner.AddComponent<LibraryManager>();
                library.LibraryRootOverride = root; library.cardParent = parent.transform;
                library.demoMode = true; // reproduces installed Main's previous serialized value
                Assert.AreEqual(1, library.Refresh());
                var tile = parent.GetComponentInChildren<SceneCardUI>();
                tile.openDetailsButton.onClick.Invoke();
                var card = library.ActiveDetailsCard;
                Assert.IsTrue(card.watchVideoButton.interactable);
                Assert.IsFalse(card.enterSceneButton.interactable);
                Assert.IsTrue(card.rebuildButton.gameObject.activeSelf);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent); UnityEngine.Object.DestroyImmediate(owner);
                Directory.Delete(root, true);
            }
        }
    }
}
