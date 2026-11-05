using UnityEngine;
using UnityEngine.SceneManagement;

namespace ReMotionXR.Core
{
    public static class AppLaunchEvidence
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Debug.Log($"[ReMotionLaunch] package={Application.identifier} " +
                      $"version={Application.version} scene={scene.path} " +
                      $"index={scene.buildIndex} mode={mode}");

            if (Application.identifier == "com.remotion.xr" &&
                scene.name == "PinchDiagnostic")
            {
                Debug.LogError("[ReMotionLaunch] WRONG ARTIFACT: normal package loaded " +
                               "PinchDiagnostic. Stop diagnostic delivery; rebuild Main-only.");
            }
        }
    }
}
