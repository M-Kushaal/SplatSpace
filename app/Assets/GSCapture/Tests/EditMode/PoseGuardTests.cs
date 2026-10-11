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
        public void ARCoresNormalMapCorrectionsDontPause()
        {
            // room 3: corrections of 15-70 cm and ~9 deg in a frame paused capture 25 times
            var g = new PoseGuard();
            float t = 0, yaw = 0;
            var pos = Vector3.zero;
            Run(g, ref t, ref pos, ref yaw, 10, 0f, 0f);
            pos += new Vector3(0.3f, 0, 0);
            Assert.That(Run(g, ref t, ref pos, ref yaw, 1, 0f, 0f), Is.EqualTo(PoseEvent.None), "30 cm correction");
            Run(g, ref t, ref pos, ref yaw, 10, 0f, 0f);
            Assert.That(Run(g, ref t, ref pos, ref yaw, 2, 9f * Mathf.Deg2Rad / (2 * Dt), 0f, gyroOverride: 0.05f),
                Is.EqualTo(PoseEvent.None), "9 deg nudge");
            Assert.That(g.Paused, Is.False);
            Assert.That(g.Epoch, Is.EqualTo(0));
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

    public class ManualCaptureTests
    {
        [Test]
        public void ManualUnlocksOnlyAfterHoldingTheSameSliceFiveSeconds()
        {
            var h = new SliceHold();
            float t = 0;
            for (; t < 4.9f; t += 0.1f) h.Update(7, t);
            Assert.That(h.Held, Is.LessThan(5f));
            h.Update(8, t += 0.1f);                // brief wobble onto the neighbour slice
            h.Update(7, t += 0.1f);
            for (; t < 5.3f; t += 0.1f) h.Update(7, t);
            Assert.That(h.Held, Is.GreaterThanOrEqualTo(5f), "a short wobble doesn't restart the count");
            for (float t0 = t; t < t0 + 0.7f; t += 0.1f) h.Update(9, t);   // really moved to another slice
            Assert.That(h.Held, Is.LessThan(1f));
            h.Restart(t);
            Assert.That(h.Held, Is.EqualTo(0f));
        }

        [Test]
        public void ViewKeyUsesTheNearestBandBetweenBands()
        {
            var p = new StationPlanner();
            Assert.That(p.NearestBand(-20f), Is.EqualTo((int)SceneBand.Down), "between level (0) and down (-35)");
            Assert.That(p.NearestBand(15f), Is.EqualTo((int)SceneBand.Level));
            Assert.That(p.NearestBand(70f), Is.EqualTo((int)SceneBand.Up));
            var fwdDown = Quaternion.Euler(20f, 7.5f, 0) * Vector3.forward;  // pitch -20: between bands
            var lens = new Vector3(0, 1.4f, 0.4f);
            p.StartStation(lens, Quaternion.Euler(0, 7.5f, 0) * Vector3.forward);
            Assert.That(p.ViewKey(lens, fwdDown), Is.EqualTo((int)SceneBand.Down * p.Config.Sectors + 0));
        }
    }

    public class RecoveryTests
    {
        const float Dt = 1f / 30f;

        [Test]
        public void ManualUnlocksAfterThreeSeconds()
        {
            Assert.That(SceneCaptureFlow.ManualHoldSeconds, Is.EqualTo(3f));
        }

        static PoseGuard Steady(out float t, Vector3 pos, Quaternion rot)
        {
            var g = new PoseGuard();
            t = 0;
            for (int i = 0; i < 10; i++) g.Update(t += Dt, pos, rot, 0f, true);
            return g;
        }

        [Test]
        public void AShortLossWithAPlausiblePoseIsBridgedByTheGyro()
        {
            var pos = new Vector3(0, 1.4f, 0);
            var g = Steady(out float t, pos, Quaternion.identity);
            for (int i = 0; i < 20; i++) g.Update(t += Dt, pos, Quaternion.identity, 1.5f, false); // 0.7 s lost, turning
            // back: turned ~45 deg (gyro 1.5 rad/s x 0.7 s = 60 deg possible), moved 30 cm
            var ev = g.Update(t += Dt, pos + new Vector3(0.3f, 0, 0), Quaternion.Euler(0, 45f, 0), 0.2f, true);
            Assert.That(ev, Is.EqualTo(PoseEvent.Relocalized));
            Assert.That(g.NeedsVisualCheck, Is.False, "nothing jumped: no photo re-check needed");
            Assert.That(g.Epoch, Is.EqualTo(0));
            for (int i = 0; i < 40; i++) g.Update(t += Dt, pos + new Vector3(0.3f, 0, 0), Quaternion.Euler(0, 45f, 0), 0.1f, true);
            Assert.That(g.ReadyToResume, Is.True);
        }

        [Test]
        public void ALossThatComesBackTurnedWithoutTheGyroIsAJump()
        {
            var pos = new Vector3(0, 1.4f, 0);
            var g = Steady(out float t, pos, Quaternion.identity);
            for (int i = 0; i < 20; i++) g.Update(t += Dt, pos, Quaternion.identity, 0.05f, false); // phone kept still
            g.Update(t += Dt, pos, Quaternion.Euler(0, 40f, 0), 0.05f, true);                      // AR says 40 deg
            Assert.That(g.NeedsVisualCheck, Is.True);
            Assert.That(g.Epoch, Is.EqualTo(1));
        }

        // ---- epipolar + depth check on synthetic cameras looking at a textured wall

        const int W = 480, H = 270;
        const float F = 380f;

        static CamPose Pose(Vector3 pos, Quaternion rot) => new CamPose
        {
            C2wGL = PoseConverter.ToOpenGLCameraToWorld(pos, rot), Fx = F, Fy = F, Cx = W / 2f, Cy = H / 2f, Height = H,
            Valid = true,
        };

        /// <summary>Wall points seen by both cameras, as features in each (rows bottom-up like the camera buffer).</summary>
        static (FrameFeatures a, FrameFeatures b, List<(int, int)> m) Observe(Vector3 pa, Quaternion ra, Vector3 pb, Quaternion rb)
        {
            var xa = new List<float>(); var ya = new List<float>(); var xb = new List<float>(); var yb = new List<float>();
            var m = new List<(int, int)>();
            var rng = new System.Random(5);
            for (int i = 0; i < 300; i++)
            {
                // a cluttered wall 3 m in front (z = 3), with things sticking out up to 1 m
                var P = new Vector3((float)rng.NextDouble() * 4f - 2f, 0.2f + (float)rng.NextDouble() * 2.4f, 3f - (float)rng.NextDouble());
                if (!Project(P, pa, ra, out float ua, out float va) || !Project(P, pb, rb, out float ub, out float vb)) continue;
                m.Add((xa.Count, xb.Count));
                xa.Add(ua); ya.Add((H - 1) - va); xb.Add(ub); yb.Add((H - 1) - vb);
            }
            FrameFeatures Make(List<float> x, List<float> y) => new FrameFeatures { X = x.ToArray(), Y = y.ToArray(), Width = W, Height = H };
            return (Make(xa, ya), Make(xb, yb), m);
        }

        static bool Project(Vector3 P, Vector3 pos, Quaternion rot, out float u, out float v)
        {
            var c2wCv = PoseConverter.ToOpenGLCameraToWorld(pos, rot) * Matrix4x4.Scale(new Vector3(1, -1, -1));
            Vector3 c = c2wCv.inverse.MultiplyPoint3x4(PoseConverter.ToOpenGLPoint(P)); // the app works in OpenGL world space
            u = F * c.x / c.z + W / 2f;
            v = F * c.y / c.z + H / 2f;
            return c.z > 0.1f && u >= 0 && u < W && v >= 0 && v < H;
        }

        [Test]
        public void AgreementWorksFromAnotherSpotAndCatchesWrongPoses()
        {
            var pa = new Vector3(0, 1.4f, 0);
            var ra = Quaternion.identity;
            var pb = new Vector3(1.2f, 1.3f, 0.6f);          // another spot, looking at the same wall at an angle
            var rb = Quaternion.Euler(5f, -20f, 0);
            var (a, b, m) = Observe(pa, ra, pb, rb);
            Assert.That(m.Count, Is.GreaterThan(50));
            Assert.That(RelocCheck.EpipolarAgreement(a, Pose(pa, ra), b, Pose(pb, rb), m), Is.GreaterThan(0.9f),
                "correct poses agree, even from a different spot and angle");
            // a restarted AR map is shifted AND turned about gravity (the 16 m jump came with a new heading too)
            Assert.That(RelocCheck.EpipolarAgreement(a, Pose(pa, ra), b,
                    Pose(pb + new Vector3(2f, 0, 0), Quaternion.Euler(0, 15f, 0) * rb), m),
                Is.LessThan(RelocCheck.MinAgreement), "AR map restarted: 2 m off and turned 15 deg");
            // side by side + AR heading off: points slide along their epipolar lines, so the rotation test must catch it
            Assert.That(RelocCheck.RotationError(a, Pose(pa, ra), b, Pose(pb, rb), m), Is.LessThan(1f));
            Assert.That(RelocCheck.RotationError(a, Pose(pa, ra), b, Pose(pb, rb * Quaternion.Euler(0, 20f, 0)), m),
                Is.GreaterThan(RelocCheck.MaxRotationErrorDeg), "AR turned 20 deg");
        }

        [Test]
        public void AJumpWhileStandingAtTheSameSpotIsCaughtByDepth()
        {
            var pa = new Vector3(0, 1.4f, 0);
            var pb = new Vector3(0.05f, 1.4f, 0.02f);        // really the same spot, turned a little
            var rb = Quaternion.Euler(0, 8f, 0);
            var (a, b, m) = Observe(pa, Quaternion.identity, pb, rb);
            Assert.That(RelocCheck.EpipolarAgreement(a, Pose(pa, Quaternion.identity), b, Pose(pb, rb), m), Is.GreaterThan(0.9f),
                "same spot, correct AR: rotation check passes");
            Assert.That(RelocCheck.EpipolarAgreement(a, Pose(pa, Quaternion.identity), b, Pose(pb + new Vector3(0, 0, -16f), rb), m),
                Is.LessThan(RelocCheck.MinAgreement), "AR says 16 m away (the second room's jump)");
        }
    }
}

