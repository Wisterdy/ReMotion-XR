#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ReMotionXR.Library;
using ReMotionXR.Core;
namespace ReMotionXR.Editor
{
    public static class LibraryVisualCheck
    {
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var prefabCard = SceneCardFactory.CreateCard(null);
            // Runtime factory upgrades legacy cards; preserve authored prefab assets.
            Object.DestroyImmediate(prefabCard.gameObject);
            var camera = new GameObject("PreviewCamera").AddComponent<Camera>();
            camera.orthographic = true; camera.orthographicSize = 400;
            camera.transform.position = new Vector3(0, 0, -1000);
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = ReMotionUiTheme.Background;
            var canvas = new GameObject("LibraryPreview", typeof(RectTransform), typeof(Canvas)).GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = camera;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(760,800);
            var heading = ReMotionUiTheme.Label(canvas.transform, "Heading", "Your library", 30, ReMotionUiTheme.TextPrimary);
            ReMotionUiTheme.SetSize(heading.transform, new Vector2(700,45));
            ((RectTransform)heading.transform).anchoredPosition = new Vector2(0,350);
            var library = canvas.gameObject.AddComponent<LibraryManager>();
            library.LibraryRootOverride = Path.Combine(Path.GetTempPath(), "remotion-library-preview");
            Directory.CreateDirectory(library.LibraryRootOverride);
            string[] titles = { "Capture · Nov 3, 19:28", "Desk setup", "Morning practice", "Training session" };
            for (int i=0; i<4; i++)
            {
                var card = SceneCardFactory.CreateCard(canvas.transform);
                var rt = (RectTransform)card.transform;
                rt.anchoredPosition = new Vector2(i%2 == 0 ? -178 : 178, i<2 ? 140 : -198);
                card.Bind(new SessionMetadata {sessionId="preview"+i, displayName=titles[i], durationSeconds=9+i*17,
                    status=i==0?"Processing":"Ready", samStatus=i==0?"Processing":"Ready"}, library);
                card.watchVideoButton.interactable = true;
                card.enterSceneButton.interactable = i != 0;
                if (i == 3) card.SetDetailsVisible(true);
            }
            Canvas.ForceUpdateCanvases();
            var target = new RenderTexture(1140,1200,24);
            camera.targetTexture = target; camera.Render();
            RenderTexture.active = target;
            var image = new Texture2D(1140,1200,TextureFormat.RGB24,false);
            image.ReadPixels(new Rect(0,0,1140,1200),0,0); image.Apply();
            File.WriteAllBytes("../docs/evidence/library-grid-preview.png",image.EncodeToPNG());
            RenderTexture.active = null; camera.targetTexture = null;
            Object.DestroyImmediate(image); Object.DestroyImmediate(target);
            Debug.Log("[ReMotion] Library preview rendered; SceneCard prefab upgraded without touching Main or input rig.");
        }
    }
}
#endif
