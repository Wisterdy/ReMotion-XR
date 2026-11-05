using UnityEngine;

namespace ReMotionXR.Core
{
    /// <summary>
    /// User accessibility preferences (persisted in PlayerPrefs, edited by
    /// the Settings UI). One standard across the app, matching the scene
    /// indicators' model: every action must work by pinch/tap OR by gaze
    /// dwell, text must scale, and motion/contrast must be adjustable.
    /// </summary>
    public static class AccessibilitySettings
    {
        private const string TextScaleKey = "remotion_text_scale";
        private const string DwellEnabledKey = "remotion_dwell_enabled";
        private const string DwellSecondsKey = "remotion_dwell_seconds";
        private const string HighContrastKey = "remotion_high_contrast";
        private const string ReduceMotionKey = "remotion_reduce_motion";

        /// <summary>UI text multiplier (1.0 / 1.25 / 1.5).</summary>
        public static float TextScale
        {
            get => Mathf.Clamp(PlayerPrefs.GetFloat(TextScaleKey, 1f), 1f, 1.5f);
            set { PlayerPrefs.SetFloat(TextScaleKey, Mathf.Clamp(value, 1f, 1.5f)); PlayerPrefs.Save(); }
        }

        /// <summary>
        /// Hover-to-select for users who cannot pinch. Normal interaction
        /// is explicit native pinch: dwell is OFF until a user explicitly
        /// opts in (a stored preference is always preserved; only the
        /// unconfigured default changed, so hover can never masquerade
        /// as a working pinch selector).
        /// </summary>
        public static bool DwellEnabled
        {
            get => PlayerPrefs.HasKey(DwellEnabledKey)
                   && PlayerPrefs.GetInt(DwellEnabledKey, 0) == 1;
            set { PlayerPrefs.SetInt(DwellEnabledKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>True when the user has explicitly chosen a dwell mode.</summary>
        public static bool DwellConfigured => PlayerPrefs.HasKey(DwellEnabledKey);

        /// <summary>Scoped diagnostic override (never persisted).</summary>
        public static bool DiagnosticSuppressDwell;

        /// <summary>Dwell time; matches ObjectIndicator's 1s gaze standard.</summary>
        public static float DwellSeconds
        {
            get => Mathf.Clamp(PlayerPrefs.GetFloat(DwellSecondsKey, 1f), 0.5f, 2.5f);
            set { PlayerPrefs.SetFloat(DwellSecondsKey, Mathf.Clamp(value, 0.5f, 2.5f)); PlayerPrefs.Save(); }
        }

        public static bool HighContrast
        {
            get => PlayerPrefs.GetInt(HighContrastKey, 0) == 1;
            set { PlayerPrefs.SetInt(HighContrastKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>Stops indicator pulsing and other decorative motion.</summary>
        public static bool ReduceMotion
        {
            get => PlayerPrefs.GetInt(ReduceMotionKey, 0) == 1;
            set { PlayerPrefs.SetInt(ReduceMotionKey, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        /// <summary>Scaled font size for a base size under current settings.</summary>
        public static int ScaledFontSize(int baseSize) =>
            Mathf.RoundToInt(baseSize * TextScale);
    }
}
