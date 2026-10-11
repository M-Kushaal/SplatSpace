using System.Collections.Generic;
using GSCapture.Core;
using NUnit.Framework;
using UnityEngine;

namespace GSCapture.Tests
{
    public class StationPlannerTests
    {
        static Vector3 Dir(float azimuthDeg, float pitchDeg = 0f)
        {
            float a = azimuthDeg * Mathf.Deg2Rad, p = pitchDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(p) * Mathf.Sin(a), Mathf.Sin(p), Mathf.Cos(p) * Mathf.Cos(a));
        }

        /// <summary>Lens of a user standing at `body` holding the phone `arm` metres out, facing `az`.</summary>
        static Vector3 Lens(Vector3 body, float az, float arm = 0.4f)
        {
            var d = Dir(az);
            return body + new Vector3(d.x, 0, d.z) * arm;
        }

        /// <summary>Spin a full station: one shot per sector centre in the given bands.</summary>
        static int Spin(StationPlanner p, Vector3 body, float arm, params float[] pitches)
        {
            int shots = 0;
            foreach (float pitch in pitches)
                for (int s = 0; s < p.Config.Sectors; s++)
                {
                    float az = (s + 0.5f) * 360f / p.Config.Sectors;
                    var lens = Lens(body, az, arm);
                    var m = p.Evaluate(lens, Dir(az, pitch));
                    if (p.StationPlacement(m, 3f) != GateReason.Ok) continue;
                    p.MarkStationShot(m, lens, Dir(az, pitch));
                    shots++;
                }
            return shots;
        }

        [Test]
        public void ArmsLengthSpinFillsTheStation()
        {
            var p = new StationPlanner();
            var body = new Vector3(1f, 1.4f, -2f);
            p.StartStation(Lens(body, 0.5f * 15f), Dir(7.5f));
            int shots = Spin(p, body, 0.4f, 0f, -35f);
            Assert.That(shots, Is.EqualTo(48));
            Assert.That(p.CurrentDone, Is.False, "the ceiling band is required too");
            shots += Spin(p, body, 0.4f, 40f);
            Assert.That(shots, Is.EqualTo(72));
            Assert.That(p.CurrentDone, Is.True);
            Assert.That(Vector3.Distance(new Vector3(p.Current.Center.x, 0, p.Current.Center.z), new Vector3(1f, 0, -2f)),
                Is.LessThan(0.02f), "body re-fitted at the lens circle's centre");
        }

        [Test]
        public void TwistingOnlyTheWristIsCaught()
        {
            var p = new StationPlanner();
            var body = Vector3.zero;
            p.StartStation(Lens(body, 7.5f, 0.03f), Dir(7.5f));
            int shots = Spin(p, body, 0.03f, 0f);
            Assert.That(shots, Is.LessThan(10), "after ~60 deg of turning the planner sees the lens isn't moving");
            var m = p.Evaluate(Lens(body, 200f, 0.03f), Dir(200f));
            Assert.That(p.StationPlacement(m, 3f), Is.EqualTo(GateReason.TooCloseToBody));
        }

        [Test]
        public void BandsSectorsAndOffsets()
        {
            var p = new StationPlanner();
            p.StartStation(Vector3.zero, Dir(7.5f)); // first view at 7.5 deg = centre of sector 0
            var m = p.Evaluate(Vector3.zero, Dir(100f, -33f));
            Assert.That(m.Band, Is.EqualTo((int)SceneBand.Down));
            Assert.That(m.Sector, Is.EqualTo(6)); // 90..105
            Assert.That(m.SectorOffsetDeg, Is.EqualTo(2.5f).Within(0.01f));
            Assert.That(p.Evaluate(Vector3.zero, Dir(10f, -20f)).Band, Is.EqualTo(-1), "between level and down");
            Assert.That(p.Evaluate(Vector3.zero, Dir(10f, 30f)).Band, Is.EqualTo((int)SceneBand.Up));
        }

