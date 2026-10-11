using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GSCapture.UI
{
    /// <summary>
    /// Small code-only UI kit: anti-aliased rounded/circle/ring sprites generated at runtime (no texture
    /// assets) plus factory helpers, so every screen can be built and rendered headless for previews.
    /// </summary>
    public static class UIKit
    {
        static Sprite rounded, circle, ring;

        public static Sprite Rounded => rounded ? rounded : rounded = MakeRounded(128, 48);
        public static Sprite Circle => circle ? circle : circle = MakeRing(128, 1f);
        public static Sprite Ring => ring ? ring : ring = MakeRing(256, 0.12f);

        static Sprite MakeRounded(int size, int radius)
        {
            var tex = NewTexture(size);
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(0, Mathf.Max(radius - x - 0.5f, x + 0.5f - (size - radius)));
                float dy = Mathf.Max(0, Mathf.Max(radius - y - 0.5f, y + 0.5f - (size - radius)));
                float a = Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        /// <summary>thickness = fraction of the radius that is opaque (1 = filled disc).</summary>
        static Sprite MakeRing(int size, float thickness)
        {
            var tex = NewTexture(size);
            var px = new Color32[size * size];
            float r = size * 0.5f, inner = r * (1f - thickness);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                float a = Mathf.Clamp01(r - d) * (thickness >= 1f ? 1f : Mathf.Clamp01(d - inner));
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        static Texture2D NewTexture(int size) =>
            new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

        // ------------------------------------------------------------------ layout helpers

        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        public static RectTransform Stretch(this RectTransform rt, float left = 0, float right = 0, float top = 0, float bottom = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        /// <summary>Anchor to a point of the parent (0..1), with a pivot on the same point, offset and size.</summary>
        public static RectTransform Place(this RectTransform rt, Vector2 anchor, Vector2 offset, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
            return rt;
        }

        // ------------------------------------------------------------------ elements

        public static Image Panel(Transform parent, string name, Color color, float radiusScale = 1f)
        {
            var rt = Rect(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Rounded;
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 48f / Mathf.Max(1f, Theme.Radius * radiusScale);
            img.color = color;
            return img;
        }

        public static Image Dot(Transform parent, string name, Color color, float size)
        {
            var rt = Rect(parent, name);
            rt.sizeDelta = new Vector2(size, size);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = Circle;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        public static Text Label(Transform parent, string name, string text, int size, Color color, Font font = null,
                                 TextAnchor align = TextAnchor.MiddleLeft)
        {
            var rt = Rect(parent, name);
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font ? font : Theme.Regular;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.text = text;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.lineSpacing = 1.1f;
            return t;
        }

        public enum ButtonStyle { Primary, Secondary, Ghost, Danger }

        public static Button Button(Transform parent, string name, string label, ButtonStyle style, Action onClick,
                                    int fontSize = Theme.BodySize)
        {
            var bg = Panel(parent, name, Color.white, 2f);
            var btn = bg.gameObject.AddComponent<Button>();
            Color fill, text;
            switch (style)
            {
                case ButtonStyle.Primary: fill = Theme.Accent; text = Theme.OnAccent; break;
                case ButtonStyle.Danger: fill = Theme.WithAlpha(Theme.Danger, 0.16f); text = Theme.Danger; break;
                case ButtonStyle.Ghost: fill = new Color(1, 1, 1, 0.0f); text = Theme.TextDim; break;
                default: fill = Theme.GlassLight; text = Theme.Text; break;
            }
            bg.color = fill;
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1, 1, 1, 0.92f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            colors.disabledColor = new Color(1, 1, 1, 0.35f);
            colors.fadeDuration = 0.08f;
            btn.colors = colors;
            var l = Label(bg.transform, "Label", label, fontSize, text, style == ButtonStyle.Primary ? Theme.Bold : Theme.SemiBold,
                TextAnchor.MiddleCenter);
            l.rectTransform.Stretch(16, 16);
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return btn;
        }

        public static void SetLabel(this Button b, string text) => b.GetComponentInChildren<Text>().text = text;

        /// <summary>Rounded status pill: coloured dot + text on a tinted background. Returns (root, dot, label).</summary>
        public static (Image bg, Image dot, Text label) Pill(Transform parent, string name, string text, Color color,
                                                              int fontSize = Theme.CaptionSize)
        {
            var bg = Panel(parent, name, Theme.WithAlpha(color, 0.16f), 2f);
            bg.raycastTarget = false;
            var dot = Dot(bg.transform, "Dot", color, fontSize * 0.45f);
            dot.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(fontSize * 0.75f, 0), dot.rectTransform.sizeDelta);
            var label = Label(bg.transform, "Label", text, fontSize, color, Theme.SemiBold);
            label.rectTransform.Stretch(fontSize * 1.5f, fontSize * 0.7f);
            return (bg, dot, label);
        }

        public static void SetPill(this (Image bg, Image dot, Text label) pill, string text, Color color)
        {
            pill.bg.color = Theme.WithAlpha(color, 0.16f);
            pill.dot.color = color;
            pill.label.color = color;
            pill.label.text = text;
        }

        /// <summary>Circular progress: faint track + radial fill. Returns the fill image (set fillAmount).</summary>
        public static Image ProgressRing(Transform parent, string name, float size, Color color)
        {
            var root = Rect(parent, name);
            root.sizeDelta = new Vector2(size, size);
            var track = root.gameObject.AddComponent<Image>();
            track.sprite = Ring;
            track.color = new Color(1, 1, 1, 0.12f);
            track.raycastTarget = false;
            var fillRt = Rect(root, "Fill").Stretch();
            var fill = fillRt.gameObject.AddComponent<Image>();
            fill.sprite = Ring;
            fill.color = color;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Radial360;
            fill.fillOrigin = (int)Image.Origin360.Top;
            fill.fillClockwise = true;
            fill.fillAmount = 0f;
            fill.raycastTarget = false;
            return fill;
        }

        public static Slider Slider(Transform parent, string name, float min, float max, float value)
        {
            var rt = Rect(parent, name);
            var s = rt.gameObject.AddComponent<Slider>();
            var track = Panel(rt, "Track", new Color(1, 1, 1, 0.14f), 2f);
            track.rectTransform.anchorMin = new Vector2(0, 0.5f);
            track.rectTransform.anchorMax = new Vector2(1, 0.5f);
            track.rectTransform.sizeDelta = new Vector2(0, 12);
            var fillArea = Rect(rt, "FillArea");
            fillArea.anchorMin = new Vector2(0, 0.5f);
            fillArea.anchorMax = new Vector2(1, 0.5f);
            fillArea.sizeDelta = new Vector2(0, 12);
            var fill = Panel(fillArea, "Fill", Theme.Accent, 2f);
            fill.rectTransform.Stretch();
            var handleArea = Rect(rt, "HandleArea").Stretch(22, 22);
            var handle = Dot(handleArea, "Handle", Color.white, 44);
            handle.raycastTarget = true;
            handle.rectTransform.sizeDelta = new Vector2(44, 0); // slider stretches the handle to its height
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.targetGraphic = handle;
            s.minValue = min;
            s.maxValue = max;
            s.value = value;
            return s;
        }

        public static CanvasGroup Group(Component c)
        {
            var g = c.GetComponent<CanvasGroup>();
            return g ? g : c.gameObject.AddComponent<CanvasGroup>();
        }

        public static IEnumerator Fade(CanvasGroup g, float to, float seconds)
        {
            float from = g.alpha, t = 0;
            while (t < seconds)
            {
                t += Time.unscaledDeltaTime;
                g.alpha = Mathf.Lerp(from, to, Mathf.SmoothStep(0, 1, t / seconds));
                yield return null;
            }
            g.alpha = to;
        }

        public static Canvas Canvas(Transform parent, string name, int sortOrder = 0)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(2400, 1080);
            scaler.matchWidthOrHeight = 1f; // landscape phones: scale by height
            return canvas;
        }

        public static void EnsureEventSystem(Transform parent)
        {
            if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            es.transform.SetParent(parent, false);
        }

        /// <summary>Root that respects the device safe area (notch / rounded corners).</summary>
        public static RectTransform SafeArea(Transform canvas, Rect? safeOverride = null, Vector2? screenOverride = null)
        {
            var rt = Rect(canvas, "SafeArea");
            Rect safe = safeOverride ?? Screen.safeArea;
            Vector2 screen = screenOverride ?? new Vector2(Screen.width, Screen.height);
            if (screen.x <= 0 || screen.y <= 0) return rt.Stretch();
            rt.anchorMin = new Vector2(safe.xMin / screen.x, safe.yMin / screen.y);
            rt.anchorMax = new Vector2(safe.xMax / screen.x, safe.yMax / screen.y);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            return rt;
        }
    }
}
