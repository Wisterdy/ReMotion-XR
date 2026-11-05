using UnityEngine;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// God Mode: scales the replay to a tabletop diorama the user can
    /// rotate and zoom to inspect form from any angle.
    /// </summary>
    public class GodModeController : MonoBehaviour
    {
        [Tooltip("Root of the replayed scene that gets scaled down.")]
        public Transform sceneRoot;
        [Range(0.05f, 1f)] public float dioramaScale = 0.2f;
        public float rotateSpeed = 60f;
        public float zoomSpeed = 0.5f;
        public bool GodModeActive { get; private set; }

        private Vector3 _fullScale = Vector3.one;
        private Quaternion _fullRotation = Quaternion.identity;
        private Vector3 _fullPosition = Vector3.zero;

        private void Awake()
        {
            if (sceneRoot != null)
            {
                _fullScale = sceneRoot.localScale;
                _fullRotation = sceneRoot.localRotation;
                _fullPosition = sceneRoot.localPosition;
            }
        }

        /// <summary>Restores the replay root exactly as playback authored it.</summary>
        public void ResetView()
        {
            if (sceneRoot == null) return;
            sceneRoot.localScale = _fullScale;
            sceneRoot.localRotation = _fullRotation;
            sceneRoot.localPosition = _fullPosition;
        }

        public void SetGodMode(bool on)
        {
            GodModeActive = on;
            if (sceneRoot == null) return;
            if (on) sceneRoot.localScale = _fullScale * dioramaScale;
            else ResetView();
        }

        private void Update()
        {
            if (!GodModeActive) return;
            // Wired input path: either controller thumbstick drives the
            // diorama. No device in Editor -> no-op, ApplyInput is used.
            var devices = new System.Collections.Generic.List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevicesWithCharacteristics(
                UnityEngine.XR.InputDeviceCharacteristics.Controller, devices);
            foreach (var device in devices)
            {
                if (device.TryGetFeatureValue(
                        UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 stick)
                    && stick.sqrMagnitude > 0.01f)
                {
                    ApplyInput(stick, Time.deltaTime);
                    break;
                }
            }
        }

        /// <summary>Stick/thumb input: x = rotate, y = zoom. Call from XR input polling.</summary>
        public void ApplyInput(Vector2 stick, float deltaTime)
        {
            if (!GodModeActive || sceneRoot == null) return;
            sceneRoot.Rotate(Vector3.up, stick.x * rotateSpeed * deltaTime, Space.World);
            float s = Mathf.Clamp(sceneRoot.localScale.x + stick.y * zoomSpeed * deltaTime,
                _fullScale.x * 0.05f, _fullScale.x);
            sceneRoot.localScale = Vector3.one * s;
        }
    }
}