        [Test]
        public void PlacementReasons()
        {
            var p = new StationPlanner();
            var body = Vector3.zero;
            p.StartStation(Lens(body, 7.5f), Dir(7.5f));
            var m = p.Evaluate(Lens(body, 7.5f), Dir(7.5f));
            Assert.That(p.StationPlacement(m, 0.3f), Is.EqualTo(GateReason.TooCloseToSurface));
            Assert.That(p.StationPlacement(m, 3f), Is.EqualTo(GateReason.Ok));
            p.MarkStationShot(m, Lens(body, 7.5f), Dir(7.5f));
            Assert.That(p.StationPlacement(p.Evaluate(Lens(body, 7.5f), Dir(7.5f)), 3f), Is.EqualTo(GateReason.AlreadyCaptured));
            Assert.That(p.StationPlacement(p.Evaluate(Lens(body, 14f), Dir(14f)), 3f), Is.EqualTo(GateReason.AlreadyCaptured),
                "anywhere inside the sector counts as that sector");
            Assert.That(p.StationPlacement(p.Evaluate(Lens(body, 30f), Dir(30f, -20f)), 3f), Is.EqualTo(GateReason.BetweenSlots),
                "between the level and down bands");
            var far = p.Evaluate(new Vector3(1.5f, 0, 0), Dir(90f));
            Assert.That(p.StationPlacement(far, 3f), Is.EqualTo(GateReason.LeftStation));
        }

        [Test]
        public void NewStationsMustBeApartAndNextTargetPointsToTheGap()
        {
            var p = new StationPlanner();
            var body = Vector3.zero;
            p.StartStation(Lens(body, 7.5f), Dir(7.5f));
            Spin(p, body, 0.4f, 0f);
            Assert.That(p.NextTarget(p.Evaluate(Lens(body, 0f), Dir(0f, 0f)), out int band, out _), Is.True);
            Assert.That(band, Is.EqualTo((int)SceneBand.Down), "level band is full -> look down next");
            Spin(p, body, 0.4f, -35f);
            Assert.That(p.NextTarget(p.Evaluate(Lens(body, 0f), Dir(0f, -35f)), out band, out _), Is.True);
            Assert.That(band, Is.EqualTo((int)SceneBand.Up), "then up at the ceiling");
            Assert.That(p.CanStartStationAt(Lens(new Vector3(0.3f, 0, 0), 0f), Dir(0f)), Is.False);
            Assert.That(p.CanStartStationAt(Lens(new Vector3(1.5f, 0, 0), 0f), Dir(0f)), Is.True);
        }

        [Test]
        public void CloseupsNeedANewViewpoint()
        {
            var p = new StationPlanner();
            Assert.That(p.CloseupPlacement(Vector3.zero, Vector3.forward, 1f), Is.EqualTo(GateReason.Ok));
            Assert.That(p.MarkCloseup(Vector3.zero, Vector3.forward), Is.EqualTo(9000));
            Assert.That(p.CloseupPlacement(new Vector3(0.05f, 0, 0), Vector3.forward, 1f), Is.EqualTo(GateReason.AlreadyCaptured));
            Assert.That(p.CloseupPlacement(new Vector3(0.2f, 0, 0), Vector3.forward, 1f), Is.EqualTo(GateReason.Ok), "moved 20 cm");
            Assert.That(p.CloseupPlacement(Vector3.zero, Dir(20f), 1f), Is.EqualTo(GateReason.Ok), "turned 20 deg");
        }

        [Test]
        public void SectorsAreCountedFromTheFirstView()
        {
            var p = new StationPlanner();
            p.StartStation(Vector3.zero, Dir(100f)); // the user starts facing 100 deg
            var m = p.Evaluate(Vector3.zero, Dir(100f));
            Assert.That(m.Sector, Is.EqualTo(0));
            Assert.That(m.SectorOffsetDeg, Is.EqualTo(0f).Within(0.01f), "the first photo is on a sector centre");
            Assert.That(p.Evaluate(Vector3.zero, Dir(116f)).Sector, Is.EqualTo(1));
            Assert.That(p.Evaluate(Vector3.zero, Dir(90f)).Sector, Is.EqualTo(23));
        }

