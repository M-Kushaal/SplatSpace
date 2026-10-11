using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using GSCapture.Core;
using GSCapture.Net;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GSCapture.Tests
{
    /// <summary>JSON models vs. real responses captured from the FastAPI backend (Fixtures/*.json).</summary>
    public class ModelTests
    {
        static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(Application.dataPath, "GSCapture/Tests/EditMode/Fixtures", name));

        [Test]
        public void ParsesCaptureCreated()
        {
            var c = JsonUtility.FromJson<CaptureCreated>(Fixture("capture_created.json"));
            Assert.IsNotEmpty(c.capture_id);
            Assert.AreEqual(2, c.uploads.Count);
            StringAssert.StartsWith("http://192.168.1.23:8000/files/", c.uploads[0].url);
        }

        [Test]
        public void ParsesReadyCaptureWithMetricsAndArtifacts()
        {
            var c = JsonUtility.FromJson<CaptureOut>(Fixture("capture_ready.json"));
            Assert.AreEqual("ready", c.status);
            Assert.IsTrue(c.HasJob);
            Assert.AreEqual(19.91f, c.job.metrics.psnr_object_mean, 1e-3f);
            Assert.AreEqual(1522.5f, c.job.metrics.train_seconds, 1e-3f);
            Assert.That(c.job.artifacts.Exists(a => a.kind == "splat_object_ply" && a.url.Contains("/files/")));
        }

        [Test]
        public void ParsesRefiningStageAndMissingJob()
        {
            var r = JsonUtility.FromJson<CaptureOut>(Fixture("capture_refining.json"));
            Assert.AreEqual("training", r.status);
            Assert.AreEqual("refining poses", r.job.stage);
            var q = JsonUtility.FromJson<CaptureOut>(Fixture("capture_queued_nojob.json"));
            Assert.IsFalse(q.HasJob); // JSON null job must not look like a real job
        }

        [Test]
        public void ErrorMessagesComeFromBackendDetail()
        {
            StringAssert.Contains("wrong convention", BackendClient.ErrorMessage(Fixture("error_422.json")));
            StringAssert.StartsWith("capture failed validation:", BackendClient.ErrorMessage(Fixture("error_422.json")));
            Assert.AreEqual("invalid or expired token", BackendClient.ErrorMessage(Fixture("error_401.json")));
            Assert.IsNull(BackendClient.ErrorMessage("not json"));
            StringAssert.Contains("not a valid email", BackendClient.ErrorMessage(Fixture("error_422_request.json")));
        }

        [Test]
        public void DeviceAccountUsesAnEmailTheBackendAccepts()
        {
            PlayerPrefs.SetString("gs.device.email", "device-old@gscapture.local");
            StringAssert.EndsWith("@devices.gscapture.app", BackendClient.DeviceEmail);
            StringAssert.DoesNotContain(".local", BackendClient.DeviceEmail);
        }
    }

    public class LibraryTests
    {
        string dir;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "gs_lib_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(dir, true);

        string MakeCapture(string id, int frames, bool complete = true)
        {
            string c = Path.Combine(dir, id);
            Directory.CreateDirectory(Path.Combine(c, "images"));
            Directory.CreateDirectory(Path.Combine(c, "local"));
            var sb = new System.Text.StringBuilder("{\"frames\": [");
            for (int i = 0; i < frames; i++)
            {
                File.WriteAllBytes(Path.Combine(c, "images", $"{i:D6}.jpg"), new byte[100 + i]);
                sb.Append(i > 0 ? "," : "").Append($"{{\"file_path\": \"images/{i:D6}.jpg\"}}");
            }
            if (complete) File.WriteAllText(Path.Combine(c, "manifest.json"), sb.Append("]}").ToString());
            File.WriteAllBytes(Path.Combine(c, "points3d.ply"), new byte[50]);
            File.WriteAllText(Path.Combine(c, "local", "splat_object.ply"), "local only");
            return c;
        }

        [Test]
        public void UploadFilesPutManifestLastAndSkipLocalOnlyFiles()
        {
            string c = MakeCapture("20261002_082535_399e21", 3);
            CaptureLibrary.SaveState(c, new UploadState { phase = "uploading" });
            File.WriteAllText(Path.Combine(c, "images", "x.tmp"), "partial");
            var files = CaptureLibrary.UploadFiles(c);
            var paths = files.ConvertAll(f => f.path);
            Assert.AreEqual("manifest.json", paths[paths.Count - 1]);
            CollectionAssert.Contains(paths, "images/000002.jpg");
            CollectionAssert.Contains(paths, "points3d.ply");
            Assert.That(paths.TrueForAll(p => !p.Contains("\\") && !p.StartsWith("local/") && !p.EndsWith(".tmp")
                                               && p != CaptureLibrary.StateFile));
            Assert.AreEqual(102, files.Find(f => f.path == "images/000002.jpg").size);
        }

        [Test]
        public void StateRoundTripsForResume()
        {
            string c = MakeCapture("cap", 2);
            var s = new UploadState { server = "http://x:8000", remote_id = "r1", phase = "uploading", urls_issued_unix = 123 };
            s.uploaded.Add("images/000000.jpg");
            s.targets.Add(new UploadTarget { path = "manifest.json", url = "http://x/files/a" });
            CaptureLibrary.SaveState(c, s);
            var back = CaptureLibrary.LoadState(c);
            Assert.AreEqual("r1", back.remote_id);
            CollectionAssert.AreEqual(s.uploaded, back.uploaded);
            Assert.AreEqual("http://x/files/a", back.targets[0].url);
            File.WriteAllText(Path.Combine(c, CaptureLibrary.StateFile), "{corrupt");
            Assert.AreEqual("local", CaptureLibrary.LoadState(c).phase); // corrupt state never crashes the app
        }

        [Test]
        public void ScanFindsCapturesNewestFirstWithFrameCounts()
        {
            MakeCapture("20261001_101010_aaaaaa", 4);
            MakeCapture("20261002_082535_bbbbbb", 7);
            MakeCapture("20261003_090000_cccccc", 2, complete: false);
            var list = CaptureLibrary.Scan(dir);
            Assert.AreEqual(3, list.Count);
            Assert.AreEqual("20261003_090000_cccccc", list[0].Id);
            Assert.IsFalse(list[0].Complete);
            Assert.AreEqual(7, list[1].Frames);
            Assert.AreEqual(new DateTime(2026, 10, 2, 8, 25, 35, DateTimeKind.Utc), list[1].Created);
            Assert.IsNotNull(list[1].ThumbPath);
        }

        [Test]
        public void StatusLabelsFollowServerState()
        {
            CaptureEntry E(UploadState s, bool complete = true) => new CaptureEntry { Complete = complete, State = s };
            Assert.AreEqual("Incomplete", StatusView.For(E(new UploadState(), false)).text);
            Assert.AreEqual("On device", StatusView.For(E(new UploadState())).text);
            Assert.AreEqual("Uploading 42%", StatusView.For(E(new UploadState { phase = "uploading" }), 0.42f).text);
            Assert.AreEqual("Upload paused", StatusView.For(E(new UploadState { phase = "uploading" })).text);
            Assert.AreEqual("Queued", StatusView.For(E(new UploadState { phase = "uploaded", status = "queued" })).text);
            Assert.AreEqual("Refining poses", StatusView.For(E(new UploadState { phase = "uploaded", status = "training", stage = "refining poses" })).text);
            Assert.AreEqual("Training", StatusView.For(E(new UploadState { phase = "uploaded", status = "training", stage = "training" })).text);
            Assert.AreEqual("Ready", StatusView.For(E(new UploadState { phase = "uploaded", status = "ready" })).text);
            Assert.AreEqual("Rejected", StatusView.For(E(new UploadState { phase = "uploaded", status = "invalid" })).text);
            Assert.AreEqual("Upload failed", StatusView.For(E(new UploadState { phase = "failed" })).text);
            Assert.IsTrue(new UploadState { phase = "uploaded", status = "training" }.NeedsPolling);
            Assert.IsFalse(new UploadState { phase = "uploaded", status = "ready" }.NeedsPolling);
        }
    }

    public class ViewerLinkTests
    {
        [Test]
        public void ViewerUrlEscapesSignedLinks()
        {
            string splat = "http://192.168.1.102:8000/files/captures/c/jobs/j/splat_object.ply?exp=1&sig=ab+cd";
            string url = UploadManager.ViewerUrl("http://192.168.1.102:8000/", splat, "http://x/m.json?sig=1&exp=2", null, "Oct 3 14:01");
            StringAssert.StartsWith("http://192.168.1.102:8000/viewer/?url=http%3A%2F%2F192.168.1.102", url);
            StringAssert.Contains("%3Fexp%3D1%26sig%3Dab%2Bcd", url); // the signature survives intact
            StringAssert.Contains("&metrics=http%3A%2F%2Fx%2Fm.json%3Fsig%3D1%26exp%3D2", url);
            StringAssert.DoesNotContain("&refine=", url);
            StringAssert.EndsWith("&name=Capture%20Oct%203%2014%3A01", url);
        }
    }

    /// <summary>
    /// Real HTTP round trip against a running backend (set GS_TEST_API, e.g. http://127.0.0.1:8765):
    /// device account -> create capture -> PUT every file -> complete -> poll. Skipped otherwise.
    /// </summary>
    public class LiveBackendTests
    {
        [UnityTest]
        public IEnumerator UploadsABundleAndGetsItQueued()
        {
            string api = Environment.GetEnvironmentVariable("GS_TEST_API");
            if (string.IsNullOrEmpty(api))
            {
                Assert.Ignore("GS_TEST_API not set");
                yield break;
            }
            string dir = Path.Combine(Path.GetTempPath(), "gs_live_" + Guid.NewGuid().ToString("N"));
            WriteBundle(dir);

            var client = new BackendClient(api);
            bool? healthy = null;
            string why = null;
            yield return client.Health((ok, msg) => { healthy = ok; why = msg; });
            Assert.IsTrue(healthy, $"backend not reachable at {api}: {why}");

            var files = CaptureLibrary.UploadFiles(dir);
            CaptureCreated created = null;
            string err = null;
            yield return client.CreateCapture(new CaptureCreate { title = "unity-live-test", files = files }, (c, e) => { created = c; err = e; });
            Assert.IsNull(err, err);
            Assert.AreEqual(files.Count, created.uploads.Count);

            var urls = new Dictionary<string, string>();
            foreach (var t in created.uploads) urls[t.path] = t.url;
            foreach (var f in files)
            {
                string putErr = "pending";
                yield return BackendClient.PutFile(urls[f.path], Path.Combine(dir, f.path), null, r => putErr = r);
                Assert.IsNull(putErr, $"{f.path}: {putErr}");
            }

            CaptureOut cap = null;
            long code = 0;
            yield return client.Complete(created.capture_id, (c, k, e) => { cap = c; code = k; err = e; });
            Assert.IsNull(err, err);
            Assert.AreEqual("queued", cap.status);
            Assert.IsTrue(cap.validation.ok);

            CaptureOut again = null;
            yield return client.GetCapture(created.capture_id, (c, e) => { again = c; err = e; });
            Assert.IsNull(err, err);
            Assert.AreEqual(created.capture_id, again.id);
            Directory.Delete(dir, true);
        }

        /// <summary>A valid 24-frame orbit bundle with small real JPEGs, written by the app's own bundle writer.</summary>
        static void WriteBundle(string dir)
        {
            Directory.CreateDirectory(Path.Combine(dir, "images"));
            var dome = new ViewpointDome(Vector3.zero, 0.1f, 0.2f, 50f);
            var tex = new Texture2D(64, 48);
            var frames = new List<CapturedFrame>();
            for (int i = 0; i < 24; i++)
            {
                var slot = dome.Slots[i * 2];
                Vector3 pos = dome.SlotPosition(slot.Index);
                string rel = $"images/{i:D6}.jpg";
                File.WriteAllBytes(Path.Combine(dir, rel), tex.EncodeToJPG());
                frames.Add(new CapturedFrame
                {
                    FilePath = rel, Width = 64, Height = 48, Fx = 60, Fy = 60, Cx = 32, Cy = 24, TargetId = slot.Index,
                    CameraToWorldGL = PoseConverter.ToOpenGLCameraToWorld(pos, Quaternion.LookRotation(dome.Center - pos, Vector3.up)),
                });
            }
            UnityEngine.Object.DestroyImmediate(tex);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), CaptureBundleWriter.ManifestJson("live", "arcore",
                new Dictionary<string, string>(), frames, PoseConverter.ToOpenGLPoint(Vector3.zero), 0.1f, 0.2f, null,
                new Dictionary<string, int>()));
        }
    }
}
