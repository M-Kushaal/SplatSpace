using System;
using System.Collections.Generic;
using GSCapture.Net;
using UnityEngine;
using UnityEngine.UI;

namespace GSCapture.UI
{
    /// <summary>Start screen: "New capture" + the list of captures on this phone with their sync status.</summary>
    public sealed class HomeScreen
    {
        public readonly RectTransform Root;
        public event Action NewCapture, OpenSettings;
        public event Action<CaptureEntry> Upload, Delete, Export, Download, View;

        readonly RectTransform listContent;
        readonly Text countLabel, emptyLabel, serverLabel;
        readonly Image serverDot;
        readonly Dictionary<string, Row> rows = new Dictionary<string, Row>();

        public HomeScreen(Transform canvas, Transform safe)
        {
            Root = UIKit.Rect(canvas, "Home").Stretch();
            var bg = Root.gameObject.AddComponent<Image>();
            bg.color = Theme.Background;
            var content = UIKit.Rect(Root, "Content");
            content.anchorMin = safe is RectTransform s ? s.anchorMin : Vector2.zero;
            content.anchorMax = safe is RectTransform s2 ? s2.anchorMax : Vector2.one;
            content.offsetMin = content.offsetMax = Vector2.zero;

            // ---- left column: brand, hero action, tips, server
            var title = UIKit.Label(content, "Title", "SplatSpace", Theme.TitleSize, Theme.Text, Theme.Bold);
            title.rectTransform.Place(new Vector2(0, 1), new Vector2(90, -80), new Vector2(760, 80));
            var sub = UIKit.Label(content, "Subtitle", "Scan an object or a whole room. Get a 3D Gaussian splat.",
                Theme.BodySize, Theme.TextDim);
            sub.rectTransform.Place(new Vector2(0, 1), new Vector2(90, -160), new Vector2(760, 44));

            var hero = UIKit.Button(content, "NewCapture", "", UIKit.ButtonStyle.Primary, () => NewCapture?.Invoke());
            hero.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(90, -250), new Vector2(760, 210));
            var heroLabel = hero.GetComponentInChildren<Text>();
            heroLabel.text = "New capture";
            heroLabel.fontSize = 52;
            heroLabel.alignment = TextAnchor.UpperLeft;
            heroLabel.rectTransform.Stretch(52, 40, 42, 40);
            var heroSub = UIKit.Label(hero.transform, "Sub", "Object or scene · guided", Theme.CaptionSize,
                Theme.WithAlpha(Theme.OnAccent, 0.75f), Theme.SemiBold, TextAnchor.LowerLeft);
            heroSub.rectTransform.Stretch(54, 40, 40, 44);
            var plus = UIKit.Label(hero.transform, "Plus", "+", 120, Theme.WithAlpha(Theme.OnAccent, 0.85f), Theme.Bold,
                TextAnchor.MiddleCenter);
            plus.rectTransform.Place(new Vector2(1, 0.5f), new Vector2(-40, 4), new Vector2(140, 160));

            var tips = UIKit.Panel(content, "Tips", Theme.Card);
            tips.rectTransform.Place(new Vector2(0, 1), new Vector2(90, -500), new Vector2(760, 330));
            var tipsTitle = UIKit.Label(tips.transform, "Title", "For a sharp result", Theme.CaptionSize + 2, Theme.TextDim, Theme.SemiBold);
            tipsTitle.rectTransform.Place(new Vector2(0, 1), new Vector2(40, -30), new Vector2(680, 40));
            string[] tipLines =
            {
                "Matte, textured objects work best",
                "Put a patterned cloth or paper under it",
                "Bright, even light",
                "Move slowly and pause at each marker",
            };
            for (int i = 0; i < tipLines.Length; i++)
            {
                var dot = UIKit.Dot(tips.transform, "Bullet", Theme.Accent, 12);
                dot.rectTransform.Place(new Vector2(0, 1), new Vector2(46, -102 - i * 56), new Vector2(12, 12));
                var l = UIKit.Label(tips.transform, "Tip", tipLines[i], Theme.CaptionSize + 2, Theme.Text);
                l.rectTransform.Place(new Vector2(0, 1), new Vector2(78, -88 - i * 56), new Vector2(640, 40));
            }

