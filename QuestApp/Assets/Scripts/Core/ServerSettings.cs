using UnityEngine;

namespace ReMotionXR.Core
{
    /// <summary>
    /// Gateway settings (Quest Settings UI edits this asset at runtime).
    /// NEVER hardcode endpoints or keys in code, and never commit values.
    /// RUNPOD_API_KEY must NEVER be here or in the APK — it lives only in
    /// the gateway/server environment. The on-device key below is the
    /// separate REMOTION_API_KEY app credential, sent to the gateway as
    /// X-API-Key and nowhere else.
    /// </summary>
    [CreateAssetMenu(menuName = "ReMotion XR/Server Settings", fileName = "ServerSettings")]
    public class ServerSettings : ScriptableObject
    {
        [Tooltip("FastAPI gateway base URL, e.g. https://gateway.example (the /v1 API)")]
        public string gatewayUrl = "";

        [Tooltip("REMOTION_API_KEY app credential for the gateway (X-API-Key).")]
        public string remotionApiKey = "";

        [Tooltip("Optional local FastAPI base URL for development, e.g. http://192.168.1.10:8000")]
        public string localServerUrl = "http://127.0.0.1:8000";

        // Legacy fields: the Quest must not call RunPod directly any
        // more. Kept so old saved assets load; superseded by gatewayUrl.
        [Tooltip("Legacy: direct RunPod endpoint. No longer used by the client.")]
        public string serverlessEndpointUrl = "";

        /// <summary>Gateway base URL (new field wins; legacy endpoint is migrated, not used as RunPod).</summary>
        public string GatewayUrl =>
            !string.IsNullOrWhiteSpace(gatewayUrl) ? gatewayUrl.TrimEnd('/') : "";

        public bool HasGateway => !string.IsNullOrWhiteSpace(GatewayUrl);

        // Back-compat for existing callers/tests.
        public bool HasServerless => HasGateway;

        /// <summary>Runtime overrides from the Settings UI (persisted locally only).</summary>
        public void LoadRuntimeOverrides()
        {
            gatewayUrl = PlayerPrefs.GetString("remotion_gateway", gatewayUrl);
            remotionApiKey = PlayerPrefs.GetString("remotion_api_key", remotionApiKey);
        }

        public void SaveRuntimeOverrides()
        {
            PlayerPrefs.SetString("remotion_gateway", gatewayUrl);
            PlayerPrefs.SetString("remotion_api_key", remotionApiKey);
            PlayerPrefs.Save();
        }
    }
}
