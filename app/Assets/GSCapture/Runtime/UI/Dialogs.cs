using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace GSCapture.UI
{
    /// <summary>"Capture complete" summary with stats, a thumbnail strip and next actions.</summary>
    public sealed class ReviewScreen
    {
        public readonly RectTransform Root;
        public event Action Upload, Export, Discard, Done;

        readonly Text subtitle;
        readonly Text[] statValues = new Text[4];
        readonly RawImage[] thumbs = new RawImage[6];

        static readonly string[] StatNames = { "Frames", "Coverage", "Duration", "AR points" };

        public ReviewScreen(Transform canvas)
        {
            Root = UIKit.Rect(canvas, "Review").Stretch();
            Root.gameObject.AddComponent<Image>().color = new Color(0.02f, 0.03f, 0.04f, 0.82f);
            var card = UIKit.Panel(Root, "Card", Theme.Card);
            card.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1560, 740));
            var c = card.transform;

            var check = UIKit.Dot(c, "Check", Theme.WithAlpha(Theme.Success, 0.18f), 96);
            check.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -64), new Vector2(96, 96));
            var tick = UIKit.Label(check.transform, "Tick", "✓", 52, Theme.Success, Theme.Bold, TextAnchor.MiddleCenter);
            tick.rectTransform.Stretch();
            var title = UIKit.Label(c, "Title", "Capture complete", Theme.TitleSize - 6, Theme.Text, Theme.Bold);
            title.rectTransform.Place(new Vector2(0, 1), new Vector2(200, -60), new Vector2(1200, 64));
            subtitle = UIKit.Label(c, "Subtitle", "", Theme.CaptionSize + 2, Theme.TextDim);
            subtitle.rectTransform.Place(new Vector2(0, 1), new Vector2(200, -126), new Vector2(1250, 40));

            for (int i = 0; i < 4; i++)
            {
                var tile = UIKit.Panel(c, StatNames[i], Theme.CardHover);
                tile.rectTransform.Place(new Vector2(0, 1), new Vector2(70 + i * 360, -210), new Vector2(340, 150));
                statValues[i] = UIKit.Label(tile.transform, "Value", "", 54, Theme.Text, Theme.Bold);
                statValues[i].rectTransform.Place(new Vector2(0, 1), new Vector2(32, -22), new Vector2(290, 70));
                var name = UIKit.Label(tile.transform, "Name", StatNames[i], Theme.CaptionSize, Theme.TextDim);
                name.rectTransform.Place(new Vector2(0, 0), new Vector2(32, 20), new Vector2(290, 40));
            }

            for (int i = 0; i < 6; i++)
            {
                var mask = UIKit.Panel(c, "ThumbMask", Color.white, 0.5f);
                mask.rectTransform.Place(new Vector2(0, 1), new Vector2(70 + i * 240, -400), new Vector2(222, 125));
                mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
                thumbs[i] = UIKit.Rect(mask.transform, "Thumb").Stretch().gameObject.AddComponent<RawImage>();
                thumbs[i].raycastTarget = false;
            }

            var up = UIKit.Button(c, "Upload", "Upload & process", UIKit.ButtonStyle.Primary, () => Upload?.Invoke(), 36);
            up.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(70, 64), new Vector2(520, 124));
            var exp = UIKit.Button(c, "Export", "Export .zip", UIKit.ButtonStyle.Secondary, () => Export?.Invoke());
            exp.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(620, 64), new Vector2(300, 124));
            var del = UIKit.Button(c, "Discard", "Discard", UIKit.ButtonStyle.Danger, () => Discard?.Invoke());
            del.GetComponent<RectTransform>().Place(new Vector2(1, 0), new Vector2(-300, 64), new Vector2(220, 124));
            var done = UIKit.Button(c, "Done", "Later", UIKit.ButtonStyle.Ghost, () => Done?.Invoke());
            done.GetComponent<RectTransform>().Place(new Vector2(1, 0), new Vector2(-60, 64), new Vector2(220, 124));
        }

        public void Show(int frames, float coverage, TimeSpan duration, int points, string sizeText, IList<string> thumbPaths)
        {
            Root.gameObject.SetActive(true);
            subtitle.text = $"Saved on this phone · {sizeText}. Upload it to build the 3D splat.";
            statValues[0].text = frames.ToString();
            statValues[1].text = $"{Mathf.RoundToInt(coverage * 100)}%";
            statValues[2].text = $"{(int)duration.TotalMinutes}:{duration.Seconds:D2}";
            statValues[3].text = points >= 1000 ? $"{points / 1000f:F1}k" : points.ToString();
            for (int i = 0; i < thumbs.Length; i++)
            {
                var tex = i < thumbPaths.Count ? Thumbnails.Get(thumbPaths[i], 320) : null;
                thumbs[i].texture = tex;
                thumbs[i].color = tex ? Color.white : Theme.CardHover;
            }
        }

        public void Hide() => Root.gameObject.SetActive(false);
    }

    /// <summary>Server address + connection test + device info.</summary>
    public sealed class SettingsScreen
    {
        public readonly RectTransform Root;
        public event Action<string> Save, Test;
        public event Action Closed;

        readonly InputField input;
        readonly Text status, device;

        public SettingsScreen(Transform canvas)
        {
            Root = UIKit.Rect(canvas, "Settings").Stretch();
            Root.gameObject.AddComponent<Image>().color = new Color(0.02f, 0.03f, 0.04f, 0.82f);
            var card = UIKit.Panel(Root, "Card", Theme.Card);
            card.rectTransform.Place(new Vector2(0.5f, 0.6f), Vector2.zero, new Vector2(1240, 700));
            var c = card.transform;

            var title = UIKit.Label(c, "Title", "Settings", Theme.TitleSize - 8, Theme.Text, Theme.Bold);
            title.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -56), new Vector2(800, 64));
            var lbl = UIKit.Label(c, "Label", "Processing server", Theme.CaptionSize + 2, Theme.TextDim, Theme.SemiBold);
            lbl.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -150), new Vector2(800, 40));

            var field = UIKit.Panel(c, "Field", Theme.GlassLight);
            field.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -200), new Vector2(1100, 104));
            input = field.gameObject.AddComponent<InputField>();
            var text = UIKit.Label(field.transform, "Text", "", Theme.BodySize + 2, Theme.Text);
            text.rectTransform.Stretch(34, 34);
            text.raycastTarget = true;
            text.supportRichText = false;
            var ph = UIKit.Label(field.transform, "Placeholder", "http://192.168.x.x:8000", Theme.BodySize + 2, Theme.TextFaint);
            ph.rectTransform.Stretch(34, 34);
            input.textComponent = text;
            input.placeholder = ph;
            input.contentType = InputField.ContentType.Standard;
            input.lineType = InputField.LineType.SingleLine;
            input.keyboardType = TouchScreenKeyboardType.URL;
            input.caretColor = Theme.Accent;
            input.selectionColor = Theme.WithAlpha(Theme.Accent, 0.35f);

            var hint = UIKit.Label(c, "Hint", "Run backend/scripts/run_dev.ps1 on your PC and type the address it prints. Phone and PC must be on the same Wi-Fi.",
                Theme.CaptionSize, Theme.TextDim);
            hint.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -326), new Vector2(1100, 80));

            status = UIKit.Label(c, "Status", "", Theme.CaptionSize + 2, Theme.TextDim, Theme.SemiBold);
            status.rectTransform.Place(new Vector2(0, 0), new Vector2(70, 236), new Vector2(1100, 44));
            device = UIKit.Label(c, "Device", "", Theme.CaptionSize - 2, Theme.TextFaint);
            device.rectTransform.Place(new Vector2(0, 0), new Vector2(70, 190), new Vector2(1100, 36));

            var test = UIKit.Button(c, "Test", "Test connection", UIKit.ButtonStyle.Secondary, () => Test?.Invoke(input.text));
            test.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(70, 40), new Vector2(360, 96));
            var save = UIKit.Button(c, "Save", "Save", UIKit.ButtonStyle.Primary, () => Save?.Invoke(input.text));
            save.GetComponent<RectTransform>().Place(new Vector2(1, 0), new Vector2(-300, 40), new Vector2(260, 96));
            var close = UIKit.Button(c, "Close", "Close", UIKit.ButtonStyle.Ghost, () => Closed?.Invoke());
            close.GetComponent<RectTransform>().Place(new Vector2(1, 0), new Vector2(-60, 40), new Vector2(220, 96));
        }

        public void Show(string server, string deviceInfo)
        {
            Root.gameObject.SetActive(true);
            input.text = server;
            device.text = deviceInfo;
            SetStatus("", Theme.TextDim);
        }

        public void SetStatus(string text, Color color)
        {
            status.text = text;
            status.color = color;
        }

        public void Hide() => Root.gameObject.SetActive(false);
    }

    /// <summary>Short bottom message.</summary>
    public sealed class Toast
    {
        readonly RectTransform root;
        readonly Text label;
        readonly Image dot;
        readonly CanvasGroup group;
        readonly MonoBehaviour host;
        Coroutine routine;

        public Toast(Transform canvas, MonoBehaviour host)
        {
            this.host = host;
            var bg = UIKit.Panel(canvas, "Toast", Theme.GlassLight, 2f);
            root = bg.rectTransform;
            root.Place(new Vector2(0.5f, 0), new Vector2(0, 60), new Vector2(1000, 96));
            bg.raycastTarget = false;
            dot = UIKit.Dot(root, "Dot", Theme.Accent, 20);
            dot.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(40, 0), new Vector2(20, 20));
            label = UIKit.Label(root, "Text", "", Theme.BodySize - 2, Theme.Text, Theme.SemiBold, TextAnchor.MiddleCenter);
            label.rectTransform.Stretch(76, 40);
            group = UIKit.Group(bg);
            group.alpha = 0;
            group.blocksRaycasts = false;
        }

        public void Show(string text, Color color, float seconds = 3f)
        {
            label.text = text;
            dot.color = color;
            root.sizeDelta = new Vector2(Mathf.Clamp(140 + text.Length * 17, 500, 1900), 96);
            root.SetAsLastSibling();
            if (routine != null) host.StopCoroutine(routine);
            routine = host.StartCoroutine(Run(seconds));
        }

        IEnumerator Run(float seconds)
        {
            yield return UIKit.Fade(group, 1, 0.15f);
            yield return new WaitForSecondsRealtime(seconds);
            yield return UIKit.Fade(group, 0, 0.3f);
        }
    }

    /// <summary>"New capture" chooser: the two capture types the app offers.</summary>
    public sealed class CaptureTypeSheet
    {
        public readonly RectTransform Root;
        public event Action ObjectChosen, SceneChosen, Cancelled;

        public CaptureTypeSheet(Transform canvas)
        {
            Root = UIKit.Rect(canvas, "CaptureType").Stretch();
            var dim = Root.gameObject.AddComponent<Image>();
            dim.color = new Color(0.02f, 0.03f, 0.04f, 0.95f);
            var dimButton = Root.gameObject.AddComponent<Button>(); // tap outside the cards = cancel
            dimButton.transition = Selectable.Transition.None;
            dimButton.onClick.AddListener(() => Cancelled?.Invoke());

            var title = UIKit.Label(Root, "Title", "What do you want to capture?", Theme.TitleSize - 6, Theme.Text, Theme.Bold,
                TextAnchor.MiddleCenter);
            title.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, 380), new Vector2(1600, 80));

            Card("Object", "Object capture", "One thing on a table or the floor.\nWalk around it while the app shows you where to stand.",
                "about 5 min · 60–100 photos", new Vector2(-400, -10), () => ObjectChosen?.Invoke(), ObjectIcon);
            Card("Scene", "Scene capture", "A whole room or space.\nTurn with the phone at arm's length at 5–8 spots (level, down, up), then walk the walls.",
                "about 10 min · 400–600 photos", new Vector2(400, -10), () => SceneChosen?.Invoke(), SceneIcon);

            var cancel = UIKit.Button(Root, "Cancel", "Cancel", UIKit.ButtonStyle.Ghost, () => Cancelled?.Invoke());
            cancel.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0.5f), new Vector2(0, -400), new Vector2(300, 100));
            Root.gameObject.SetActive(false);
        }

        void Card(string name, string heading, string body, string meta, Vector2 offset, Action onClick, Action<Transform> icon)
        {
            var b = UIKit.Button(Root, name, "", UIKit.ButtonStyle.Secondary, onClick);
            b.GetComponent<Image>().color = Theme.Card;
            var rt = b.GetComponent<RectTransform>();
            rt.Place(new Vector2(0.5f, 0.5f), offset, new Vector2(740, 560));
            var badge = UIKit.Dot(rt, "Icon", Theme.WithAlpha(Theme.Accent, 0.16f), 150);
            badge.rectTransform.Place(new Vector2(0, 1), new Vector2(60, -60), new Vector2(150, 150));
            icon(badge.transform);
            var h = UIKit.Label(rt, "Heading", heading, Theme.HeadingSize + 6, Theme.Text, Theme.Bold);
            h.rectTransform.Place(new Vector2(0, 1), new Vector2(60, -250), new Vector2(640, 64));
            var t = UIKit.Label(rt, "Body", body, Theme.CaptionSize + 2, Theme.TextDim);
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.rectTransform.Place(new Vector2(0, 1), new Vector2(60, -330), new Vector2(630, 140));
            var m = UIKit.Label(rt, "Meta", meta, Theme.CaptionSize, Theme.Accent, Theme.SemiBold);
            m.rectTransform.Place(new Vector2(0, 0), new Vector2(60, 50), new Vector2(640, 44));
        }

        /// <summary>A small object on a dotted orbit.</summary>
        static void ObjectIcon(Transform parent)
        {
            var orbit = UIKit.Rect(parent, "Orbit");
            orbit.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(104, 104));
            var ring = orbit.gameObject.AddComponent<Image>();
            ring.sprite = UIKit.Ring;
            ring.color = Theme.WithAlpha(Theme.Accent, 0.55f);
            ring.raycastTarget = false;
            var cube = UIKit.Panel(parent, "Object", Theme.Accent, 0.35f);
            cube.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(40, 40));
            cube.raycastTarget = false;
            var cam = UIKit.Dot(parent, "Camera", Theme.Text, 18);
            cam.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(37, 37), new Vector2(18, 18));
        }

        /// <summary>A room outline with a few spots.</summary>
        static void SceneIcon(Transform parent)
        {
            var room = UIKit.Panel(parent, "Room", Theme.WithAlpha(Theme.Accent, 0.55f), 0.25f);
            room.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(96, 76));
            room.raycastTarget = false;
            var inner = UIKit.Panel(room.transform, "Inner", Theme.Card, 0.2f);
            inner.rectTransform.Stretch(6, 6, 6, 6);
            inner.raycastTarget = false;
            Vector2[] spots = { new Vector2(-24, -14), new Vector2(22, -14), new Vector2(0, 14) };
            foreach (var s in spots)
            {
                var d = UIKit.Dot(inner.transform, "Spot", Theme.Accent, 14);
                d.rectTransform.Place(new Vector2(0.5f, 0.5f), s, new Vector2(14, 14));
            }
        }

        public void Show() => Root.gameObject.SetActive(true);

        public void Hide() => Root.gameObject.SetActive(false);
    }
}
