#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ReMotionXR.Editor
{
    /// <summary>
    /// Controlled comparison for the native-pinch investigation. The
    /// diagnostic scene is a byte copy of the installed SDK's
    /// RayExamples sample scene (complete official hand-ray rig), plus
    /// one observer-only PinchDiagnosticLogger and an in-scene banner.
    ///
    /// ISOLATION CONTRACT (do not weaken):
    /// - Builds a SEPARATE application: package
    ///   com.remotion.xr.pinchdiagnostic, product name
    ///   "ReMotion Pinch Diagnostic". It never replaces, updates or
    ///   shares data with com.remotion.xr.
    /// - Builds ONLY the diagnostic scene.
    /// - Every shared PlayerSettings value it touches is captured first
    ///   and restored in a finally block, success or failure.
    /// - A failed build exits nonzero in batchmode and never leaves a
    ///   stale APK to be mistaken for this build.
    /// - Prints (and writes next to the APK) a build manifest:
    ///   applicationId, scene0, versions, absolute APK path, SHA256.
    /// Main is never modified by this setup.
    /// </summary>
    public static class ReMotionPinchDiagnosticSetup
    {
        public const string DiagnosticApplicationId = "com.remotion.xr.pinchdiagnostic";
        public const string DiagnosticProductName = "ReMotion Pinch Diagnostic";
        private const string DiagnosticScene = "Assets/Scenes/PinchDiagnostic.unity";
        private const string MainScene = "Assets/Scenes/Main.unity";
        private const string DiagnosticApk = "Builds/PinchDiagnostic.apk";
        private const string DiagnosticVersionName = "1.0-diag";
        private const int DiagnosticVersionCode = 1;

        [MenuItem("ReMotion XR/Dump Pinch Diagnostic Hierarchy")]
        public static void DumpDiagnosticHierarchy()
        {
            var scene = EditorSceneManager.OpenScene(DiagnosticScene, OpenSceneMode.Single);
            var interesting = new[] { "Hand", "Ray", "Selector", "Camera", "EventSystem", "Interactor", "OVR" };
            foreach (var root in scene.GetRootGameObjects())
                DumpRecursive(root.transform, interesting, 0);
        }

        private static void DumpRecursive(Transform t, string[] interesting, int depth)
        {
            foreach (var component in t.GetComponents<Component>())
            {
                if (component == null) continue;
                string typeName = component.GetType().Name;
                foreach (var key in interesting)
                {
                    if (typeName.Contains(key) || t.name.Contains(key))
                    {
                        Debug.Log($"[ReMotion] DiagHierarchy depth={depth} go={t.name} comp={typeName}");
                        break;
                    }
                }
            }
            for (int i = 0; i < t.childCount; i++)
                DumpRecursive(t.GetChild(i), interesting, depth + 1);
        }

        [MenuItem("ReMotion XR/Build Pinch Diagnostic Scene+APK")]
        public static void BuildDiagnostic()
        {
            // DiagnosticScene is a byte copy of the SDK's RayExamples
            // sample scene (full official rig), refreshed outside Unity.
            // Unity must have imported it before this runs.
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string apkPath = Path.Combine(projectRoot, DiagnosticApk);
            string manifestPath = apkPath + ".build-manifest.json";

            // Capture every shared setting before touching any of them.
            string prevIdentifier = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);
            string prevProductName = PlayerSettings.productName;
            string prevBundleVersion = PlayerSettings.bundleVersion;
            int prevVersionCode = PlayerSettings.Android.bundleVersionCode;

            BuildResult result = BuildResult.Failed;
            try
            {
                if (File.Exists(apkPath)) File.Delete(apkPath); // never ship a stale APK
                if (File.Exists(manifestPath)) File.Delete(manifestPath);

                var scene = EditorSceneManager.OpenScene(DiagnosticScene, OpenSceneMode.Single);
                if (!scene.IsValid())
                {
                    Debug.LogError("[ReMotion] Diagnostic: scene missing: " + DiagnosticScene);
                    return;
                }
                // Observer only: reports chain edges, feeds nothing into input.
                if (UnityEngine.Object.FindFirstObjectByType<ReMotionXR.Core.PinchDiagnosticLogger>() == null)
                {
                    var loggerGo = new GameObject("PinchDiagnosticLogger");
                    loggerGo.AddComponent<ReMotionXR.Core.PinchDiagnosticLogger>();
                    EditorSceneManager.MoveGameObjectToScene(loggerGo, scene);
                }
                EnsureBanner(scene);
                EditorSceneManager.SaveScene(scene, DiagnosticScene);
                Debug.Log("[ReMotion] Diagnostic scene saved: " + DiagnosticScene);

                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, DiagnosticApplicationId);
                PlayerSettings.productName = DiagnosticProductName;
                PlayerSettings.bundleVersion = DiagnosticVersionName;
                PlayerSettings.Android.bundleVersionCode = DiagnosticVersionCode;
                AssetDatabase.SaveAssets();

                var settings = new BuildPlayerOptions
                {
                    scenes = new[] { DiagnosticScene },
                    locationPathName = apkPath,
                    target = BuildTarget.Android,
                    targetGroup = BuildTargetGroup.Android,
                    options = BuildOptions.Development,
                };
                var report = BuildPipeline.BuildPlayer(settings);
                result = report.summary.result;
                Debug.Log("[ReMotion] Diagnostic APK build: " + result
                          + " errors=" + report.summary.totalErrors + " -> " + apkPath);
            }
            finally
            {
                // Restore every changed shared setting, success or failure.
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, prevIdentifier);
                PlayerSettings.productName = prevProductName;
                PlayerSettings.bundleVersion = prevBundleVersion;
                PlayerSettings.Android.bundleVersionCode = prevVersionCode;
                AssetDatabase.SaveAssets();
                // Restore the editor to the normal app scene.
                if (File.Exists(Path.Combine(projectRoot, MainScene)))
                    EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            }

            if (result != BuildResult.Succeeded || !File.Exists(apkPath))
            {
                Debug.LogError("[ReMotion] Diagnostic build FAILED (" + result + "); no APK published.");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            string sha256 = Sha256Hex(apkPath);
            string manifest =
                "{\n" +
                $"  \"purpose\": \"pinch-diagnostic (separate app; never install over com.remotion.xr)\",\n" +
                $"  \"applicationId\": \"{DiagnosticApplicationId}\",\n" +
                $"  \"productName\": \"{DiagnosticProductName}\",\n" +
                $"  \"scene0\": \"{DiagnosticScene}\",\n" +
                $"  \"versionCode\": {DiagnosticVersionCode},\n" +
                $"  \"versionName\": \"{DiagnosticVersionName}\",\n" +
                $"  \"apkPath\": \"{apkPath.Replace("\\", "\\\\")}\",\n" +
                $"  \"sha256\": \"{sha256}\"\n" +
                "}\n";
            File.WriteAllText(manifestPath, manifest);
            Debug.Log("[ReMotion] BuildManifest " + manifest.Replace("\n", " "));
        }

        /// <summary>Head-following banner so the test app can never be
        /// mistaken for ReMotion. Best effort: launcher name still
        /// identifies the app if the sample rig has no CenterEyeAnchor.</summary>
        private static void EnsureBanner(Scene scene)
        {
            Transform anchor = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (t.name == "CenterEyeAnchor" || t.name == "CenterEye")
                    { anchor = t; break; }
                if (anchor != null) break;
            }
            if (anchor == null)
            {
                Debug.LogWarning("[ReMotion] Diagnostic banner: no CenterEyeAnchor found; banner skipped.");
                return;
            }
            var existing = anchor.Find("PinchDiagnosticBanner");
            if (existing != null) return;
            var bannerGo = new GameObject("PinchDiagnosticBanner");
            bannerGo.transform.SetParent(anchor, false);
            bannerGo.transform.localPosition = new Vector3(0f, -0.42f, 1.4f);
            bannerGo.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var text = bannerGo.AddComponent<TextMesh>();
            text.text = "PINCH DIAGNOSTIC\nseparate test app - your ReMotion app is unchanged";
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 96;
            text.characterSize = 0.02f;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                text.font = font;
                var renderer = bannerGo.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.material = font.material;
            }
            // Parenting under the anchor already places it in the scene.
            Debug.Log("[ReMotion] Diagnostic banner added under " + anchor.name);
        }

        private static string Sha256Hex(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            byte[] hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "");
        }
    }
}
#endif
