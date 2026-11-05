using UnityEngine;
using UnityEngine.EventSystems;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Guarantees the XR UI input path exists: exactly one EventSystem with
    /// an input module. A world-space Canvas + GraphicRaycaster alone can
    /// never receive clicks — uGUI input requires an EventSystem. The scene
    /// generator calls this at build time; ReMotionMainUIController calls it
    /// at runtime so already-saved scenes self-heal. Meta ray/poke
    /// interactors from the Interaction SDK prefab can be added on top;
    /// this is the base every one of them still needs.
    /// </summary>
    public static class XrUiInputSetup
    {
        public static EventSystem EnsureEventSystem()
        {
            var existing = Object.FindFirstObjectByType<EventSystem>();
            if (existing != null)
            {
                EnsureModule(existing.gameObject);
                return existing;
            }
            var go = new GameObject("EventSystem");
            var created = go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
            return created;
        }

        public static void EnsureModule(GameObject eventSystemGo)
        {
            if (eventSystemGo == null) return;
            if (eventSystemGo.GetComponent<BaseInputModule>() == null)
                eventSystemGo.AddComponent<StandaloneInputModule>();
        }
    }
}
