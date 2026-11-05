using UnityEngine;
using UnityEngine.UI;
using ReMotionXR.Accessibility;
using ReMotionXR.Core;

namespace ReMotionXR.Library
{
    /// <summary>
    /// Single source of the app's visual language: dark slate panels,
    /// one accent color, muted secondary text, rounded corners, and
    /// consistent button styles. Used by the scene generator and the
    /// runtime card factory so every surface looks the same.
    /// </summary>
    public static class ReMotionUiTheme
    {
        public static readonly Color Background = new Color(0.055f, 0.07f, 0.09f, 0.98f);
        public static readonly Color Panel = new Color(0.09f, 0.115f, 0.145f, 1f);
        public static readonly Color PanelAlt = new Color(0.13f, 0.165f, 0.205f, 1f);
        public static readonly Color Divider = new Color(0.19f, 0.23f, 0.28f, 1f);
        // Darkened so white labels meet WCAG AA (>=4.5:1) on Quest displays.
        public static readonly Color Accent = new Color(0.1f, 0.38f, 0.9f, 1f);
        public static readonly Color AccentPressed = new Color(0.08f, 0.3f, 0.72f, 1f);
        public static readonly Color RecordRed = new Color(0.72f, 0.11f, 0.11f, 1f);
        public static readonly Color Success = new Color(0.16f, 0.72f, 0.42f, 1f);
        public static readonly Color Warning = new Color(0.98f, 0.66f, 0.15f, 1f);
        public static readonly Color Danger = new Color(0.96f, 0.36f, 0.36f, 1f);
        public static readonly Color TextPrimary = new Color(0.95f, 0.97f, 0.99f, 1f);
        /// <summary>Muted text; brightens under High Contrast.</summary>
        public static Color TextSecondary => AccessibilitySettings.HighContrast
            ? new Color(0.86f, 0.9f, 0.95f, 1f)
            : new Color(0.62f, 0.68f, 0.75f, 1f);

        private static Sprite _roundedSprite;

