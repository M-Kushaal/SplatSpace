using System;
using System.Collections.Generic;
using GSCapture.Core.Quality;
using UnityEngine;
using UnityEngine.UI;

namespace GSCapture.UI
{
    /// <summary>
    /// The room check, shown when the user taps Finish on a scene capture: "Checking photos…", then up to five
    /// issues (red = will break the 3D model, amber = will hurt it, grey = tip) with the fix for each and thumbnails
    /// of the flagged photos. Fix now = delete the flagged photos and go back to capturing; Save = the normal finish.
    /// </summary>
    public sealed class CheckSheet
    {
        public readonly RectTransform Root;
        public event Action FixNow, Save, KeepCapturing;

        const int Rows = 5, Thumbs = 6;
        readonly Text title, summary;
        readonly Image[] dots = new Image[Rows];
        readonly Text[] rowTitles = new Text[Rows], rowFixes = new Text[Rows];
        readonly RawImage[] thumbs = new RawImage[Thumbs];
        readonly GameObject thumbRow;
        readonly Button fixButton, saveButton, keepButton;

        public CheckSheet(Transform canvas)
        {
            Root = UIKit.Rect(canvas, "RoomCheck").Stretch();
            Root.gameObject.AddComponent<Image>().color = new Color(0.02f, 0.03f, 0.04f, 0.82f);
            var card = UIKit.Panel(Root, "Card", Theme.Card);
            card.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1560, 900));
            var c = card.transform;

            title = UIKit.Label(c, "Title", "Checking photos…", Theme.TitleSize - 6, Theme.Text, Theme.Bold);
            title.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -56), new Vector2(1400, 64));
            summary = UIKit.Label(c, "Summary", "", Theme.CaptionSize + 2, Theme.TextDim);
            summary.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -120), new Vector2(1400, 40));

            for (int i = 0; i < Rows; i++)
            {
                float y = -180 - i * 80;
                dots[i] = UIKit.Dot(c, $"Dot{i}", Theme.Success, 26);
                dots[i].rectTransform.Place(new Vector2(0, 1), new Vector2(84, y - 10), new Vector2(26, 26));
                rowTitles[i] = UIKit.Label(c, $"Row{i}", "", Theme.BodySize, Theme.Text, Theme.SemiBold);
                rowTitles[i].rectTransform.Place(new Vector2(0, 1), new Vector2(130, y + 4), new Vector2(1350, 44));
                rowFixes[i] = UIKit.Label(c, $"Fix{i}", "", Theme.CaptionSize - 2, Theme.TextDim);
                rowFixes[i].rectTransform.Place(new Vector2(0, 1), new Vector2(130, y - 36), new Vector2(1350, 36));
            }

            thumbRow = UIKit.Rect(c, "Thumbs").gameObject;
            thumbRow.GetComponent<RectTransform>().Stretch();
            for (int i = 0; i < Thumbs; i++)
            {
                var mask = UIKit.Panel(thumbRow.transform, "ThumbMask", Color.white, 0.5f);
                mask.rectTransform.Place(new Vector2(0, 0), new Vector2(70 + i * 240, 186), new Vector2(222, 125));
                mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
                thumbs[i] = UIKit.Rect(mask.transform, "Thumb").Stretch().gameObject.AddComponent<RawImage>();
                thumbs[i].raycastTarget = false;
            }

            fixButton = UIKit.Button(c, "Fix", "Fix now", UIKit.ButtonStyle.Primary, () => FixNow?.Invoke(), 36);
            fixButton.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(70, 50), new Vector2(460, 124));
            saveButton = UIKit.Button(c, "Save", "Save anyway", UIKit.ButtonStyle.Secondary, () => Save?.Invoke());
            saveButton.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(560, 50), new Vector2(380, 124));
            keepButton = UIKit.Button(c, "Keep", "Keep capturing", UIKit.ButtonStyle.Ghost, () => KeepCapturing?.Invoke());
            keepButton.GetComponent<RectTransform>().Place(new Vector2(1, 0), new Vector2(-60, 50), new Vector2(380, 124));
            Root.gameObject.SetActive(false);
        }

        public bool Visible => Root.gameObject.activeSelf;

        public void ShowChecking(int photos)
        {
            Root.gameObject.SetActive(true);
            title.text = "Checking photos…";
            summary.text = $"Making sure all {photos} photos fit together - a few seconds";
            for (int i = 0; i < Rows; i++) SetRow(i, null);
            thumbRow.SetActive(false);
            fixButton.gameObject.SetActive(false);
            saveButton.gameObject.SetActive(false);
            keepButton.gameObject.SetActive(false);
        }

        public void ShowReport(RoomReport rep, IList<string> flaggedPaths)
        {
            Root.gameObject.SetActive(true);
            var worst = rep.Worst;
            title.text = worst == Severity.Bad ? "Fix before you leave the room" :
                         worst == Severity.Warn ? "Almost there" : "Room check passed";
            summary.text = $"{rep.Summary} · checked in {rep.Millis / 1000f:0.0} s";
            for (int i = 0; i < Rows; i++) SetRow(i, i < rep.Issues.Count ? rep.Issues[i] : null);

            thumbRow.SetActive(flaggedPaths.Count > 0);
            for (int i = 0; i < Thumbs; i++)
            {
                var tex = i < flaggedPaths.Count ? Thumbnails.Get(flaggedPaths[i], 320) : null;
                thumbs[i].texture = tex;
                thumbs[i].transform.parent.gameObject.SetActive(tex != null);
            }

            bool fixable = worst >= Severity.Warn;
            fixButton.gameObject.SetActive(fixable);
            saveButton.gameObject.SetActive(true);
            keepButton.gameObject.SetActive(true);
            saveButton.SetLabel(fixable ? "Save anyway" : "Save");
            var saveRt = saveButton.GetComponent<RectTransform>();
            saveRt.anchoredPosition = new Vector2(fixable ? 560 : 70, 50);
            saveButton.GetComponent<Image>().color = fixable ? Theme.GlassLight : Theme.Accent;
            saveButton.GetComponentInChildren<Text>().color = fixable ? Theme.Text : Theme.OnAccent;
        }

        void SetRow(int i, CheckIssue issue)
        {
            bool on = issue != null;
            dots[i].gameObject.SetActive(on);
            rowTitles[i].text = on ? issue.Title : "";
            rowFixes[i].text = on ? issue.Fix : "";
            if (!on) return;
            dots[i].color = issue.Severity == Severity.Bad ? Theme.Danger :
                            issue.Severity == Severity.Warn ? Theme.Warning :
                            issue.Severity == Severity.Good ? Theme.Success : Theme.TextDim;
        }

        public void Hide() => Root.gameObject.SetActive(false);
    }
}
