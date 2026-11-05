#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace ReMotionXR.Editor
{
    /// <summary>
    /// Quest 3 project configuration. Menu: ReMotion XR / Configure Quest Project.
    /// XR/OpenXR types are reached by reflection so this file compiles in a
    /// clean Unity install even before XR packages finish importing, and
    /// before the (manual) Meta XR SDK import. See QuestApp/README.md.
    /// </summary>
    public static class ReMotionProjectSetup
    {
        [MenuItem("ReMotion XR/Configure Quest Project")]
        public static void ConfigureQuestProject()
        {
            // ---- Player: Android / Quest ----
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.remotion.xr");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.SetMobileMTRendering(NamedBuildTarget.Android, true);
            PlayerSettings.SetMobileMTRendering(NamedBuildTarget.Standalone, true);
            SetSerializedInt("ProjectSettings/ProjectSettings.asset", "activeInputHandler", 2); // Both
            SetSerializedInt("ProjectSettings/ProjectSettings.asset", "m_ActiveColorSpace", 1); // Linear (Quest required)
            SetGraphicsDynamicBatchingOff();
            Debug.Log("[ReMotion] Player/Android settings applied (Vulkan, IL2CPP ARM64, API 32, Input=Both, Linear).");

            ConfigureXr(BuildTargetGroup.Android);
            ConfigureXr(BuildTargetGroup.Standalone); // Editor / Quest Link testing
            EnableOpenXrFeatures();
            ConfigureOculusProjectConfig();
            EnsureMainSceneInBuildSettings();
            AssetDatabase.SaveAssets();
        }

        // ---- XR Plugin Management + OpenXR via reflection (compile-safe) ----

        private static void ConfigureXr(BuildTargetGroup group)
        {
            var settingsType = FindType("UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget")
                               ?? FindType("UnityEngine.XR.Management.XRGeneralSettingsPerBuildTarget");
            if (settingsType == null)
            {
                Debug.LogWarning("[ReMotion] XR Plugin Management not imported yet; skipping XR loader setup for " + group);
                return;
            }
            try
            {
                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
                var general = settingsType.GetMethod("XRGeneralSettingsForBuildTarget", flags)
                    ?.Invoke(null, new object[] { group });
                if (general == null)
                {
                    var perTarget = settingsType.GetMethod("GetOrCreate", flags)?.Invoke(null, null);
                    if (perTarget == null) { Debug.LogWarning("[ReMotion] Cannot create XR settings for " + group); return; }
                    perTarget.GetType().GetMethod("CreateDefaultSettingsForBuildTarget", flags)
                        ?.Invoke(perTarget, new object[] { group });
                    perTarget.GetType().GetMethod("CreateDefaultManagerSettingsForBuildTarget", flags)
                        ?.Invoke(perTarget, new object[] { group });
                    general = perTarget;
                    Debug.Log("[ReMotion] Created XR settings for " + group);
                }
                var manager = general.GetType().GetMethod("ManagerSettingsForBuildTarget", flags)
                                  ?.Invoke(general, new object[] { group })
                              ?? general.GetType().GetProperty("Manager", flags)?.GetValue(general);
                var assign = FindType("UnityEditor.XR.Management.Metadata.XRPackageMetadataStore")
                    ?.GetMethods().FirstOrDefault(m => m.Name == "AssignLoader");
                var loaderType = FindType("UnityEngine.XR.OpenXR.OpenXRLoader");
                if (manager != null && assign != null && loaderType != null)
                {
                    // AssignLoader matches by full type name, NOT assembly-qualified.
                    assign.Invoke(null, new[] { manager, loaderType.FullName, group });
                    var loadersField = manager.GetType().GetField("m_Loaders",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    int count = (loadersField?.GetValue(manager) as System.Collections.ICollection)?.Count ?? -1;
                    if (count > 0) Debug.Log($"[ReMotion] OpenXR loader assigned for {group} (loaders={count})");
                    else Debug.LogError($"[ReMotion] OpenXR loader assignment FAILED for {group} (loaders={count})");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ReMotion] XR setup skipped for " + group + ": " + e.Message);
            }
        }

        private static void EnableOpenXrFeatures()
        {
            try
            {
                var settingsType = FindType("UnityEngine.XR.OpenXR.OpenXRSettings");
                if (settingsType == null) return;
                var getForGroup = settingsType.GetMethod("GetSettingsForBuildTargetGroup",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var settings = getForGroup?.Invoke(null, new object[] { BuildTargetGroup.Android });
                if (settings == null)
                {
                    var instanceProp = settingsType.GetProperty("ActiveBuildTargetInstance",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    settings = instanceProp?.GetValue(null);
                }
                Debug.Log($"[ReMotion] OpenXR settings lookup: type={settingsType.FullName} method={getForGroup != null} settings={(settings == null ? "null" : "ok")} (feature enable below is settings-independent)");
                // Enable Meta Quest Support + Hand Tracking by feature id,
                // the same way the Project Settings UI does.
                var helpers = FindType("UnityEditor.XR.OpenXR.Features.FeatureHelpers");
                helpers?.GetMethods(System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.NonPublic |
                                    System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "RefreshFeatures")
                    ?.Invoke(null, new object[] { BuildTargetGroup.Android });
                var byIds = helpers?.GetMethods(System.Reflection.BindingFlags.Public |
                                                System.Reflection.BindingFlags.NonPublic |
                                                System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "GetFeaturesWithIdsForBuildTarget");
                // NOTE: handtracking id resolves to a look-alike profile in
                // this Unity version, so Hand Tracking is enabled by name below.
                string[] ids = { "com.unity.openxr.feature.metaquest" };
                var features = byIds?.Invoke(null, new object[] { BuildTargetGroup.Android, ids })
                    as System.Collections.IEnumerable;
                Debug.Log($"[ReMotion] OpenXR feature lookup: helpers={helpers != null} method={byIds != null} result={(features == null ? "null" : "ok")}");
                if (features == null) return;
                foreach (var feature in features)
                {
                    if (feature == null) continue;
                    feature.GetType().GetProperty("enabled")?.SetValue(feature, true);
                    Debug.Log("[ReMotion] OpenXR feature enabled: " + feature.GetType().FullName);
                }

                // Ensure the real Hand Tracking feature (XR Hands) is on and
                // no look-alike interaction profile was enabled in its place.
                var getFeatures = settingsType.GetMethods()
                    .FirstOrDefault(m => m.Name == "GetFeatures" && !m.IsGenericMethodDefinition
                                         && m.GetParameters().Length == 0);
                var all = settings != null && getFeatures != null
                    ? getFeatures.Invoke(settings, null) as System.Collections.IEnumerable : null;
                if (all != null)
                {
                    foreach (var feature in all)
                    {
                        if (feature == null) continue;
                        string simple = feature.GetType().Name;
                        if (simple == "HandTracking" || simple == "MetaQuestFeature")
                        {
                            feature.GetType().GetProperty("enabled")?.SetValue(feature, true);
                            Debug.Log("[ReMotion] OpenXR feature enabled (by name): " + feature.GetType().FullName);
                        }
                        else if (simple == "MicrosoftHandInteraction")
                        {
                            feature.GetType().GetProperty("enabled")?.SetValue(feature, false);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ReMotion] OpenXR feature setup skipped: " + e.Message);
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            // Package assemblies may not be loaded in batchmode yet.
            foreach (string asmName in new[]
                     { "Unity.XR.Management", "Unity.XR.Management.Editor",
                       "Unity.XR.Management.Runtime", "UnityEngine.XR.Management",
                       "Unity.XR.OpenXR", "Unity.XR.OpenXR.Editor", "UnityEngine.XR.OpenXR",
                       "Unity.XR.CoreUtils", "UnityEngine.XR.CoreUtils" })
            {
                try
                {
                    var asm = System.Reflection.Assembly.Load(asmName);
                    var t = asm?.GetType(fullName);
                    if (t != null) return t;
                }
                catch { /* package absent: compile-safe skip */ }
            }
            return null;
        }

        /// <summary>Meta project config: hands + passthrough on (device MR).</summary>
        private static void ConfigureOculusProjectConfig()
        {
            const string path = "Assets/Oculus/OculusProjectConfig.asset";
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (assets == null || assets.Length == 0)
            {
                Debug.LogWarning("[ReMotion] OculusProjectConfig not found (Meta SDK not imported yet); skipping.");
                return;
            }
            var so = new SerializedObject(assets[0]);
            SetInt(so, "handTrackingSupport", 1);        // ControllersAndHands
            SetInt(so, "_insightPassthroughSupport", 2); // Required
            var enabled = so.FindProperty("insightPassthroughEnabled");
            if (enabled != null) enabled.boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[ReMotion] OculusProjectConfig: Hand Tracking = ControllersAndHands, Passthrough = Required.");
        }

        private static void SetInt(SerializedObject so, string name, int value)
        {
            var prop = so.FindProperty(name);
            if (prop != null) prop.intValue = value;
        }

        /// <summary>Main scene must be in Build Settings or the device boots empty.</summary>
        private static void EnsureMainSceneInBuildSettings()
        {
            const string scene = "Assets/Scenes/Main.unity";
            if (!System.IO.File.Exists(scene)) return;
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == scene)) return;
            scenes.Add(new EditorBuildSettingsScene(scene, true));
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("[ReMotion] Added Main scene to Build Settings.");
        }

        private static void SetSerializedInt(string assetPath, string property, int value)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            if (assets == null || assets.Length == 0) return;
            var so = new SerializedObject(assets[0]);
            var prop = so.FindProperty(property) ?? so.FindProperty("m_" + property);
            if (prop != null) { prop.intValue = value; so.ApplyModifiedPropertiesWithoutUndo(); }
        }

        private static void SetGraphicsDynamicBatchingOff()
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new SerializedObject(assets[0]);
            var tiers = so.FindProperty("m_TierSettings");
            if (tiers == null) return;
            for (int i = 0; i < tiers.arraySize; i++)
            {
                var tier = tiers.GetArrayElementAtIndex(i);
                var standard = tier.FindPropertyRelative("standard");
                var dyn = standard?.FindPropertyRelative("useDynamicBatching");
                if (dyn != null) dyn.boolValue = false;
            }
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
#endif