        /// <summary>Shared 9-sliced rounded-rectangle sprite (radius ~14px).</summary>
        public static Sprite RoundedSprite
        {
            get
            {
                if (_roundedSprite != null) return _roundedSprite;
                const int size = 48, radius = 14;
                var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(radius - x, 0, x - (size - 1 - radius));
                    float dy = Mathf.Max(radius - y, 0, y - (size - 1 - radius));
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(radius - d + 0.5f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a);
                }
                tex.SetPixels(pixels);
                tex.Apply();
                _roundedSprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                    new Vector4(radius, radius, radius, radius));
                return _roundedSprite;
            }
        }

        public static Image MakePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = RoundedSprite;
            image.type = Image.Type.Sliced;
            image.color = color;
            return image;
        }

        public static Text Label(Transform parent, string name, string content,
                                 int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<Text>();
            text.text = content;
            text.fontSize = AccessibilitySettings.ScaledFontSize(size);
            text.color = color;
            text.alignment = anchor;
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false; // decorative text never enlarges hit targets
            return text;
        }

        public enum ButtonStyle { Primary, Secondary, Danger, Record }

        public static Button Button(Transform parent, string name, string label,
                                    ButtonStyle style, int fontSize = 15)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.sprite = RoundedSprite;
            image.type = Image.Type.Sliced;
            image.color = style switch
            {
                ButtonStyle.Primary => Accent,
                ButtonStyle.Record => RecordRed,
                ButtonStyle.Danger => new Color(0.35f, 0.16f, 0.16f, 1f),
                _ => PanelAlt,
            };
            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            button.colors = colors;
            button.targetGraphic = image;
            // Every menu button is also dwell-operable (accessibility):
            // hover with the pinch ray for DwellSeconds to activate.
            go.AddComponent<DwellClick>();
            var text = Label(go.transform, "Label", label, fontSize,
                TextPrimary, TextAnchor.MiddleCenter);
            // Stretch the label within its own button, whatever its width.
            var labelRt = text.GetComponent<RectTransform>();
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(8f, 2f);
            labelRt.offsetMax = new Vector2(-8f, -2f);
            return button;
        }

        public static Color BackgroundForStyle(ButtonStyle style) => style switch
        {
            ButtonStyle.Primary => Accent,
            ButtonStyle.Record => RecordRed,
            ButtonStyle.Danger => new Color(0.35f, 0.16f, 0.16f, 1f),
            _ => PanelAlt,
        };

        /// <summary>
        /// Restyles a button built elsewhere (saved scenes ship plain white
        /// Images with white labels = invisible text). Applies the themed
        /// background and a white label with AA contrast on it.
        /// </summary>
        public static void ApplyStyle(Button button, ButtonStyle style, int fontSize = 0)
        {
            if (button == null) return;
            var image = button.GetComponent<Image>();
            if (image == null) image = button.gameObject.AddComponent<Image>();
            image.sprite = RoundedSprite;
            image.type = Image.Type.Sliced;
            image.color = BackgroundForStyle(style);
            button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.12f, 1.12f, 1.12f, 1f);
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.disabledColor = new Color(0.45f, 0.45f, 0.45f, 0.6f);
            button.colors = colors;
            NormalizeButtonLabel(button);
            var label = button.GetComponentInChildren<Text>();
            if (label != null)
            {
                label.color = TextPrimary;
                if (fontSize > 0) label.fontSize = AccessibilitySettings.ScaledFontSize(fontSize);
            }
        }

        /// <summary>
        /// Normalizes a button's EXISTING label (saved-scene labels were
        /// serialized oversized with raycast on): stretch it inside its
        /// owning button with padding and stop it capturing the ray —
        /// hits resolve to the button itself, never a neighbor.
        /// </summary>
        public static void NormalizeButtonLabel(Button button)
        {
            if (button == null) return;
            foreach (var text in button.GetComponentsInChildren<Text>(true))
            {
                text.raycastTarget = false;
                var rt = text.GetComponent<RectTransform>();
                if (rt == null || text.transform.parent != button.transform) continue;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = new Vector2(8f, 2f);
                rt.offsetMax = new Vector2(-8f, -2f);
                text.horizontalOverflow = HorizontalWrapMode.Wrap;
                text.verticalOverflow = VerticalWrapMode.Truncate;
            }
        }

        /// <summary>WCAG relative-luminance contrast ratio (1..21).</summary>
        public static float ContrastRatio(Color a, Color b)
        {
            float la = RelativeLuminance(a), lb = RelativeLuminance(b);
            float lighter = Mathf.Max(la, lb), darker = Mathf.Min(la, lb);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        private static float RelativeLuminance(Color c)
        {
            float Channel(float v) => v <= 0.03928f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
            return 0.2126f * Channel(c.r) + 0.7152f * Channel(c.g) + 0.0722f * Channel(c.b);
        }

        /// <summary>Small pill: colored dot + label, e.g. a status chip.</summary>
        public static Text StatusPill(Transform parent, string name, string status)
        {
            var pill = MakePanel(parent, name, PanelAlt);
            pill.GetComponent<RectTransform>().sizeDelta = new Vector2(130f, 30f);
            var dot = MakePanel(pill.transform, "Dot", ColorForStatus(status));
            var dotRt = dot.GetComponent<RectTransform>();
            dotRt.anchorMin = dotRt.anchorMax = new Vector2(0f, 0.5f);
            dotRt.anchoredPosition = new Vector2(16f, 0f);
            dotRt.sizeDelta = new Vector2(10f, 10f);
            var text = Label(pill.transform, "Label", status, 13, TextPrimary,
                             TextAnchor.MiddleCenter);
            text.GetComponent<RectTransform>().sizeDelta = new Vector2(120f, 26f);
            return text;
        }

        public static Color ColorForStatus(string status) => status switch
        {
            "Ready" => Success,
            "Failed" => Danger,
            "Processing" => Warning,
            _ => TextSecondary,
        };

        public static void SetSize(Transform t, Vector2 size) =>
            t.GetComponent<RectTransform>().sizeDelta = size;
    }
}
