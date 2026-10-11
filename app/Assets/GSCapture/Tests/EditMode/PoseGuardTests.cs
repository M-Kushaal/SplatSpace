using System.Collections.Generic;
using GSCapture.Core;
using GSCapture.Core.Quality;
using NUnit.Framework;
using UnityEngine;

namespace GSCapture.Tests
{
    public class PoseGuardTests
    {
        const float Dt = 1f / 30f;

        /// <summary>Feed n frames of a user turning at `turnRadS` (gyro agrees) and walking at `speed` m/s.</summary>
        static PoseEvent Run(PoseGuard g, ref float t, ref Vector3 pos, ref float yaw, int n, float turnRadS, float speed,
                             bool tracking = true, float gyroOverride = -1f)
        {
            var any = PoseEvent.None;
            for (int i = 0; i < n; i++)
            {
                t += Dt;
                yaw += turnRadS * Dt * Mathf.Rad2Deg;
                pos += Vector3.right * speed * Dt;
                var e = g.Update(t, pos, Quaternion.Euler(0, yaw, 0), gyroOverride >= 0 ? gyroOverride : turnRadS, tracking);
                if (e != PoseEvent.None && any == PoseEvent.None) any = e;
            }
            return any;
        }

        [Test]
        public void BriskNormalTurnsAndWalkingDontTrigger()
        {
            var g = new PoseGuard();
            float t = 0, yaw = 0;
            var pos = Vector3.zero;
            Assert.That(Run(g, ref t, ref pos, ref yaw, 120, 2f, 0.8f), Is.EqualTo(PoseEvent.None));
            Assert.That(g.Paused, Is.False);
            Assert.That(g.Epoch, Is.EqualTo(0));
        }

        [Test]
        public void ATeleportPausesAndNeedsAVisualCheck()
        {
            var g = new PoseGuard();
            float t = 0, yaw = 0;
            var pos = Vector3.zero;
            Run(g, ref t, ref pos, ref yaw, 10, 0.3f, 0.2f);
            pos += new Vector3(0.6f, 0, 0); // ARCore snapped 60 cm in one frame
            Assert.That(Run(g, ref t, ref pos, ref yaw, 1, 0.3f, 0.2f), Is.EqualTo(PoseEvent.Teleport));
            Assert.That(g.Paused && g.NeedsVisualCheck, Is.True);
            Assert.That(g.Epoch, Is.EqualTo(1));
            Run(g, ref t, ref pos, ref yaw, 40, 0.1f, 0f);
            Assert.That(g.ReadyToResume, Is.True, "steady for > 1 s");
            Assert.That(g.NeedsVisualCheck, Is.True, "but the position must still be confirmed");
            g.ConfirmVisually();
            g.Resume();
            Assert.That(g.Paused || g.NeedsVisualCheck, Is.False);
        }

        [Test]
        public void ARTurningWithoutTheGyroIsAJump()
        {
            var g = new PoseGuard();
            float t = 0, yaw = 0;
            var pos = Vector3.zero;
            Run(g, ref t, ref pos, ref yaw, 10, 0f, 0f);
            // AR yaw swings 20 deg in 0.1 s while the gyro says the phone is still
            Assert.That(Run(g, ref t, ref pos, ref yaw, 3, 20f * Mathf.Deg2Rad / 0.1f, 0f, gyroOverride: 0.05f),
                Is.EqualTo(PoseEvent.GyroMismatch));
        }

        [Test]
        public void LosingAndRegainingTrackingIsARelocalisation()
        {
            var g = new PoseGuard();
            float t = 0, yaw = 0;
            var pos = Vector3.zero;
            Run(g, ref t, ref pos, ref yaw, 10, 0f, 0f);
            Assert.That(Run(g, ref t, ref pos, ref yaw, 20, 0f, 0f, tracking: false), Is.EqualTo(PoseEvent.TrackingLost));
            Assert.That(g.Epoch, Is.EqualTo(0));
            pos += new Vector3(16f, 0, 0); // the second room: back from a loss, 16 m away
            Assert.That(Run(g, ref t, ref pos, ref yaw, 1, 0f, 0f), Is.EqualTo(PoseEvent.Relocalized));
            Assert.That(g.Epoch, Is.EqualTo(1));
            Assert.That(g.NeedsVisualCheck, Is.True);
        }

