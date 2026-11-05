#if UNITY_EDITOR
using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEngine;

namespace ReMotionXR.Editor
{
    /// <summary>
    /// Quest build requirements that must land in the generated Android
    /// manifest and project config, per Meta's Passthrough Camera API and
    /// hand-tracking docs:
    /// - horizonos.permission.HEADSET_CAMERA (camera capture won't start
    ///   without it; Meta's preprocessor only adds it when the project
    ///   config flag is on, and users report it getting dropped, so the
    ///   generated manifest is patched here directly at build time)
    /// - com.oculus.permission.HAND_TRACKING for hand/pinch input
    /// - OculusProjectConfig: passthrough + camera access enabled
    /// Menu ReMotion XR / Apply Quest Build Settings does the config half.
    /// </summary>
    public class ReMotionAndroidBuildSetup : IPostGenerateGradleAndroidProject
    {
        // Must exceed Meta OVRGradleGeneration's 99999, which copies
        // Meta's deny-all network_sec_config.xml onto the same path and
        // silently clobbers any lower-order edit.
        public int callbackOrder => 100000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifestPath = Path.Combine(path, "src/main/AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning("[ReMotion] No generated AndroidManifest.xml to patch: " + manifestPath);
                return;
            }
            var doc = new XmlDocument();
            doc.Load(manifestPath);
            var manifest = doc.DocumentElement;
            const string androidNs = "http://schemas.android.com/apk/res/android";
            AddPermission(doc, manifest, androidNs, "horizonos.permission.HEADSET_CAMERA");
            AddPermission(doc, manifest, androidNs, "com.oculus.permission.HAND_TRACKING");
            doc.Save(manifestPath);
            Debug.Log("[ReMotion] Android manifest patched: HEADSET_CAMERA + HAND_TRACKING permissions.");

            // Unity generates network_sec_config.xml with cleartext off;
            // the local PC server is plain HTTP on the LAN, so permit
            // cleartext there (RunPod traffic stays HTTPS).
            foreach (string cfg in Directory.GetFiles(path, "network_sec_config.xml",
                                                      SearchOption.AllDirectories))
            {
                string xml = File.ReadAllText(cfg);
                string patched = xml.Replace("cleartextTrafficPermitted=\"false\"",
                                             "cleartextTrafficPermitted=\"true\"");
                if (patched != xml)
                {
                    File.WriteAllText(cfg, patched);
                    Debug.Log("[ReMotion] network_sec_config: cleartext permitted for LAN server: " + cfg);
                }
            }
        }

        private static void AddPermission(XmlDocument doc, XmlElement manifest,
                                          string androidNs, string permission)
        {
            foreach (XmlNode node in manifest.ChildNodes)
                if (node.Name == "uses-permission" &&
                    node.Attributes?["android:name"]?.Value == permission)
                    return;
            var element = doc.CreateElement("uses-permission");
            var attr = doc.CreateAttribute("android", "name", androidNs);
            attr.Value = permission;
            element.Attributes.Append(attr);
            manifest.AppendChild(element);
        }

        [MenuItem("ReMotion XR/Apply Quest Build Settings")]
        public static void ApplyQuestBuildSettings()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("Assets/Oculus/OculusProjectConfig.asset");
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[ReMotion] OculusProjectConfig.asset not found.");
                return;
            }
            var so = new SerializedObject(assets[0]);
            // insightPassthroughEnabled is obsolete (auto-reset by Meta);
            // the live flag is _insightPassthroughSupport (2 = Required).
            var support = so.FindProperty("_insightPassthroughSupport");
            if (support != null) support.intValue = 2;
            SetBool(so, "isPassthroughCameraAccessEnabled", true);
            so.ApplyModifiedPropertiesWithoutUndo();
            // The local PC server is plain HTTP on the LAN; without this
            // UnityWebRequest refuses cleartext and SAM uploads never
            // leave the headset (RunPod stays HTTPS either way).
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            EnsureRuntimeShadersIncluded();
            AssetDatabase.SaveAssets();
            Debug.Log("[ReMotion] Quest build settings: passthrough + camera access enabled.");
        }

        /// <summary>
        /// Runtime scene materials are created from Shader.Find at runtime;
        /// without an always-included entry the shader is stripped and the
        /// replay floor/ghosts render magenta on Quest. Pin the shaders
        /// SceneMaterials can resolve to into every build.
        /// </summary>
        public static void EnsureRuntimeShadersIncluded()
        {
            var graphicsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (graphicsAssets == null || graphicsAssets.Length == 0) return;
            var graphics = new SerializedObject(graphicsAssets[0]);
            var included = graphics.FindProperty("m_AlwaysIncludedShaders");
            if (included == null) return;
            foreach (string shaderName in new[] { "Standard", "Unlit/Color", "Sprites/Default" })
            {
                var shader = Shader.Find(shaderName);
                if (shader == null) continue;
                bool present = false;
                for (int i = 0; i < included.arraySize; i++)
                    if (included.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    { present = true; break; }
                if (present) continue;
                included.InsertArrayElementAtIndex(included.arraySize);
                included.GetArrayElementAtIndex(included.arraySize - 1).objectReferenceValue = shader;
            }
            graphics.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetBool(SerializedObject so, string field, bool value)
        {
            var prop = so.FindProperty(field);
            if (prop != null) prop.boolValue = value;
            else Debug.LogWarning("[ReMotion] OculusProjectConfig field not found: " + field);
        }
    }
}
#endif
