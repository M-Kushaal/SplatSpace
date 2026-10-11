using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GSCapture.Core;
using GSCapture.Net;
using GSCapture.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

namespace GSCapture.EditorTools
{
    /// <summary>
    /// Renders every app screen at phone resolution (2400x1080) to PNG, with sample data, so the UI can be
    /// reviewed without a device. Needs a graphics device (don't pass -nographics).
    ///   Menu: Capture > Render UI previews
    ///   CLI:  Unity -batchmode -projectPath app -executeMethod GSCapture.EditorTools.UIPreviewRenderer.RunBatch -quit
    /// Env: GS_PREVIEW_PHOTOS = folder of capture JPEGs used as camera background/thumbnails,
    ///      GS_PREVIEW_OUT = output folder (default Library/ui_previews).
    /// </summary>
    public static class UIPreviewRenderer
    {
        const int W = 2400, H = 1080;

        [MenuItem("Capture/Render UI previews")]
        public static void RenderAll()
        {
            string outDir = Environment.GetEnvironmentVariable("GS_PREVIEW_OUT");
            if (string.IsNullOrEmpty(outDir)) outDir = Path.GetFullPath("Library/ui_previews");
            Directory.CreateDirectory(outDir);
            string photoDir = Environment.GetEnvironmentVariable("GS_PREVIEW_PHOTOS");
            var photos = !string.IsNullOrEmpty(photoDir) && Directory.Exists(photoDir)
                ? Directory.GetFiles(photoDir, "*.jpg").OrderBy(p => p).ToList()
                : new List<string>();
            string Photo(int i) => photos.Count == 0 ? null : photos[(i * 7) % photos.Count];

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            var camGo = new GameObject("PreviewCamera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.targetTexture = rt;
            cam.orthographic = true;
            // Any MonoBehaviour works as coroutine host; previews never start coroutines.
            var host = new GameObject("Host").AddComponent<UnityEngine.EventSystems.EventSystem>();

            void Shot(string name, Action<Transform, RectTransform> build, string backgroundPhoto = null)
            {
                var canvasGo = UIKit.Canvas(host.transform, "UI");
                canvasGo.renderMode = RenderMode.ScreenSpaceCamera;
                canvasGo.worldCamera = cam;
                canvasGo.planeDistance = 1;
                var canvas = canvasGo.transform;
                if (backgroundPhoto != null)
                {
                    var bg = UIKit.Rect(canvas, "CameraFeed").Stretch().gameObject.AddComponent<RawImage>();
                    bg.texture = Thumbnails.Get(backgroundPhoto, 1600);
                }
                // Simulate a phone notch inset on the left (landscape) like many Android devices.
                var safe = UIKit.SafeArea(canvas, new Rect(90, 0, W - 90, H), new Vector2(W, H));
                build(canvas, safe);
                Canvas.ForceUpdateCanvases();
                foreach (var lg in canvas.GetComponentsInChildren<LayoutGroup>(true))
                    LayoutRebuilder.ForceRebuildLayoutImmediate(lg.GetComponent<RectTransform>());
                Canvas.ForceUpdateCanvases();
                cam.Render();
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Path.Combine(outDir, name + ".png"), tex.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tex);
                UnityEngine.Object.DestroyImmediate(canvasGo.gameObject);
                Debug.Log($"[GSCapture] preview {name}.png");
            }

            Shot("1_home", (canvas, safe) =>
            {
                var home = new HomeScreen(canvas, safe);
                home.SetServer("http://192.168.1.23:8000", true);
                var now = DateTime.UtcNow;
                var entries = new List<CaptureEntry>
                {
                    Entry("a", now.AddMinutes(-5), 68, Photo(1), new UploadState { phase = "uploading" }),
                    Entry("b", now.AddMinutes(-40), 68, Photo(2), new UploadState { phase = "uploaded", status = "training", stage = "refining poses" }),
                    Entry("c", now.AddHours(-3), 66, Photo(3), new UploadState
                        { phase = "uploaded", status = "ready", psnr_object = 19.9f, train_seconds = 1522 }),
                    Entry("d", now.AddDays(-1), 41, Photo(4), new UploadState
                        { phase = "uploaded", status = "invalid", message = "capture failed validation: target is visible in only 34% of frames" }),
                    Entry("e", now.AddDays(-2), 68, Photo(5), new UploadState()),
                };
                home.Refresh(entries, id => id == "a" ? 0.42f : -1f);
            });
            Shot("2_home_empty", (canvas, safe) =>
            {
                var home = new HomeScreen(canvas, safe);
                home.SetServer("http://192.168.1.23:8000", false);
                home.Refresh(new List<CaptureEntry>(), _ => -1f);
            });
            Shot("3_scanning", (canvas, safe) =>
            {
                var hud = new CaptureHud(canvas, safe, host);
                hud.SetStep(0);
                hud.SetHint("Find the surface", "Slowly sweep the phone over the table or floor the object stands on");
                hud.SetCoach(null, Theme.Info);
                hud.SetPlacement(false, false);
                hud.SetCapturing(false);
            }, Photo(10));
            Shot("4_placing", (canvas, safe) =>
            {
                var hud = new CaptureHud(canvas, safe, host);
                hud.SetStep(1);
                hud.SetHint(null);
                hud.SetCoach("Resize the cylinder so the whole object fits inside", Theme.Info);
                hud.SetPlacement(true, true);
                hud.SetCapturing(false);
            }, Photo(11));
            Shot("5_capturing", (canvas, safe) =>
            {
                var hud = new CaptureHud(canvas, safe, host);
                hud.SetStep(2);
                hud.SetHint(null);
                hud.SetPlacement(false, false);
                hud.SetCapturing(true);
                hud.SetCoach(CaptureGate.Coaching(GateReason.Settling), CaptureHud.CoachColor(GateReason.Settling));
                hud.SetProgress(34, 0.5f, true, "→  Walk right around the object", false);
            }, Photo(12));
            Shot("6_review", (canvas, safe) =>
            {
                var review = new ReviewScreen(canvas);
                review.Show(68, 1f, TimeSpan.FromSeconds(309), 16829, "55 MB",
                    Enumerable.Range(0, 6).Select(i => Photo(i * 3)).Where(p => p != null).ToList());
            }, Photo(13));
            Shot("7_settings", (canvas, safe) =>
            {
                new HomeScreen(canvas, safe).Refresh(new List<CaptureEntry>(), _ => -1f);
                var settings = new SettingsScreen(canvas);
                settings.Show("http://192.168.1.23:8000", "Device account device-3f9a1c2b… · 5 captures on this phone");
                settings.SetStatus("✓ Connected", Theme.Success);
            });

            Shot("8_capture_type", (canvas, safe) =>
            {
                new HomeScreen(canvas, safe).Refresh(new List<CaptureEntry>(), _ => -1f);
                new CaptureTypeSheet(canvas).Show();
            });
            Shot("9_scene_capturing", (canvas, safe) =>
            {
                var hud = new CaptureHud(canvas, safe, host);
                hud.SetStepNames(CaptureHud.SceneSteps);
                hud.SetStep(2);
                hud.SetHint(null);
                hud.SetStartOnly(false, false, "");
                hud.SetCapturing(true);
                var planner = new StationPlanner();
                planner.StartStation(new Vector3(-1.2f, 1.4f, 0.4f), Vector3.forward);
                planner.EndStation();
                var st = planner.StartStation(new Vector3(0.6f, 1.4f, 0.4f), Vector3.forward);
                for (int k = 0; k < 15; k++) st.Filled[0, k] = true;
                for (int k = 0; k < 6; k++) st.Filled[1, k] = true;
                var sh = new SceneHud(safe, planner.Config.Sectors);
                sh.SetCompass(230f, st, 0, 15, 0, "Spot 2", $"{Mathf.RoundToInt(st.Fill * 100)}%");
                sh.SetButtons(ScenePhase.Spin, false, false);
                var rng = new System.Random(3);
                var pts = new List<Vector2>();
                for (int i = 0; i < 700; i++) // walls of a 4 x 3 m room + a sofa
                {
                    float t = (float)rng.NextDouble();
                    int side = rng.Next(5);
                    pts.Add(side == 0 ? new Vector2(-2 + 4 * t, -1.5f) : side == 1 ? new Vector2(-2 + 4 * t, 1.5f) :
                            side == 2 ? new Vector2(-2, -1.5f + 3 * t) : side == 3 ? new Vector2(2, -1.5f + 3 * t) :
                            new Vector2(-1.5f + 1.6f * t, 1.1f));
                }
                var spots = new List<Vector2> { new Vector2(-1.2f, 0f), new Vector2(0.6f, 0f) };
                var cov = new FloorCoverage();
                cov.Update(pts, spots);
                Vector2? next = cov.SuggestSpot(out Vector2 n, out _) ? n : (Vector2?)null;
                sh.SetMap(pts, spots, new Vector2(0.9f, -0.2f), 230f, 0.4f, cov, next);
                hud.SetCoach(CaptureGate.Coaching(GateReason.TooCloseToBody), CaptureHud.CoachColor(GateReason.TooCloseToBody));
                hud.SetProgress(61, st.Fill, false, "↻  Turn right (whole body, arm out)", false, "of this spot", "1 more spot");
            }, Photo(14));

            UnityEngine.Object.DestroyImmediate(host.gameObject);
            UnityEngine.Object.DestroyImmediate(camGo);
            rt.Release();
            Debug.Log($"[GSCapture] UI previews written to {outDir}");
        }

        static CaptureEntry Entry(string id, DateTime created, int frames, string thumb, UploadState state) => new CaptureEntry
        {
            Id = id, Dir = "", Complete = true, Frames = frames, Bytes = 55L << 20, Created = created, ThumbPath = thumb, State = state,
        };

        public static void RunBatch()
        {
            try
            {
                RenderAll();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
