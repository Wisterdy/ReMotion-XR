using System;
using System.Reflection;
using UnityEngine;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Repairs hand handedness at scene load. The Meta building-block hand
    /// prefab ships with HandType/_skeletonType/_meshType = None, and the
    /// scene generator's original reflection silently failed to set them
    /// (property lookup on an internal field; public-only lookup on
    /// internal setters), so the right hand rendered mirrored.
    ///
    /// Skeleton/mesh types are NEVER guessed here: this project uses the
    /// OpenXR hand skeleton version (OculusRuntimeSettings), whose types
    /// are XRHandLeft/XRHandRight with a different bone count than the
    /// classic HandLeft/HandRight. Forcing the classic family made bone
    /// count and mesh bone-weights diverge ("Bone weights do not match
    /// bones", GPU buffer overflow, native crash). Instead the guard sets
    /// OVRHand.HandType (the source of truth), then asks each component's
    /// own data provider for its skeleton/mesh type and applies it through
    /// Meta's internal setters, which reinitialize and rebind correctly.
    /// </summary>
    public static class HandednessGuard
    {
        public enum HandSide { None, Left, Right }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void FixSceneHands()
        {
            // Main-scene repair only: the pinch diagnostic scene must run
            // the SDK sample exactly as shipped, unmodified by this guard.
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Main") return;
            int fixedCount = 0;
            foreach (var root in UnityEngine.SceneManagement.SceneManager
                         .GetActiveScene().GetRootGameObjects())
                fixedCount += ApplyToHierarchy(root.transform);
            if (fixedCount > 0)
                Debug.Log($"[ReMotion] HandednessGuard set side on {fixedCount} hand component(s).");
        }

        /// <summary>Sets handedness on every Meta hand component below root.</summary>
        public static int ApplyToHierarchy(Transform root)
        {
            if (root == null) return 0;
            var components = root.GetComponentsInChildren<Component>(true);
            int count = 0;

            // Pass 1: OVRHand.HandType is the source of truth for providers.
            foreach (var component in components)
            {
                if (component == null || component.GetType().Name != "OVRHand") continue;
                var side = ResolveSide(component.transform);
                if (side == HandSide.None) continue;
                if (SetEnumField(component, "HandType", "Hand" + side)) count++;
            }

            // Pass 2: skeleton/mesh follow their data provider's type.
            foreach (var component in components)
            {
                if (component == null) continue;
                string typeName = component.GetType().Name;
                if (typeName != "OVRSkeleton" && typeName != "OVRMesh") continue;
                var side = ResolveSide(component.transform);
                if (side == HandSide.None) continue;
                bool changed = typeName == "OVRSkeleton"
                    ? ApplyProviderType(component, "IOVRSkeletonDataProvider",
                                        "GetSkeletonType", "SetSkeletonType", side)
                    : ApplyProviderType(component, "IOVRMeshDataProvider",
                                        "GetMeshType", "SetMeshType", side);
                if (changed) count++;
                // OVRMesh loaded its mesh in Awake (with the prefab's
                // default left type) before this guard could run, and on
                // device nothing ever reloads it: the right hand kept the
                // left mesh under right bones — the deformation. Reload
                // the mesh now, before OVRMeshRenderer binds at Start.
                if (changed && typeName == "OVRMesh") ReloadMesh(component);
            }
            return count;
        }

        private static void ReloadMesh(Component ovrMesh)
        {
            var getter = FindMethod(ovrMesh.GetType(), "GetMeshType");
            var initialize = FindMethod(ovrMesh.GetType(), "Initialize");
            if (getter == null || initialize == null ||
                initialize.GetParameters().Length != 1) return;
            try { initialize.Invoke(ovrMesh, new[] { getter.Invoke(ovrMesh, null) }); }
            catch (Exception e)
            {
                Debug.LogWarning("[ReMotion] HandednessGuard mesh reload: " + e.Message);
            }
        }

        private static bool ApplyProviderType(Component target, string interfaceName,
                                              string getterName, string setterName,
                                              HandSide side)
        {
            var setter = FindEnumSetter(target.GetType(), setterName);
            if (setter == null) return false;
            var paramType = setter.GetParameters()[0].ParameterType;

            object value = ProviderValue(target, interfaceName, getterName);
            if (value == null || value.GetType() != paramType)
            {
                // Fallback: derive the family from the configured skeleton
                // version (OpenXR -> XRHand*, OVR -> Hand*), never assume.
                string enumName = SkeletonTypePrefix() + side;
                try { value = Enum.Parse(paramType, enumName); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ReMotion] HandednessGuard {setterName}: {e.Message}");
                    return false;
                }
            }

            var getter = FindMethod(target.GetType(), setterName.Replace("Set", "Get"));
            if (getter != null && Equals(getter.Invoke(target, null), value))
                return false; // already correct: no re-init churn
            setter.Invoke(target, new[] { value });
            return true;
        }

        /// <summary>Asks the nearest OVRHand data provider for its type value.</summary>
        private static object ProviderValue(Component target, string interfaceName, string getterName)
        {
            for (var node = target.transform; node != null; node = node.parent)
            {
                foreach (var candidate in node.GetComponents<Component>())
                {
                    if (candidate == null || candidate.GetType().Name != "OVRHand") continue;
                    foreach (var iface in candidate.GetType().GetInterfaces())
                    {
                        if (iface.Name != interfaceName) continue;
                        var getter = iface.GetMethod(getterName);
                        if (getter != null) return getter.Invoke(candidate, null);
                    }
                }
            }
            return null;
        }

        /// <summary>"XRHand" when the project uses the OpenXR skeleton, else "Hand".</summary>
        public static string SkeletonTypePrefix()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var settingsType = asm.GetType("OVRRuntimeSettings");
                    if (settingsType == null) continue;
                    var instance = settingsType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    var version = instance?.GetType().GetProperty("HandSkeletonVersion")
                        ?.GetValue(instance)?.ToString();
                    return version == "OpenXR" ? "XRHand" : "Hand";
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[ReMotion] HandednessGuard version lookup: " + e.Message);
            }
            return "Hand";
        }

        /// <summary>
        /// Side from hierarchy: an ancestor named like LeftHandAnchor /
        /// RightHandAnchor (or the hand object itself) decides. Pure name
        /// logic so it is unit-testable without the Meta SDK.
        /// </summary>
        public static HandSide ResolveSide(Transform node)
        {
            for (var t = node; t != null; t = t.parent)
            {
                var side = SideFromName(t.name);
                if (side != HandSide.None) return side;
            }
            return HandSide.None;
        }

        public static HandSide SideFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return HandSide.None;
            bool left = name.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0;
            bool right = name.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0;
            if (left == right) return HandSide.None; // both or neither: ambiguous
            return left ? HandSide.Left : HandSide.Right;
        }

        private static MethodInfo FindEnumSetter(Type type, string methodName)
        {
            var method = FindMethod(type, methodName);
            if (method == null || method.GetParameters().Length != 1) return null;
            return method.GetParameters()[0].ParameterType.IsEnum ? method : null;
        }

        private static MethodInfo FindMethod(Type type, string methodName)
        {
            const BindingFlags flags = BindingFlags.Instance |
                                       BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var method in type.GetMethods(flags))
                if (method.Name == methodName) return method;
            return null;
        }

        private static bool SetEnumField(Component target, string fieldName, string enumName)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || !field.FieldType.IsEnum) return false;
            try
            {
                var value = Enum.Parse(field.FieldType, enumName);
                if (Equals(field.GetValue(target), value)) return false; // already correct
                field.SetValue(target, value);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ReMotion] HandednessGuard {fieldName}: {e.Message}");
                return false;
            }
        }
    }
}
