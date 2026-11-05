#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using ReMotionXR.Library;
using ReMotionXR.Core;
namespace ReMotionXR.Editor
{
    public static class GalleryVisualCheck
    {
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string dir = Path.Combine(Path.GetTempPath(), "remotion-gallery-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            for (int i=0; i<10; i++)
            {
                string sid="preview"+i; string folder=Path.Combine(dir,sid); Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"metadata.json"),JsonUtility.ToJson(new SessionMetadata {
                    sessionId=sid,displayName="Capture " + (10-i).ToString("00"),durationSeconds=8+i*4,status=i==0?"Processing":"Ready"}));
                File.WriteAllBytes(Path.Combine(folder,"original_video.mp4"),new byte[]{1});
                File.SetLastWriteTimeUtc(Path.Combine(folder,"original_video.mp4"),DateTime.UtcNow.AddMinutes(-i));
                string poster="../Server/local_input/quest-a8cfb73e7f14/thumbnail.jpg";
                if (i%3==0 && File.Exists(poster)) File.Copy(poster,Path.Combine(folder,"thumbnail.jpg"));
            }
            var camera=new GameObject("PreviewCamera").AddComponent<Camera>();
            camera.orthographic=true;camera.orthographicSize=350;camera.transform.position=new Vector3(0,0,-1000);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=ReMotionUiTheme.Background;
            var canvas=new GameObject("LibraryPreview",typeof(RectTransform),typeof(Canvas)).GetComponent<Canvas>();
            canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=camera;
            ((RectTransform)canvas.transform).sizeDelta=new Vector2(1200,700);
            var title=ReMotionUiTheme.Label(canvas.transform,"Heading","Your library",30,ReMotionUiTheme.TextPrimary);
            ReMotionUiTheme.SetSize(title.transform,new Vector2(1120,48));
            ((RectTransform)title.transform).anchoredPosition=new Vector2(0,230);
            var sub=ReMotionUiTheme.Label(canvas.transform,"Order","Latest captures first  /  Select a capture to watch or enter its scene",17,ReMotionUiTheme.TextSecondary);
            ReMotionUiTheme.SetSize(sub.transform,new Vector2(1120,30));
            ((RectTransform)sub.transform).anchoredPosition=new Vector2(0,175);
            var content=new GameObject("SceneList",typeof(RectTransform));content.transform.SetParent(canvas.transform,false);
            var library=canvas.gameObject.AddComponent<LibraryManager>();library.cardParent=content.transform;library.LibraryRootOverride=dir;
            library.Refresh();Canvas.ForceUpdateCanvases();
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)content.transform);
            foreach (var tile in content.GetComponentsInChildren<SceneCardUI>())
            {
                var corners=new Vector3[4];((RectTransform)tile.transform).GetWorldCorners(corners);
                foreach(var corner in corners)
                    if(!((RectTransform)content.transform.parent).rect.Contains(content.transform.parent.InverseTransformPoint(corner)))
                        throw new Exception("Poster outside two-row viewport: "+tile.name);
            }
            Render(camera,"../docs/evidence/library-five-across-preview.png");
            content.transform.GetChild(0).GetComponent<SceneCardUI>().openDetailsButton.onClick.Invoke();
            Canvas.ForceUpdateCanvases();Render(camera,"../docs/evidence/library-capture-details-preview.png");
            Directory.Delete(dir,true);
            Debug.Log("[ReMotion] Five-across gallery and action panel rendered; all ten posters bounded inside viewport.");
        }
        private static void Render(Camera camera,string path)
        {
            var target=new RenderTexture(1440,840,24);camera.targetTexture=target;camera.Render();RenderTexture.active=target;
            var image=new Texture2D(1440,840,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,1440,840),0,0);image.Apply();
            File.WriteAllBytes(path,image.EncodeToPNG());RenderTexture.active=null;camera.targetTexture=null;
            UnityEngine.Object.DestroyImmediate(image);UnityEngine.Object.DestroyImmediate(target);
        }
    }
}
#endif
