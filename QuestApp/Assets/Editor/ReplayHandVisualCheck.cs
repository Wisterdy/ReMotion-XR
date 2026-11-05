using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using ReMotionXR.Core;
using ReMotionXR.Replay;

namespace ReMotionXR.Editor
{
    public static class ReplayHandVisualCheck
    {
        private static bool buildAfterPreview;
        public static void InspectAndBuild() { buildAfterPreview=true; Inspect(); }
        public static void Inspect()
        {
            string evidence=Path.GetFullPath(Path.Combine(Application.dataPath,"../../docs/evidence"));
            var report=new StringBuilder();
            EditorSceneManager.OpenScene("Assets/Scenes/Main.unity");
            foreach(var r in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include,FindObjectsSortMode.None))
                report.AppendLine($"LIVE {r.name}: mesh={r.sharedMesh?.name} vertices={r.sharedMesh?.vertexCount} path={AssetDatabase.GetAssetPath(r.sharedMesh)} material={AssetDatabase.GetAssetPath(r.sharedMaterial)} shader={r.sharedMaterial?.shader.name}");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var lightGo=new GameObject("Preview directional light");
            lightGo.AddComponent<Light>().type=LightType.Directional;
            lightGo.transform.rotation=Quaternion.Euler(50f,-30f,0f);
            ScenePackage.TryParseTimeline(File.ReadAllBytes(Path.Combine(evidence,"visual-fix-scene.remotion")),out var timeline,out _);
            var frame=timeline.frames[timeline.frames.Count/2];
            foreach(string side in new[]{"Left","Right"})
            {
                var prefab=Resources.Load<GameObject>("Hands/OpenXR"+side+"Hand");
                var hand=Object.Instantiate(prefab);
                foreach(var r in hand.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    report.AppendLine($"REPLAY {side}: mesh={r.sharedMesh.name} vertices={r.sharedMesh.vertexCount} bones={r.bones.Length} root={r.rootBone?.name} shader={r.sharedMaterial?.shader.name}");
                    foreach(var b in r.bones) report.AppendLine($" bone {b.name} p={b.position:F4} q={b.rotation:F4} scale={b.lossyScale:F4}");
                }
                var frameRoot=new GameObject("CaptureRoot");
                hand.transform.SetParent(frameRoot.transform,false);
                var driver=hand.AddComponent<HandModelDriver>(); driver.Bind(hand);
                var joints=side=="Left"?frame.leftHandJoints:frame.rightHandJoints;
                var rotations=side=="Left"?frame.leftHandRotations:frame.rightHandRotations;
                report.AppendLine($"FRAME {side}: joints={joints.Length} rotations={rotations.Length} mapped={driver.BoneCount}");
                // Cancel the new conversion for the controlled old-render comparison.
                var oldInput=new Quaternion[rotations.Length];
                for(int i=0;i<oldInput.Length;++i) oldInput[i]=rotations[i]*Quaternion.Euler(0,180,0);
                driver.Pose(joints,oldInput,frameRoot.transform);
                foreach(var r in hand.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    var baked=new Mesh(); r.BakeMesh(baked); report.AppendLine($"BAKED {side}: bounds={baked.bounds} rendererScale={r.transform.lossyScale}"); Object.DestroyImmediate(baked);
                    SceneMaterials.ApplyColor(r,new Color(.35f,.55f,1));
                }
                Render(hand,Path.Combine(evidence,"hand-before-"+side+".png"));
                var source=AssetDatabase.LoadAssetAtPath<Material>("Packages/com.meta.xr.sdk.core/Editor/BuildingBlocks/BlockData/HandTracking/Materials/DefaultHandMaterial.mat");
                var material=new Material(source);
                foreach(var r in hand.GetComponentsInChildren<SkinnedMeshRenderer>()) r.sharedMaterial=material;
                Render(hand,Path.Combine(evidence,"hand-meta-"+side+".png"));
                driver.Pose(joints,rotations,frameRoot.transform);
                Render(hand,Path.Combine(evidence,"hand-basis-"+side+".png"));
                Object.DestroyImmediate(frameRoot); Object.DestroyImmediate(material);
            }
            File.WriteAllText(Path.Combine(evidence,"hand-visual-inspection.txt"),report.ToString());
            if(buildAfterPreview) ReMotionBuild.BuildApkBatchmode();
            else EditorApplication.Exit(0);
        }

        private static void Render(GameObject hand,string path)
        {
            var renderers=hand.GetComponentsInChildren<SkinnedMeshRenderer>();
            var bounds=new Bounds(); bool first=true;
            var meshes=new System.Collections.Generic.List<Mesh>(); var staticRoot=new GameObject("BakedHandPreview");
            foreach(var r in renderers) {
                var baked=new Mesh(); r.BakeMesh(baked,true); baked.RecalculateBounds(); meshes.Add(baked);
                var g=new GameObject("BakedSurface"); g.transform.SetParent(staticRoot.transform,false);
                g.transform.localScale=Vector3.one*100f;
                g.AddComponent<MeshFilter>().sharedMesh=baked; g.AddComponent<MeshRenderer>().sharedMaterial=r.sharedMaterial;
                var scaledBounds=new Bounds(baked.bounds.center*100f,baked.bounds.size*100f);
                if(first){bounds=scaledBounds;first=false;}else bounds.Encapsulate(scaledBounds);
                r.enabled=false;
            }
            Debug.Log($"Hand preview {path} bounds={bounds}");
            var go=new GameObject("PreviewCamera"); var c=go.AddComponent<Camera>();
            c.clearFlags=CameraClearFlags.SolidColor;c.backgroundColor=new Color(.08f,.09f,.12f); c.nearClipPlane=.005f;c.farClipPlane=10;c.fieldOfView=42;
            c.transform.position=bounds.center+new Vector3(.12f,.18f,-.28f).normalized*Mathf.Max(.3f,bounds.size.magnitude*1.25f);
            c.transform.LookAt(bounds.center);var rt=new RenderTexture(960,960,24);c.targetTexture=rt;c.Render();
            var old=RenderTexture.active;RenderTexture.active=rt;var image=new Texture2D(960,960,TextureFormat.RGB24,false);image.ReadPixels(new Rect(0,0,960,960),0,0);image.Apply();RenderTexture.active=old;
            File.WriteAllBytes(path,image.EncodeToPNG());c.targetTexture=null;Object.DestroyImmediate(image);Object.DestroyImmediate(rt);Object.DestroyImmediate(go);
            Object.DestroyImmediate(staticRoot);foreach(var mesh in meshes)Object.DestroyImmediate(mesh);foreach(var r in renderers)r.enabled=true;
        }
    }
}
