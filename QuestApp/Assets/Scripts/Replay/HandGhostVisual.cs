using System.Collections.Generic;
using UnityEngine;

namespace ReMotionXR.Replay
{
    /// <summary>
    /// Full-joint ghost hand: one small sphere per recorded XR joint,
    /// positioned relative to the ghost root (joint 0). Replaces the
    /// single floating cube, so a replay visibly shows the recorded
    /// hand opening, closing and moving — the actual captured motion.
    /// </summary>
    public class HandGhostVisual : MonoBehaviour
    {
        private readonly List<Transform> _markers = new List<Transform>();
        private Color _color = Color.cyan;
        private HandModelDriver _model;
        private bool _modelTried;

        public int MarkerCount => _markers.Count;
        public bool UsingHandModel => _model != null && _model.IsReady;

        public void Configure(Color color) => _color = color;

        public void SetJoints(Vector3[] joints) => SetPose(joints, null);

        public void SetPose(Vector3[] joints, Quaternion[] rotations)
        {
            if (joints == null || joints.Length == 0) return;
            if (!_modelTried) { _modelTried = true; TryBuildModel(); }
            if (UsingHandModel && _model.Pose(joints, rotations, transform.parent))
            {
                // Real blue hand model is posed; ball markers stay hidden.
                SetMarkersVisible(false);
                var own = GetComponent<Renderer>();
                if (own != null) own.enabled = false;
                return;
            }
            EnsureMarkers(joints.Length);
            Vector3 origin = joints[0];
            // Markers are children of the scaled ghost cube: compensate so
            // world offsets/sizes match the recorded joint data exactly.
            float parentScale = Mathf.Abs(transform.lossyScale.x) > 0.0001f
                ? transform.lossyScale.x : 1f;
            for (int i = 0; i < _markers.Count; i++)
            {
                bool active = i < joints.Length;
                _markers[i].gameObject.SetActive(active);
                if (!active) continue;
                _markers[i].localPosition = (joints[i] - origin) / parentScale;
                float worldSize = i == 0 ? 0.035f : 0.02f;
                _markers[i].localScale = Vector3.one * (worldSize / parentScale);
            }
        }

        private void SetMarkersVisible(bool visible)
        {
            foreach (var m in _markers) m.gameObject.SetActive(visible);
        }

        /// <summary>
        /// Instantiate the shipped OpenXR hand model for this ghost's
        /// side (blue Meta-style hand), parented to the scene root so it
        /// follows scene anchoring. Falls back to joint markers when the
        /// model or its bones are unavailable.
        /// </summary>
        private void TryBuildModel()
        {
            bool left = name.Contains("Left");
            var prefab = Resources.Load<GameObject>(left ? "Hands/OpenXRLeftHand" : "Hands/OpenXRRightHand");
            if (prefab == null || transform.parent == null) return;
            var instance = Instantiate(prefab, transform.parent);
            instance.name = name + "Model";
            // Use the same Meta material as the live tracked-hand overlay.
            // A generic Standard tint loses its gradient/rim/wrist shading.
            var handMaterial = Resources.Load<Material>("ReplayBlueHand");
            foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                if (handMaterial != null) renderer.sharedMaterial = handMaterial;
                else SceneMaterials.ApplyColor(renderer, _color);
            _model = instance.AddComponent<HandModelDriver>();
            _model.Bind(instance);
            Debug.Log($"[ReMotion] Replay hand {name}: bones={_model.BoneCount}, material={handMaterial?.name}");
            if (!_model.IsReady)
            {
                Debug.LogWarning("[ReMotion] Hand model has no XRHand bones; using joint markers.");
                Destroy(instance);
                _model = null;
            }
        }

        private void EnsureMarkers(int count)
        {
            while (_markers.Count < count)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "JointMarker";
                go.transform.SetParent(transform, false);
                go.transform.localScale = Vector3.one * (_markers.Count == 0 ? 0.035f : 0.02f);
                var collider = go.GetComponent<Collider>();
                if (collider != null)
                {
                    if (Application.isPlaying) Destroy(collider);
                    else DestroyImmediate(collider);
                }
                SceneMaterials.ApplyColor(go.GetComponent<Renderer>(), _color);
                _markers.Add(go.transform);
            }
        }
    }
}
