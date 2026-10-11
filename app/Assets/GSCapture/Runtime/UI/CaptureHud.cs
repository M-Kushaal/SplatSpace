using System;
using System.Collections;
using GSCapture.Core;
using UnityEngine;
using UnityEngine.UI;

namespace GSCapture.UI
{
    /// <summary>Overlay during a scan: steps, coaching, coverage ring, placement controls.</summary>
    public sealed class CaptureHud
    {
        public readonly RectTransform Root;
        public readonly Slider Radius, Height;
        public event Action Close, StartCapture, Finish;

        readonly MonoBehaviour host;
        readonly (Image bg, Image dot, Text label)[] steps = new (Image, Image, Text)[4];
        readonly Image coachBg, coachDot, flash;
        readonly Text coachText, hintTitle, hintSub, framesText, coverageText, directionText, radiusValue, heightValue;
        readonly RectTransform hint, placement, progressPanel, direction;
        readonly Image ring;
        readonly Button startButton, finishButton;
        Coroutine flashRoutine;

        public static readonly string[] ObjectSteps = { "Surface", "Place", "Capture", "Review" };
        public static readonly string[] SceneSteps = { "Floor", "Start", "Capture", "Review" };
        string[] StepNames = ObjectSteps;

        public CaptureHud(Transform canvas, Transform safe, MonoBehaviour host)
        {
            this.host = host;
            Root = UIKit.Rect(safe, "CaptureHud").Stretch();

            var close = UIKit.Button(Root, "Close", "✕", UIKit.ButtonStyle.Secondary, () => Close?.Invoke(), 36);
            close.GetComponent<RectTransform>().Place(new Vector2(0, 1), new Vector2(40, -36), new Vector2(96, 96));

            // ---- step indicator
            var stepsBar = UIKit.Panel(Root, "Steps", Theme.Glass, 2f);
            stepsBar.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -36), new Vector2(940, 96));
            for (int i = 0; i < 4; i++)
            {
                var p = UIKit.Pill(stepsBar.transform, $"Step{i}", $"{i + 1}  {StepNames[i]}", Theme.TextFaint, Theme.CaptionSize);
                p.bg.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(14 + i * 230, 0), new Vector2(220, 68));
                p.dot.gameObject.SetActive(false);
                p.label.alignment = TextAnchor.MiddleCenter;
                p.label.rectTransform.Stretch(8, 8);
                steps[i] = p;
            }

            // ---- coaching banner
            coachBg = UIKit.Panel(Root, "Coach", Theme.Glass, 2f);
            coachBg.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -156), new Vector2(900, 84));
            coachDot = UIKit.Dot(coachBg.transform, "Dot", Theme.Accent, 22);
            coachDot.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(36, 0), new Vector2(22, 22));
            coachText = UIKit.Label(coachBg.transform, "Text", "", Theme.BodySize + 2, Theme.Text, Theme.SemiBold, TextAnchor.MiddleCenter);
            coachText.rectTransform.Stretch(70, 40);

            // ---- centred hint card (scanning / placing)
            var hintBg = UIKit.Panel(Root, "Hint", Theme.Glass);
            hint = hintBg.rectTransform;
            hint.Place(new Vector2(0.5f, 0.5f), new Vector2(0, -60), new Vector2(1240, 220));
            hintTitle = UIKit.Label(hint, "Title", "", Theme.HeadingSize, Theme.Text, Theme.SemiBold, TextAnchor.MiddleCenter);
            hintTitle.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -40), new Vector2(1160, 60));
            hintSub = UIKit.Label(hint, "Sub", "", Theme.CaptionSize + 2, Theme.TextDim, null, TextAnchor.UpperCenter);
            hintSub.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -112), new Vector2(1160, 90));

            // ---- placement card
            var placeBg = UIKit.Panel(Root, "Placement", Theme.Glass);
            placement = placeBg.rectTransform;
            placement.Place(new Vector2(0, 0), new Vector2(40, 40), new Vector2(640, 330));
            var pTitle = UIKit.Label(placement, "Title", "Object size", Theme.CaptionSize + 2, Theme.TextDim, Theme.SemiBold);
            pTitle.rectTransform.Place(new Vector2(0, 1), new Vector2(40, -30), new Vector2(560, 40));
            (Radius, radiusValue) = LabeledSlider(placement, "Radius", -96, 0.03f, 1.0f, 0.12f);
            (Height, heightValue) = LabeledSlider(placement, "Height", -206, 0.03f, 2.0f, 0.25f);
            Radius.onValueChanged.AddListener(v => radiusValue.text = Cm(v));
            Height.onValueChanged.AddListener(v => heightValue.text = Cm(v));
            radiusValue.text = Cm(Radius.value);
            heightValue.text = Cm(Height.value);

            startButton = UIKit.Button(Root, "Start", "Start capture", UIKit.ButtonStyle.Primary, () => StartCapture?.Invoke(), 36);
            startButton.GetComponent<RectTransform>().Place(new Vector2(1, 0), new Vector2(-40, 40), new Vector2(420, 120));

            // ---- capture progress (right side)
            var prog = UIKit.Panel(Root, "Progress", Theme.Glass);
            progressPanel = prog.rectTransform;
            progressPanel.Place(new Vector2(1, 0), new Vector2(-40, 40), new Vector2(420, 560));
            ring = UIKit.ProgressRing(progressPanel, "Coverage", 250, Theme.Accent);
            ring.transform.parent.GetComponent<RectTransform>().Place(new Vector2(0.5f, 1), new Vector2(0, -40), new Vector2(250, 250));
            framesText = UIKit.Label(ring.transform.parent, "Frames", "0", 76, Theme.Text, Theme.Bold, TextAnchor.MiddleCenter);
            framesText.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, 14), new Vector2(240, 100));
            var framesCaption = UIKit.Label(ring.transform.parent, "Caption", "frames", Theme.CaptionSize, Theme.TextDim, null,
                TextAnchor.MiddleCenter);
            framesCaption.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, -50), new Vector2(240, 40));
            coverageText = UIKit.Label(progressPanel, "Coverage", "0% coverage", Theme.CaptionSize + 2, Theme.TextDim, Theme.SemiBold,
                TextAnchor.MiddleCenter);
            coverageText.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -306), new Vector2(380, 44));
            finishButton = UIKit.Button(progressPanel, "Finish", "Finish", UIKit.ButtonStyle.Primary, () => Finish?.Invoke(), 36);
            finishButton.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0), new Vector2(0, 36), new Vector2(340, 112));

            // ---- next-move hint (bottom left)
            var dirBg = UIKit.Panel(Root, "Direction", Theme.Glass, 2f);
            direction = dirBg.rectTransform;
            direction.Place(new Vector2(0, 0), new Vector2(40, 40), new Vector2(820, 92));
            directionText = UIKit.Label(direction, "Text", "", Theme.BodySize, Theme.Text, Theme.SemiBold);
            directionText.rectTransform.Stretch(40, 40);

            // ---- shutter flash (full screen, on the canvas, not the safe area)
            flash = UIKit.Rect(canvas, "Flash").Stretch().gameObject.AddComponent<Image>();
            flash.color = new Color(1, 1, 1, 0);
            flash.raycastTarget = false;
        }

        (Slider, Text) LabeledSlider(RectTransform parent, string name, float y, float min, float max, float value)
        {
            var label = UIKit.Label(parent, name + "Label", name, Theme.BodySize, Theme.Text, Theme.SemiBold);
            label.rectTransform.Place(new Vector2(0, 1), new Vector2(40, y), new Vector2(300, 44));
            var val = UIKit.Label(parent, name + "Value", "", Theme.BodySize, Theme.Accent, Theme.SemiBold, TextAnchor.MiddleRight);
            val.rectTransform.Place(new Vector2(1, 1), new Vector2(-40, y), new Vector2(200, 44));
            var s = UIKit.Slider(parent, name, min, max, value);
            s.GetComponent<RectTransform>().Place(new Vector2(0.5f, 1), new Vector2(0, y - 56), new Vector2(560, 44));
            return (s, val);
        }

        static string Cm(float m) => m >= 1f ? $"{m:F2} m" : $"{Mathf.RoundToInt(m * 100)} cm";

        // ------------------------------------------------------------------ state

        /// <summary>Object and scene captures label the four steps differently.</summary>
        public void SetStepNames(string[] names) => StepNames = names;

        public void SetStep(int active)
        {
            for (int i = 0; i < steps.Length; i++)
            {
                Color c = i < active ? Theme.Success : i == active ? Theme.Accent : Theme.TextFaint;
                steps[i].SetPill(i < active ? $"✓  {StepNames[i]}" : $"{i + 1}  {StepNames[i]}", c);
                if (i > active) steps[i].bg.color = new Color(1, 1, 1, 0.04f);
            }
        }

        public void SetHint(string title, string sub = "")
        {
            hint.gameObject.SetActive(!string.IsNullOrEmpty(title));
            hintTitle.text = title;
            hintSub.text = sub;
        }

        public void SetCoach(string text, Color color)
        {
            coachBg.gameObject.SetActive(!string.IsNullOrEmpty(text));
            coachText.text = text;
            coachText.horizontalOverflow = HorizontalWrapMode.Overflow;
            float w = string.IsNullOrEmpty(text) ? 0 : coachText.preferredWidth;
            coachBg.rectTransform.sizeDelta = new Vector2(Mathf.Clamp(w + 170, 520, 1500), 84);
            coachDot.color = color;
            coachBg.color = Color.Lerp(Theme.Glass, Theme.WithAlpha(color, 0.9f), 0.18f);
        }

        public static Color CoachColor(GateReason r)
        {
            switch (r)
            {
                case GateReason.Ok: return Theme.Success;
                case GateReason.NotTracking:
                case GateReason.TooDark: return Theme.Danger;
                case GateReason.MovingTooFast:
                case GateReason.Settling:
                case GateReason.Blurry: return Theme.Warning;
                default: return Theme.Info;
            }
        }

        public void SetPlacement(bool visible, bool canStart)
        {
            placement.gameObject.SetActive(visible);
            startButton.gameObject.SetActive(visible);
            startButton.interactable = canStart;
            startButton.SetLabel("Start capture");
        }

        /// <summary>Start button without the object-size card (scene captures).</summary>
        public void SetStartOnly(bool visible, bool canStart, string label)
        {
            placement.gameObject.SetActive(false);
            startButton.gameObject.SetActive(visible);
            startButton.interactable = canStart;
            startButton.SetLabel(label);
        }

        public void SetCapturing(bool visible)
        {
            progressPanel.gameObject.SetActive(visible);
            direction.gameObject.SetActive(visible);
        }

        public void SetProgress(int frames, float coverage, bool canFinish, string next, bool saving,
                                string coverageLabel = "coverage", string finishBlockedLabel = null)
        {
            framesText.text = frames.ToString();
            ring.fillAmount = coverage;
            ring.color = coverage >= 0.999f ? Theme.Success : Theme.Accent;
            coverageText.text = saving ? "saving…" : $"{Mathf.RoundToInt(coverage * 100)}% {coverageLabel}";
            finishButton.interactable = canFinish;
            finishButton.SetLabel(canFinish ? "Finish" : finishBlockedLabel ?? $"Finish ({Mathf.Max(0, 20 - frames)} more)");
            directionText.text = next;
        }

        public void Flash()
        {
            if (flashRoutine != null) host.StopCoroutine(flashRoutine);
            flashRoutine = host.StartCoroutine(FlashRoutine());
        }

        IEnumerator FlashRoutine()
        {
            for (float t = 0; t < 0.22f; t += Time.unscaledDeltaTime)
            {
                flash.color = new Color(1, 1, 1, 0.35f * (1f - t / 0.22f));
                yield return null;
            }
            flash.color = new Color(1, 1, 1, 0);
        }

        public void Show(bool visible) => Root.gameObject.SetActive(visible);
    }
}