        [Test]
        public void AShakePausesWithoutStartingANewEpoch()
        {
            var g = new PoseGuard();
            float t = 0, yaw = 0;
            var pos = Vector3.zero;
            Assert.That(Run(g, ref t, ref pos, ref yaw, 3, 4f, 0f), Is.EqualTo(PoseEvent.Shake));
            Assert.That(g.Paused, Is.True);
            Assert.That(g.Epoch, Is.EqualTo(0));
            Assert.That(g.NeedsVisualCheck, Is.False);
            Run(g, ref t, ref pos, ref yaw, 40, 0.2f, 0f);
            Assert.That(g.ReadyToResume, Is.True);
        }

        [Test]
        public void WalkingWhileCaptureWasPausedIsNotATeleport()
        {
            var g = new PoseGuard();
            float t = 0, yaw = 0;
            var pos = Vector3.zero;
            Run(g, ref t, ref pos, ref yaw, 10, 0f, 0f);
            t += 20f;                       // room-check sheet open for 20 s
            pos += new Vector3(3f, 0, 0);   // the user walked 3 m meanwhile
            Assert.That(Run(g, ref t, ref pos, ref yaw, 5, 0f, 0f), Is.EqualTo(PoseEvent.None));
        }

        [Test]
        public void AnchoredSpotFollowsItsAnchor()
        {
            var p = new StationPlanner();
            var fwd = new Vector3(Mathf.Sin(7.5f * Mathf.Deg2Rad), 0, Mathf.Cos(7.5f * Mathf.Deg2Rad));
            var body = new Vector3(1f, 1.4f, 1f);
            var lens = body + fwd * 0.4f;
            var st = p.StartStation(lens, fwd);
            var before = p.Evaluate(lens, fwd);
            // ARCore re-shapes its map: the room (and the anchor) are now 2 m away and turned 30 deg
            var rot = Quaternion.Euler(0, 30f, 0);
            Vector3 Move(Vector3 w) => new Vector3(2f, 0, -1f) + rot * w;
            p.SetStationAnchor(0, Move(st.AnchorPosition), rot);
            Assert.That(Vector3.Distance(st.Center, Move(body)), Is.LessThan(1e-3f));
            var after = p.Evaluate(Move(lens), rot * fwd);
            Assert.That(after.Sector, Is.EqualTo(before.Sector), "the same real view lands in the same sector");
            Assert.That(after.SectorOffsetDeg, Is.EqualTo(before.SectorOffsetDeg).Within(0.01f));
            Assert.That(p.StationPlacement(after, 3f), Is.EqualTo(GateReason.Ok), "not 'left the spot'");
        }

        [Test]
        public void SpotsFromAnOldMapAreIgnoredForNearestSpot()
        {
            var p = new StationPlanner();
            var lens = new Vector3(0, 1.4f, 0.4f);
            p.StartStation(lens, Vector3.forward);
            p.EndStation();
            Assert.That(p.NearestStation(lens, Vector3.forward), Is.EqualTo(0));
            p.Epoch = 1;
            Assert.That(p.NearestStation(lens, Vector3.forward), Is.EqualTo(-1));
            Assert.That(p.CanStartStationAt(lens, Vector3.forward), Is.True);
        }

        // ---- visual re-check

        static byte[] World(int w, int h, int seed, int cell = 9)
        {
            var rng = new System.Random(seed);
            int cw = w / cell + 1;
            var cells = new byte[cw * (h / cell + 1)];
            for (int i = 0; i < cells.Length; i++) cells[i] = (byte)rng.Next(20, 235);
            var g = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) g[y * w + x] = cells[(y / cell) * cw + x / cell];
            return g;
        }

