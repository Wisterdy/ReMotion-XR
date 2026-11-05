using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using ReMotionXR.Core;
using ReMotionXR.Library;

namespace ReMotionXR.Tests
{
    public class DeletedVideoThumbnailTests
    {
        private string root, folder;
        private GameObject owner, parent;
        private LibraryManager library;
        private SessionMetadata metadata;

        [SetUp] public void SetUp()
        {
            root=Path.Combine(Path.GetTempPath(), "remotion-thumbnail-test-"+Guid.NewGuid());
            folder=Path.Combine(root, "capture"); Directory.CreateDirectory(folder);
            owner=new GameObject("library"); parent=new GameObject("cards");
            library=owner.AddComponent<LibraryManager>();
            library.LibraryRootOverride=root; library.cardParent=parent.transform;
            metadata=new SessionMetadata { sessionId="capture", displayName="Capture", status="Ready", samStatus="Ready" };
            File.WriteAllText(Path.Combine(folder,"metadata.json"),JsonUtility.ToJson(metadata));
            File.WriteAllBytes(Path.Combine(folder,"original_video.mp4"),new byte[]{1});
            var texture=new Texture2D(2,2);
            File.WriteAllBytes(Path.Combine(folder,"thumbnail.jpg"),texture.EncodeToJPG());
            UnityEngine.Object.DestroyImmediate(texture);
        }

        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(parent); UnityEngine.Object.DestroyImmediate(owner);
            Directory.Delete(root,true);
        }

        [Test] public void DeleteOriginalRemovesPosterAndPreservesVirtualSceneAndTimeline()
        {
            File.WriteAllText(Path.Combine(folder,"scene.remotion"),"keep scene");
            File.WriteAllText(Path.Combine(folder,"timeline.json"),"keep poses");
            library.DeleteOriginalVideo("capture");
            Assert.IsFalse(File.Exists(Path.Combine(folder,"original_video.mp4")));
            Assert.IsFalse(File.Exists(Path.Combine(folder,"thumbnail.jpg")));
            Assert.AreEqual("keep scene",File.ReadAllText(Path.Combine(folder,"scene.remotion")));
            Assert.AreEqual("keep poses",File.ReadAllText(Path.Combine(folder,"timeline.json")));
            var card=parent.GetComponentInChildren<SceneCardUI>();
            Assert.IsNull(card.thumbnail.sprite);
            Assert.AreEqual("VIRTUAL SCENE",card.thumbnailLabel.text);
        }

        [TestCase(true)] [TestCase(false)]
        public void RebindingCannotDisplayLegacyOrCachedPosterWithoutOriginal(bool keepVirtual)
        {
            if(keepVirtual) File.WriteAllText(Path.Combine(folder,"scene.remotion"),"keep scene");
            var card=SceneCardFactory.CreateCard(parent.transform);
            card.Bind(metadata,library);
            Assert.IsNotNull(card.thumbnail.sprite);
            File.Delete(Path.Combine(folder,"original_video.mp4")); // reproduces an older deletion leaving thumbnail.jpg
            card.Bind(metadata,library);
            Assert.IsNull(card.thumbnail.sprite);
            Assert.IsTrue(card.thumbnailLabel.gameObject.activeSelf);
            Assert.AreEqual(keepVirtual ? "VIRTUAL SCENE" : "VIDEO DELETED",card.thumbnailLabel.text);
            Assert.IsFalse(card.watchVideoButton.interactable);
            Assert.AreEqual(keepVirtual,card.enterSceneButton.interactable);
        }
    }
}
