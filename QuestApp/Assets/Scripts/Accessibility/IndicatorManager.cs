using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ReMotionXR.Core;

namespace ReMotionXR.Accessibility
{
    /// <summary>
    /// Spawns ObjectIndicators for scene objects/people and shows the
    /// accessibility card (Name, What, Simple Instruction, Listen) while
    /// outlining the selected object.
    /// </summary>
    public class IndicatorManager : MonoBehaviour
    {
        public static IndicatorManager Instance { get; private set; }

        public ObjectIndicator indicatorPrefab;
        public GameObject cardPanel;
        public Text cardTitleText;
        public Text cardWhatText;
        public Text cardInstructionText;
        public Button listenButton;

        private readonly List<ObjectIndicator> _indicators = new List<ObjectIndicator>();
        private Renderer _outlined;
        private Color _outlinedColor;
        private AccessibilityData _current;

        private void Awake() => Instance = this;

        public ObjectIndicator Spawn(Transform target, AccessibilityData data)
        {
            if (indicatorPrefab == null) return null;
            var ind = Instantiate(indicatorPrefab, transform);
            ind.Bind(target, data);
            _indicators.Add(ind);
            return ind;
        }

        public void SetIndicatorsEnabled(bool on)
        {
            foreach (var ind in _indicators) ind.gameObject.SetActive(on);
            if (!on) HideCard();
        }

        public void ShowCard(AccessibilityData data, Transform target)
        {
            _current = data;
            if (cardTitleText != null) cardTitleText.text = data.displayName;
            if (cardWhatText != null) cardWhatText.text = data.shortDescription;
            if (cardInstructionText != null) cardInstructionText.text = data.simpleInstruction;
            if (cardPanel != null) cardPanel.SetActive(true);
            ClearOutline();
            var r = target != null ? target.GetComponentInChildren<Renderer>() : null;
            if (r != null)
            {
                _outlined = r;
                _outlinedColor = r.material.color;
                r.material.color = ObjectIndicator.ColorFor(data.indicatorColor);
            }
        }

        public void HideCard()
        {
            if (cardPanel != null) cardPanel.SetActive(false);
            ClearOutline();
            _current = null;
        }

        /// <summary>Listen button: plays accessibility audio (stub logs the clip name).</summary>
        public void OnListenPressed()
        {
            if (_current == null) return;
            // TODO: play _current.audioFile via AudioSource + TTS fallback.
            Debug.Log($"[ReMotion] Listen: {_current.audioFile} ({_current.displayName})");
        }

        private void ClearOutline()
        {
            if (_outlined != null) _outlined.material.color = _outlinedColor;
            _outlined = null;
        }
    }
}
