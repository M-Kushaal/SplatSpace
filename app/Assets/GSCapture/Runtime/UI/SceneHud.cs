using System;
using System.Collections.Generic;
using GSCapture.Core;
using UnityEngine;
using UnityEngine.UI;

namespace GSCapture.UI
{
    /// <summary>
    /// Scene (room) capture overlay, on top of the shared CaptureHud: a heading-up 360° compass with the
    /// spot's sectors (outer = tilted up at the ceiling, middle = phone level, inner = tilted down), spot
    /// buttons, and a top-down mini-map that shades the parts of the room no spot is close to yet.
    /// </summary>
    public sealed class SceneHud
    {
        public readonly RectTransform Root;
        public event Action NextSpot, StartSpot, ToggleCloseups, ContinueHere, ManualShot;

        const int MapPx = 160;
        readonly RectTransform compassSpin;
        readonly Image[,] segments;
        readonly Text centerTitle, centerSub;
        readonly Button spotButton, closeupButton;
        readonly GameObject wallsIntro;
        readonly Button continueButton;
        bool wallsIntroSeen;
        readonly RawImage map;
        readonly Texture2D mapTex;
        readonly Color32[] mapPixels = new Color32[MapPx * MapPx];
        ScenePhase phase;

        public SceneHud(Transform safe, int sectors)
        {
            Root = UIKit.Rect(safe, "SceneHud").Stretch();

            // ---- compass (bottom centre)
            var bg = UIKit.Dot(Root, "Compass", Theme.Glass, 380);
            bg.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0, 30), new Vector2(380, 380));
            bg.raycastTarget = false;
            compassSpin = UIKit.Rect(bg.transform, "Spin");
            compassSpin.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(360, 360));
            segments = new Image[3, sectors];
            float[] sizes = { 300f, 240f, 360f }; // SceneBand order: level, down, up
            for (int band = 0; band < 3; band++)
                for (int s = 0; s < sectors; s++)
                {
                    var rt = UIKit.Rect(compassSpin, $"B{band}S{s}");
                    rt.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(sizes[band], sizes[band]));
                    float width = 360f / sectors, gap = 2.5f;
                    rt.localEulerAngles = new Vector3(0, 0, -(s * width + gap * 0.5f)); // clockwise from the top
                    var img = rt.gameObject.AddComponent<Image>();
                    img.sprite = UIKit.Ring;
                    img.type = Image.Type.Filled;
                    img.fillMethod = Image.FillMethod.Radial360;
                    img.fillOrigin = (int)Image.Origin360.Top;
                    img.fillClockwise = true;
                    img.fillAmount = (width - gap) / 360f;
                    img.raycastTarget = false;
                    segments[band, s] = img;
                }
            var pointer = UIKit.Dot(bg.transform, "Pointer", Theme.Text, 22);
            pointer.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0, -2), new Vector2(22, 22));
            centerTitle = UIKit.Label(bg.transform, "Title", "", Theme.HeadingSize, Theme.Text, Theme.Bold, TextAnchor.MiddleCenter);
            centerTitle.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, 18), new Vector2(200, 56));
            centerSub = UIKit.Label(bg.transform, "Sub", "", Theme.CaptionSize - 4, Theme.TextDim, Theme.SemiBold, TextAnchor.MiddleCenter);
            centerSub.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0, -28), new Vector2(200, 40));

            // ---- spot buttons (bottom left, above the shared hint panel)
            spotButton = UIKit.Button(Root, "Spot", "Next spot", UIKit.ButtonStyle.Secondary, () =>
            {
                if (phase == ScenePhase.Spin) NextSpot?.Invoke();
                else StartSpot?.Invoke();
            }, Theme.CaptionSize + 2);
            spotButton.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(40, 150), new Vector2(330, 100));
            closeupButton = UIKit.Button(Root, "Closeups", "Walls pass", UIKit.ButtonStyle.Secondary, OnWallsButton,
                Theme.CaptionSize + 2);
            closeupButton.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(390, 150), new Vector2(300, 100));

            // ---- mini-map (left, middle)
            var mapBg = UIKit.Panel(Root, "Map", Theme.Glass);
            mapBg.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(40, 20), new Vector2(340, 380));
            var mapTitle = UIKit.Label(mapBg.transform, "Title", "Room map", Theme.CaptionSize - 2, Theme.TextDim, Theme.SemiBold);
            mapTitle.rectTransform.Place(new Vector2(0, 1), new Vector2(28, -14), new Vector2(290, 36));
            mapTex = new Texture2D(MapPx, MapPx, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            map = UIKit.Rect(mapBg.transform, "Image").gameObject.AddComponent<RawImage>();
            map.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0, 20), new Vector2(300, 300));
            map.texture = mapTex;
            map.raycastTarget = false;
            SetMap(null, null, Vector2.zero, 0f, 0f, null, null);
            wallsIntro = BuildWallsIntro();
            continueButton = UIKit.Button(Root, "ContinueHere", "Continue here", UIKit.ButtonStyle.Primary,
                () => ContinueHere?.Invoke(), Theme.CaptionSize + 2);
            continueButton.GetComponent<RectTransform>().Place(new Vector2(0.5f, 0), new Vector2(0, 440), new Vector2(420, 100));
            continueButton.gameObject.SetActive(false);

            // manual shutter (right side): always takes the photo now, for when the automatic checks are too picky
            var shutter = UIKit.Dot(Root, "Shutter", Color.white, 150);
            shutter.rectTransform.Place(new Vector2(1, 0.5f), new Vector2(-120, -60), new Vector2(150, 150));
            var inner = UIKit.Dot(shutter.transform, "Inner", Theme.Accent, 118);
            inner.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(118, 118));
            inner.raycastTarget = false;
            shutter.gameObject.AddComponent<Button>().onClick.AddListener(() => ManualShot?.Invoke());
            var lbl = UIKit.Label(Root, "ShutterLabel", "Take photo", Theme.CaptionSize - 4, Theme.TextDim, Theme.SemiBold,
                TextAnchor.MiddleCenter);
            lbl.rectTransform.Place(new Vector2(1, 0.5f), new Vector2(-120, -160), new Vector2(220, 36));
        }

        /// <summary>Offered when a position jump can't be confirmed: carry on as a new spot.</summary>
        public void ShowContinue(bool visible)
        {
            if (continueButton.gameObject.activeSelf != visible) continueButton.gameObject.SetActive(visible);
        }

        /// <summary>The first tap on Walls pass explains the step before starting it.</summary>
        GameObject BuildWallsIntro()
        {
            var dim = UIKit.Rect(Root, "WallsIntro").Stretch();
            dim.gameObject.AddComponent<Image>().color = new Color(0.02f, 0.03f, 0.04f, 0.82f);
            var card = UIKit.Panel(dim, "Card", Theme.Card);
            card.rectTransform.Place(new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400, 760));
            var c = card.transform;
            var title = UIKit.Label(c, "Title", "Last step: the walls pass", Theme.TitleSize - 6, Theme.Text, Theme.Bold);
            title.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -56), new Vector2(1260, 64));
            var why = UIKit.Label(c, "Why", "Your spots are done. From the middle of the room, corners, the tops of the walls and the " +
                "ceiling edge are far away and seen at a slant - that's where splats get holes and \"fur\". This step fixes it.",
                Theme.CaptionSize + 2, Theme.TextDim);
            why.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -150), new Vector2(1260, 100));
            string[] steps =
            {
                "1   Stand about 1 m from a wall and face it, phone level at chest height.",
                "2   Walk slowly sideways along the walls, all the way round - corners too. Photos are taken as you move.",
                "3   Then go round once more with the phone tilted up to where the wall meets the ceiling.",
            };
            for (int i = 0; i < steps.Length; i++)
            {
                var l = UIKit.Label(c, $"Step{i}", steps[i], Theme.BodySize, Theme.Text, Theme.SemiBold);
                l.rectTransform.Place(new Vector2(0, 1), new Vector2(70, -290 - i * 96), new Vector2(1260, 84));
            }
            var start = UIKit.Button(c, "Start", "Start walls pass", UIKit.ButtonStyle.Primary, () =>
            {
                wallsIntroSeen = true;
                wallsIntro.SetActive(false);
                ToggleCloseups?.Invoke();
            }, 36);
            start.GetComponent<RectTransform>().Place(new Vector2(0, 0), new Vector2(70, 50), new Vector2(520, 124));
            var later = UIKit.Button(c, "Later", "Not yet", UIKit.ButtonStyle.Ghost, () => wallsIntro.SetActive(false));
            later.GetComponent<RectTransform>().Place(new Vector2(1, 0), new Vector2(-60, 50), new Vector2(300, 124));
            dim.gameObject.SetActive(false);
            return dim.gameObject;
        }

        void OnWallsButton()
        {
            if (phase != ScenePhase.Closeups && !wallsIntroSeen) wallsIntro.SetActive(true);
            else ToggleCloseups?.Invoke();
        }

        public void Show(bool visible)
        {
            Root.gameObject.SetActive(visible);
            if (visible)
            {
                wallsIntroSeen = false; // explain it again on every new room
                wallsIntro.SetActive(false);
            }
        }

        /// <summary>Compass: rotate so the direction the phone faces is at the top; colour the spot's sectors.</summary>
        public void SetCompass(float headingDeg, Station station, int targetBand, int targetSector, int currentBand,
                               string title, string sub)
        {
            compassSpin.localEulerAngles = new Vector3(0, 0, headingDeg);
            float pulse = 0.6f + 0.4f * Mathf.Sin(Time.time * 6f);
            for (int b = 0; b < 3; b++)
                for (int s = 0; s < segments.GetLength(1); s++)
                {
                    Color c;
                    if (station != null && station.Filled[b, s]) c = Theme.WithAlpha(Theme.Success, 0.9f);
                    else if (b == targetBand && s == targetSector) c = Theme.WithAlpha(Theme.Warning, pulse);
                    else c = new Color(1, 1, 1, b == currentBand ? 0.28f : 0.12f);
                    if (station == null) c = new Color(1, 1, 1, 0.08f);
                    segments[b, s].color = c;
                }
            centerTitle.text = title;
            centerSub.text = sub;
        }

        /// <summary>
        /// Spots first, then the walls pass as its own step: the Walls pass button appears once enough spots are
        /// done (wallsReady); when the room map is covered, Next spot / Start spot disappear and Walls pass is the
        /// highlighted next action. During the walls pass the only button goes back to spots.
        /// </summary>
        public void SetButtons(ScenePhase p, bool canStartSpot, bool spotDone, int resumeSpot = -1, bool wallsReady = false,
                               bool roomCovered = false)
        {
            phase = p;
            bool walls = p == ScenePhase.Closeups;
            bool spotVisible = !walls && !(roomCovered && (p != ScenePhase.Spin || spotDone) && resumeSpot < 0);
            bool wallsVisible = walls || wallsReady;
            spotButton.gameObject.SetActive(spotVisible);
            closeupButton.gameObject.SetActive(wallsVisible);
            closeupButton.GetComponent<RectTransform>().anchoredPosition = new Vector2(spotVisible ? 390 : 40, 150);
            bool wallsNext = !walls && roomCovered;
            closeupButton.SetLabel(walls ? "← Back to spots" : wallsNext ? "Walls pass  →" : "Walls pass");
            closeupButton.GetComponent<Image>().color = wallsNext ? Theme.Accent : Theme.GlassLight;
            closeupButton.GetComponentInChildren<Text>().color = wallsNext ? Theme.OnAccent : Theme.Text;
            if (!spotVisible) return;
            spotButton.SetLabel(p == ScenePhase.Spin ? (spotDone ? "Next spot  →" : "Next spot") :
                                resumeSpot >= 0 ? $"Resume spot {resumeSpot + 1}" : "Start spot here");
            spotButton.interactable = p == ScenePhase.Spin || canStartSpot;
            spotButton.GetComponent<Image>().color = (p == ScenePhase.Spin && spotDone) || (p != ScenePhase.Spin && canStartSpot)
                ? Theme.Accent : Theme.GlassLight;
            spotButton.GetComponentInChildren<Text>().color = (p == ScenePhase.Spin && spotDone) || (p != ScenePhase.Spin && canStartSpot)
                ? Theme.OnAccent : Theme.Text;
        }

        /// <summary>Top-down map (x right, z up): AR points grey, spots teal, the phone white with a heading tick.
        /// With coverage: room cells near a spot tinted teal, cells still far from every spot orange, and the
        /// suggested next spot as a pulsing orange dot.</summary>
        public void SetMap(IList<Vector2> points, IList<Vector2> spots, Vector2 lens, float headingDeg, float spotRadius,
                           FloorCoverage coverage, Vector2? suggested)
        {
            var clear = new Color32(0, 0, 0, 0);
            for (int i = 0; i < mapPixels.Length; i++) mapPixels[i] = clear;

            Vector2 lo = lens, hi = lens;
            void Grow(Vector2 p) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
            if (points != null) foreach (var p in points) Grow(p);
            if (spots != null) foreach (var p in spots) Grow(p);
            if (suggested.HasValue) Grow(suggested.Value);
            Vector2 mid = (lo + hi) * 0.5f;
            float extent = Mathf.Max(hi.x - lo.x, hi.y - lo.y, 3f) * 1.1f; // at least a 3 m view
            float scale = (MapPx - 8) / extent;
            Vector2Int Px(Vector2 p) => new Vector2Int(Mathf.RoundToInt((p.x - mid.x) * scale + MapPx * 0.5f),
                                                       Mathf.RoundToInt((p.y - mid.y) * scale + MapPx * 0.5f));
            void Put(int x, int y, Color32 c)
            {
                if (x >= 0 && y >= 0 && x < MapPx && y < MapPx) mapPixels[y * MapPx + x] = c;
            }
            void Disc(Vector2Int c, int r, Color32 col)
            {
                for (int y = -r; y <= r; y++)
                    for (int x = -r; x <= r; x++)
                        if (x * x + y * y <= r * r) Put(c.x + x, c.y + y, col);
            }

            if (coverage != null)
            {
                Color32 near = Theme.WithAlpha(Theme.Accent, 0.22f), far = Theme.WithAlpha(Theme.Warning, 0.4f);
                foreach (var c in coverage.KnownCells)
                {
                    var a = Px(coverage.CenterOf(c) - Vector2.one * (coverage.Cell * 0.5f));
                    var b = Px(coverage.CenterOf(c) + Vector2.one * (coverage.Cell * 0.5f));
                    var col = coverage.IsCovered(c) ? near : far;
                    for (int y = a.y; y < b.y; y++)
                        for (int x = a.x; x < b.x; x++)
                            Put(x, y, col);
                }
            }
            if (points != null)
            {
                var grey = new Color32(200, 210, 225, 150);
                foreach (var p in points) { var q = Px(p); Put(q.x, q.y, grey); }
            }
            if (spots != null)
            {
                Color32 teal = Theme.Accent;
                int ring = Mathf.Max(2, Mathf.RoundToInt(spotRadius * scale));
                foreach (var s in spots)
                {
                    var c = Px(s);
                    for (int a = 0; a < 48; a++)
                    {
                        float t = a * Mathf.PI * 2 / 48;
                        Put(c.x + Mathf.RoundToInt(Mathf.Cos(t) * ring), c.y + Mathf.RoundToInt(Mathf.Sin(t) * ring), teal);
                    }
                    Disc(c, 2, teal);
                }
            }
            if (suggested.HasValue)
            {
                Color32 orange = Theme.WithAlpha(Theme.Warning, 0.6f + 0.4f * Mathf.Sin(Time.time * 6f));
                Disc(Px(suggested.Value), 5, orange);
            }
            var me = Px(lens);
            Color32 white = Color.white;
            float h = headingDeg * Mathf.Deg2Rad;
            for (int k = 0; k < 12; k++) Put(me.x + Mathf.RoundToInt(Mathf.Sin(h) * k), me.y + Mathf.RoundToInt(Mathf.Cos(h) * k), white);
            Disc(me, 3, white);
            mapTex.SetPixels32(mapPixels);
            mapTex.Apply(false);
        }
    }
}