        [Test]
        public void WallsPassMeasuresTheWalkedPath()
        {
            var p = new StationPlanner();
            for (int i = 0; i < 11; i++) p.MarkCloseup(new Vector3(0.2f * i, 1.4f, 0f), Vector3.forward);
            Assert.That(p.WallsPathMeters, Is.EqualTo(2f).Within(1e-3f));
            p.MarkCloseup(new Vector3(5f, 1.4f, 3f), Vector3.forward); // jumped to another wall: not walked
            Assert.That(p.WallsPathMeters, Is.EqualTo(2f).Within(1e-3f));
        }

        [Test]
        public void CircleFitRecoversCentreAndRadius()
        {
            var pts = new List<Vector3>();
            for (int i = 0; i < 9; i++)
            {
                float a = i * 0.3f;
                pts.Add(new Vector3(2f + 0.45f * Mathf.Cos(a), 1.3f, -1f + 0.45f * Mathf.Sin(a)));
            }
            Assert.That(StationPlanner.FitCircleXZ(pts, out Vector2 c, out float r), Is.True);
            Assert.That(Vector2.Distance(c, new Vector2(2f, -1f)), Is.LessThan(1e-3f));
            Assert.That(r, Is.EqualTo(0.45f).Within(1e-3f));
        }

        [Test]
        public void SceneGateUsesPlacementThenMotion()
        {
            var cfg = GateConfig.Default;
            var input = new GateInput { Tracking = true, SteadySeconds = 1f, SharpnessRatio = 1f, Brightness = 120f };
            Assert.That(CaptureGate.EvaluateSceneGeometry(input, GateReason.TooCloseToBody, cfg), Is.EqualTo(GateReason.TooCloseToBody));
            Assert.That(CaptureGate.EvaluateSceneGeometry(input, GateReason.Ok, cfg), Is.EqualTo(GateReason.Ok));
            input.AngularSpeed = 1f;
            Assert.That(CaptureGate.EvaluateSceneGeometry(input, GateReason.Ok, cfg), Is.EqualTo(GateReason.MovingTooFast));
            input.AngularSpeed = 0.25f; // a slow, steady turn: too fast for objects, fine for rooms (blur decides)
            input.SteadySeconds = 0.15f;
            Assert.That(CaptureGate.EvaluateSceneGeometry(input, GateReason.Ok, cfg), Is.EqualTo(GateReason.MovingTooFast));
            Assert.That(CaptureGate.EvaluateSceneGeometry(input, GateReason.Ok, GateConfig.Scene), Is.EqualTo(GateReason.Ok));
            Assert.That(CaptureGate.Coaching(GateReason.TooCloseToBody), Does.Contain("arm's length"));
        }
    }

    public class SceneManifestTests
    {
        [Test]
        public void SceneManifestHasModeAndStationsButNoObjectBounds()
        {
            var frames = new List<CapturedFrame>
            {
                new CapturedFrame { FilePath = "images/000000.jpg", CameraToWorldGL = Matrix4x4.identity, Fx = 1500, Fy = 1500,
                                    Cx = 960, Cy = 540, Width = 1920, Height = 1080, TargetId = 7 },
            };
            string ext = CaptureBundleWriter.SceneExtensionJson(-1.25f,
                new List<(Vector3, int)> { (new Vector3(0.5f, -1.25f, 2f), 40), (new Vector3(-1f, -1.25f, 0.5f), 38) }, 12);
            string json = CaptureBundleWriter.ManifestJson("room", "arcore", null, frames, Vector3.zero, 0f, 0f, null, null,
                "scene", ext);
            StringAssert.Contains("\"capture_mode\": \"scene\"", json);
            StringAssert.DoesNotContain("object_bounds", json);
            StringAssert.Contains("\"scene\": {\"floor_y\": -1.25, \"closeups\": 12, \"stations\": [{\"center\": [0.5, -1.25, 2], \"frames\": 40}", json);
        }
    }

    public class FloorCoverageTests
    {
        /// <summary>AR points along the walls of a w x d room (x right, z forward), 3 per 10 cm.</summary>
        static List<Vector2> Walls(float w, float d)
        {
            var pts = new List<Vector2>();
            for (float t = 0.05f; t < w; t += 0.1f)
                for (int k = 0; k < 3; k++) { pts.Add(new Vector2(t, 0.05f)); pts.Add(new Vector2(t, d - 0.05f)); }
            for (float t = 0.05f; t < d; t += 0.1f)
                for (int k = 0; k < 3; k++) { pts.Add(new Vector2(0.05f, t)); pts.Add(new Vector2(w - 0.05f, t)); }
            return pts;
        }

        [Test]
        public void OneSpotInABigRoomLeavesTheFarWallsUncovered()
        {
            var cov = new FloorCoverage();
            var walls = Walls(6f, 4f);
            cov.Update(walls, new List<Vector2> { new Vector2(1.5f, 2f) });
            Assert.That(cov.KnownCount, Is.GreaterThan(30));
            Assert.That(cov.Fraction, Is.InRange(0.1f, 0.6f));
            Assert.That(cov.SuggestSpot(out Vector2 next, out int gain), Is.True);
            Assert.That(next.x, Is.GreaterThan(3f), "go to the uncovered far side");
            Assert.That(next.x, Is.InRange(0.5f, 5.5f));
            Assert.That(next.y, Is.InRange(0.5f, 3.5f), "suggested spots are free floor, not on a wall");
            Assert.That(gain, Is.GreaterThan(5));
        }

        [Test]
        public void FollowingTheSuggestionsCoversTheRoom()
        {
            var cov = new FloorCoverage();
            var walls = Walls(6f, 4f);
            var spots = new List<Vector2> { new Vector2(1.5f, 2f) };
            for (int i = 0; i < 8; i++)
            {
                cov.Update(walls, spots);
                if (!cov.SuggestSpot(out Vector2 next, out _)) break;
                spots.Add(next);
            }
            cov.Update(walls, spots);
            Assert.That(cov.Fraction, Is.EqualTo(1f));
            Assert.That(spots.Count, Is.InRange(3, 7), "a 6 x 4 m room needs a handful of spots, near the corners");
            Assert.That(cov.SuggestSpot(out _, out _), Is.False);
        }

        [Test]
        public void NothingKnownMeansNoCoverageYet()
        {
            var cov = new FloorCoverage();
            cov.Update(null, new List<Vector2> { Vector2.zero });
            Assert.That(cov.Fraction, Is.EqualTo(0f));
            Assert.That(cov.SuggestSpot(out _, out _), Is.False);
        }
    }

    public class BestFrameBufferTests
    {
        [Test]
        public void KeepsTheSharpestFramePerTarget()
        {
            var discarded = new List<string>();
            var buf = new BestFrameBuffer<string>(discarded.Add, 0.6f);
            Assert.That(buf.Offer(1, 10f, 0f, "a", out _), Is.False);
            Assert.That(buf.Wants(1, 5f), Is.False, "can't beat the pending frame: skip converting it");
            Assert.That(buf.Offer(1, 30f, 0.1f, "b", out _), Is.False);
            Assert.That(buf.Offer(1, 20f, 0.2f, "c", out _), Is.False);
            Assert.That(discarded, Is.EqualTo(new[] { "a", "c" }));
            Assert.That(buf.Offer(2, 1f, 0.3f, "d", out string ready), Is.True, "moving to the next sector commits");
            Assert.That(ready, Is.EqualTo("b"));
            Assert.That(buf.PendingKey, Is.EqualTo(2));
        }

        [Test]
        public void WindowTimeoutFlushAndClear()
        {
            var discarded = new List<string>();
            var buf = new BestFrameBuffer<string>(discarded.Add, 0.6f);
            buf.Offer(3, 10f, 1f, "a", out _);
            Assert.That(buf.Tick(1.5f, out _), Is.False);
            Assert.That(buf.Tick(1.65f, out string ready), Is.True);
            Assert.That(ready, Is.EqualTo("a"));
            Assert.That(buf.HasPending, Is.False);
            buf.Offer(4, 10f, 2f, "b", out _);
            Assert.That(buf.Flush(out ready), Is.True);
            Assert.That(ready, Is.EqualTo("b"));
            Assert.That(buf.Flush(out _), Is.False);
            buf.Offer(5, 10f, 3f, "c", out _);
            buf.Clear();
            Assert.That(discarded, Is.EqualTo(new[] { "c" }));
            Assert.That(buf.HasPending, Is.False);
        }
    }
}