        static byte[] Crop(byte[] world, int ww, int x0, int y0, int w, int h)
        {
            var g = new byte[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) g[y * w + x] = world[(y0 + y) * ww + x0 + x];
            return g;
        }

        [Test]
        public void RelocCheckConfirmsTheSameMapAndCatchesAJumpedOne()
        {
            var world = World(900, 400, 11);
            var earlier = OrbLite.Extract(Crop(world, 900, 100, 50, 480, 270), 480, 270);
            var unrelated = OrbLite.Extract(World(480, 270, 77), 480, 270);
            var live = OrbLite.Extract(Crop(world, 900, 130, 60, 480, 270), 480, 270);
            var cands = new List<RelocCandidate>
            {
                new RelocCandidate { Features = unrelated, Position = new Vector3(0, 1.4f, 0), Forward = Vector3.forward },
                new RelocCandidate { Features = earlier, Position = new Vector3(0.1f, 1.4f, 0), Forward = Vector3.forward },
            };
            var here = new Vector3(0.05f, 1.4f, 0.05f);
            var ok = RelocCheck.Run(live, here, Vector3.forward, cands, RelocCheck.PickCandidates(cands, here, Vector3.forward));
            Assert.That(ok.Matched && ok.Consistent, Is.True);
            Assert.That(ok.Candidate, Is.EqualTo(1));

            // same view, but AR now says the phone is 3 m away: the map jumped
            var jumped = RelocCheck.Run(live, here + new Vector3(3f, 0, 0), Vector3.forward, cands, new[] { 0, 1 });
            Assert.That(jumped.Matched, Is.True);
            Assert.That(jumped.Consistent, Is.False);
        }
    }

    public class ExposureAndManualTests
    {
        [Test]
        public void TurningToAWindowWaitsForExposureThenResetsTheBlurBaseline()
        {
            var w = new ExposureWatch();
            float t = 0;
            for (int i = 0; i < 10; i++) Assert.That(w.Update(t += 0.1f, 100f + i % 2), Is.False);
            Assert.That(w.Adjusting, Is.False, "normal flicker is not a change");
            w.Update(t += 0.1f, 190f);                       // turned toward the window
            Assert.That(w.Adjusting, Is.True);
            w.Update(t += 0.1f, 160f);                       // exposure still pulling down
            Assert.That(w.Update(t += 0.1f, 130f), Is.False);
            bool reset = false;
            for (int i = 0; i < 6; i++) reset |= w.Update(t += 0.1f, 128f);
            Assert.That(reset, Is.True, "settled: start a fresh blur baseline");
            Assert.That(w.Adjusting, Is.False);
        }

        [Test]
        public void SharpnessBaselineCanBeCleared()
        {
            var s = new SharpnessTracker();
            for (int i = 0; i < 10; i++) s.Add(100);
            Assert.That(s.Ratio(40), Is.EqualTo(0.4f).Within(1e-4f));
            s.Clear();
            Assert.That(s.Ratio(40), Is.EqualTo(1f), "no baseline yet: nothing is called blurry");
        }

        [Test]
        public void ManualPhotoFillsTheOpenSectorOrCountsAsWallsPass()
        {
            var p = new StationPlanner();
            var fwd = new Vector3(Mathf.Sin(7.5f * Mathf.Deg2Rad), 0, Mathf.Cos(7.5f * Mathf.Deg2Rad));
            var lens = new Vector3(0, 1.4f, 0) + fwd * 0.4f;
            p.StartStation(lens, fwd);
            var m = p.Evaluate(lens, fwd);
            Assert.That(p.MarkStationShot(m, lens, fwd), Is.EqualTo(0));
            Assert.That(p.Current.Filled[0, 0], Is.True);
            // a second manual shot at the same (now filled) sector becomes an extra photo, not lost
            Assert.That(p.MarkCloseup(lens, fwd), Is.EqualTo(9000));
        }
    }
}