            serverDot = UIKit.Dot(content, "ServerDot", Theme.TextFaint, 16);
            serverDot.rectTransform.Place(new Vector2(0, 0), new Vector2(96, 78), new Vector2(16, 16));
            serverLabel = UIKit.Label(content, "Server", "", Theme.CaptionSize, Theme.TextDim);
            serverLabel.rectTransform.Place(new Vector2(0, 0), new Vector2(126, 62), new Vector2(520, 48));
            var settings = UIKit.Button(content, "Settings", "Settings", UIKit.ButtonStyle.Secondary, () => OpenSettings?.Invoke(),
                Theme.CaptionSize);
            settings.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(660, 60), new Vector2(190, 76));

            // ---- right column: capture list
            var header = UIKit.Label(content, "ListTitle", "Your captures", Theme.HeadingSize, Theme.Text, Theme.SemiBold);
            header.rectTransform.Place(new Vector2(0, 1), new Vector2(960, -86), new Vector2(700, 60));
            countLabel = UIKit.Label(content, "Count", "", Theme.CaptionSize, Theme.TextDim, null, TextAnchor.MiddleRight);
            countLabel.rectTransform.Place(new Vector2(1, 1), new Vector2(-90, -86), new Vector2(500, 60));

            var scrollRt = UIKit.Rect(content, "List");
            scrollRt.anchorMin = new Vector2(0, 0);
            scrollRt.anchorMax = new Vector2(1, 1);
            scrollRt.offsetMin = new Vector2(960, 40);
            scrollRt.offsetMax = new Vector2(-90, -170);
            var scroll = scrollRt.gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            var viewport = UIKit.Rect(scrollRt, "Viewport").Stretch();
            viewport.gameObject.AddComponent<RectMask2D>();
            viewport.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0); // raycast target for dragging
            listContent = UIKit.Rect(viewport, "Content");
            listContent.anchorMin = new Vector2(0, 1);
            listContent.anchorMax = new Vector2(1, 1);
            listContent.pivot = new Vector2(0.5f, 1);
            listContent.sizeDelta = Vector2.zero;
            var vlg = listContent.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 20;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            listContent.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport;
            scroll.content = listContent;

            emptyLabel = UIKit.Label(content, "Empty", "No captures yet.\nTap “New capture” to scan an object or a room.",
                Theme.BodySize, Theme.TextFaint, null, TextAnchor.MiddleCenter);
            emptyLabel.rectTransform.Place(new Vector2(0.69f, 0.5f), Vector2.zero, new Vector2(1100, 200));
        }

        public void SetServer(string url, bool? reachable)
        {
            serverLabel.text = reachable == true ? $"Connected · {Short(url)}" :
                               reachable == false ? $"Server offline · {Short(url)}" : $"Server · {Short(url)}";
            serverDot.color = reachable == true ? Theme.Success : reachable == false ? Theme.Danger : Theme.TextFaint;
        }

        static string Short(string url) => url.Replace("http://", "").Replace("https://", "");

        public void Refresh(IList<CaptureEntry> entries, Func<string, float> progress)
        {
            var keep = new HashSet<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                keep.Add(e.Id);
                if (!rows.TryGetValue(e.Id, out var row))
                {
                    row = new Row(listContent, this);
                    rows[e.Id] = row;
                }
                row.Root.SetSiblingIndex(i);
                row.Bind(e, progress(e.Id));
            }
            foreach (var id in new List<string>(rows.Keys))
            {
                if (keep.Contains(id)) continue;
                UnityEngine.Object.Destroy(rows[id].Root.gameObject);
                rows.Remove(id);
            }
            int done = 0;
            foreach (var e in entries) if (e.State?.status == "ready") done++;
            countLabel.text = entries.Count == 0 ? "" : $"{entries.Count} on this phone · {done} ready";
            emptyLabel.gameObject.SetActive(entries.Count == 0);
        }

        // ------------------------------------------------------------------ one capture card

        sealed class Row
        {
            public readonly RectTransform Root;
            readonly HomeScreen owner;
            readonly RawImage thumb;
            readonly Text title, meta, detail;
            readonly (Image bg, Image dot, Text label) pill;
            readonly Image barTrack, barFill;
            readonly RectTransform actions;
            string actionsKey, thumbPath;
            CaptureEntry entry;

            public Row(Transform parent, HomeScreen owner)
            {
                this.owner = owner;
                var card = UIKit.Panel(parent, "Capture", Theme.Card);
                Root = card.rectTransform;
                Root.gameObject.AddComponent<LayoutElement>().preferredHeight = 196;

                var mask = UIKit.Panel(Root, "ThumbMask", Color.white, 0.6f);
                mask.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(24, 0), new Vector2(264, 148));
                mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
                thumb = UIKit.Rect(mask.transform, "Thumb").Stretch().gameObject.AddComponent<RawImage>();
                thumb.color = Theme.CardHover;
                thumb.raycastTarget = false;

                title = UIKit.Label(Root, "Title", "", Theme.BodySize, Theme.Text, Theme.SemiBold);
                title.rectTransform.Place(new Vector2(0, 1), new Vector2(316, -26), new Vector2(560, 46));
                meta = UIKit.Label(Root, "Meta", "", Theme.CaptionSize, Theme.TextDim);
                meta.rectTransform.Place(new Vector2(0, 1), new Vector2(316, -76), new Vector2(560, 38));
                pill = UIKit.Pill(Root, "Status", "", Theme.TextDim);
                pill.bg.rectTransform.Place(new Vector2(0, 0), new Vector2(316, 26), new Vector2(300, 52));
                detail = UIKit.Label(Root, "Detail", "", Theme.CaptionSize - 2, Theme.TextDim);
                detail.horizontalOverflow = HorizontalWrapMode.Wrap;
                detail.rectTransform.anchorMin = new Vector2(0, 0);
                detail.rectTransform.anchorMax = new Vector2(1, 0);
                detail.rectTransform.pivot = new Vector2(0, 0);
                detail.rectTransform.offsetMin = new Vector2(636, 22);
                detail.rectTransform.offsetMax = new Vector2(-28, 82);
                detail.alignment = TextAnchor.MiddleRight;

                barTrack = UIKit.Panel(Root, "BarTrack", new Color(1, 1, 1, 0.08f), 2f);
                barTrack.rectTransform.anchorMin = new Vector2(0, 0);
                barTrack.rectTransform.anchorMax = new Vector2(1, 0);
                barTrack.rectTransform.pivot = new Vector2(0, 0);
                barTrack.rectTransform.offsetMin = new Vector2(660, 46);
                barTrack.rectTransform.offsetMax = new Vector2(-28, 58);
                barFill = UIKit.Panel(barTrack.transform, "Fill", Theme.Info, 2f);
                barFill.rectTransform.anchorMin = Vector2.zero;
                barFill.rectTransform.anchorMax = new Vector2(0, 1);
                barFill.rectTransform.offsetMin = barFill.rectTransform.offsetMax = Vector2.zero;

                actions = UIKit.Rect(Root, "Actions");
                actions.Place(new Vector2(1, 1), new Vector2(-24, -24), new Vector2(640, 64));
            }

            public void Bind(CaptureEntry e, float progress)
            {
                entry = e;
                title.text = e.Created.ToLocalTime().ToString("MMM d · HH:mm");
                string kind = e.Complete ? (e.Scene ? "Scene · " : "Object · ") : "";
                meta.text = e.Complete ? $"{kind}{e.Frames} frames · {CaptureLibrary.FormatBytes(e.Bytes)}" : CaptureLibrary.FormatBytes(e.Bytes);
                var (text, color) = StatusView.For(e, progress);
                pill.SetPill(text, color);
                pill.bg.rectTransform.sizeDelta = new Vector2(Mathf.Max(170, 70 + text.Length * 15), 52);

                bool uploading = progress >= 0f;
                barTrack.gameObject.SetActive(uploading);
                if (uploading) barFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(progress), 1);

                var s = e.State ?? new UploadState();
                detail.gameObject.SetActive(!uploading);
                detail.color = s.phase == "failed" || s.status == "invalid" || s.status == "failed" ? Theme.Danger : Theme.TextDim;
                detail.text = !string.IsNullOrEmpty(s.message) ? Truncate(s.message, 90) :
                              s.status == "ready" ? ReadyDetail(s) :
                              s.status == "training" || s.status == "queued" ? "Processing on the server…" : "";

                if (e.ThumbPath != thumbPath)
                {
                    thumbPath = e.ThumbPath;
                    thumb.texture = Thumbnails.Get(thumbPath);
                    thumb.color = thumb.texture ? Color.white : Theme.CardHover;
                }

                string key = ActionsKey(e, uploading);
                if (key == actionsKey) return;
                actionsKey = key;
                foreach (Transform c in actions) UnityEngine.Object.Destroy(c.gameObject);
                var buttons = new List<(string label, UIKit.ButtonStyle style, Action act)>();
                if (!uploading && e.Complete)
                {
                    if (s.status == "ready")
                    {
                        buttons.Add(("View 3D", UIKit.ButtonStyle.Primary, () => owner.View?.Invoke(entry)));
                        buttons.Add(string.IsNullOrEmpty(s.splat_local)
                            ? ("Get file", UIKit.ButtonStyle.Secondary, () => owner.Download?.Invoke(entry))
                            : ("Saved ✓", UIKit.ButtonStyle.Secondary, (Action)null));
                    }
                    else if (s.phase == "local" || s.phase == "failed" || s.phase == "uploading" || s.status == "failed")
                        buttons.Add((s.phase == "local" ? "Upload" : s.phase == "uploading" ? "Resume" : "Retry",
                                     UIKit.ButtonStyle.Primary, () => owner.Upload?.Invoke(entry)));
                    // "Rejected" (status invalid): the data itself failed validation, retrying cannot help.
                    if (s.status != "ready")
                        buttons.Add(("Export", UIKit.ButtonStyle.Secondary, () => owner.Export?.Invoke(entry)));
                }
                if (!uploading) buttons.Add(("Delete", UIKit.ButtonStyle.Danger, () => owner.Delete?.Invoke(entry)));

                float x = 0, h = 64, gap = 12;
                for (int k = buttons.Count - 1; k >= 0; k--) // laid out right to left
                {
                    var (label, style, act) = buttons[k];
                    float w = style == UIKit.ButtonStyle.Primary ? 196 : 150;
                    var b = UIKit.Button(actions, label, label, style, act, Theme.CaptionSize);
                    b.GetComponent<RectTransform>().Place(new Vector2(1, 0.5f), new Vector2(-x, 0), new Vector2(w, h));
                    b.interactable = act != null;
                    x += w + gap;
                }
            }

            static string ActionsKey(CaptureEntry e, bool uploading)
            {
                var s = e.State ?? new UploadState();
                return $"{e.Complete}|{uploading}|{s.phase}|{s.status}|{string.IsNullOrEmpty(s.splat_local)}";
            }

            static string ReadyDetail(UploadState s)
            {
                string q = s.psnr_object > 0 ? $"Quality {s.psnr_object:F1} dB" : "Splat ready";
                return s.train_seconds > 0 ? $"{q} · trained in {Mathf.RoundToInt(s.train_seconds / 60f)} min" : q;
            }

            static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }
    }

    /// <summary>Small cached thumbnails (full camera frames are 1920x1080; cards need ~264x148).</summary>
    public static class Thumbnails
    {
        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string path, int width = 384)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (cache.TryGetValue(path, out var t) && t) return t;
            try
            {
                var full = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!full.LoadImage(System.IO.File.ReadAllBytes(path))) return null;
                int h = Mathf.Max(1, Mathf.RoundToInt(width * (float)full.height / full.width));
                var rt = RenderTexture.GetTemporary(width, h, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(full, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var small = new Texture2D(width, h, TextureFormat.RGB24, false);
                small.ReadPixels(new Rect(0, 0, width, h), 0, 0);
                small.Apply(false, true);
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(full);
                cache[path] = small;
                return small;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GSCapture] thumbnail {path}: {e.Message}");
                return null;
            }
        }

        public static void Forget(string pathPrefix)
        {
            foreach (var k in new List<string>(cache.Keys))
                if (k.StartsWith(pathPrefix)) cache.Remove(k);
        }
    }
}
