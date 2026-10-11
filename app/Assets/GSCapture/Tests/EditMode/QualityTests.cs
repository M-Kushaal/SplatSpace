using System.Collections.Generic;
using GSCapture.Core;
using GSCapture.Core.Quality;
using NUnit.Framework;
using UnityEngine;

namespace GSCapture.Tests
{
    public class QualityTests
    {
        /// <summary>A blocky random "room surface": cells of random grey levels, like posters, shelves, furniture edges.</summary>
        static byte[] World(int w, int h, int seed, int cell = 9)
        {
            var rng = new System.Random(seed);
            int cw = w / cell + 1, ch = h / cell + 1;
            var cells = new byte[cw * ch];
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

        const int W = 480, H = 270;

        [Test]
        public void PopCountMatchesNaive()
        {
            var rng = new System.Random(1);
            for (int k = 0; k < 100; k++)
            {
                ulong v = ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();
                int naive = 0;
                for (int b = 0; b < 64; b++) if (((v >> b) & 1) != 0) naive++;
                Assert.That(FeatureMatcher.PopCount(v), Is.EqualTo(naive));
            }
        }

        [Test]
        public void OverlappingViewsLinkAndUnrelatedViewsDont()
        {
            var world = World(900, 400, 7);
            var a = OrbLite.Extract(Crop(world, 900, 0, 40, W, H), W, H);
            var b = OrbLite.Extract(Crop(world, 900, 140, 60, W, H), W, H);  // shifted 140 / 20 px: ~70% overlap
            var other = OrbLite.Extract(World(W, H, 99), W, H);
            Assert.That(a.Count, Is.GreaterThan(250), "a textured view gives plenty of features");
            Assert.That(a.Texture, Is.GreaterThan(0.1f));
            int linked = FeatureMatcher.GeometricInliers(a, b);
            int unrelated = FeatureMatcher.GeometricInliers(a, other);
            Assert.That(linked, Is.GreaterThan(60));
            Assert.That(unrelated, Is.LessThan(20));
        }

        [Test]
        public void BlankWallHasNoTextureAndNoFeatures()
        {
            var blank = new byte[W * H];
            for (int i = 0; i < blank.Length; i++) blank[i] = 200;
            var f = OrbLite.Extract(blank, W, H);
            Assert.That(f.Count, Is.EqualTo(0));
            Assert.That(f.Texture, Is.LessThan(0.01f));
            Assert.That(f.Brightness, Is.EqualTo(200f).Within(0.5f));
        }

        static Matrix4x4 Pose(float x, float yawDeg)
        {
            // OpenGL camera-to-world: at (x, 1.4, 0) looking along -z rotated by yaw
            var m = Matrix4x4.TRS(new Vector3(x, 1.4f, 0f), Quaternion.Euler(0, yawDeg, 0), Vector3.one);
            return m;
        }

        [Test]
        public void RoomCheckFindsStragglersDisconnectedSpotsAndSoftPhotos()
        {
            var main = World(1400, 400, 3);
            var island = World(1000, 400, 4);
            var photos = new List<CheckPhoto>();
            // spot 1: 8 photos sweeping along one wall (each overlaps its neighbours)
            for (int i = 0; i < 8; i++)
                photos.Add(new CheckPhoto
                {
                    Features = OrbLite.Extract(Crop(main, 1400, i * 110, 40, W, H), W, H), CameraToWorldGL = Pose(i * 0.1f, 0),
                    Sharpness = i == 3 ? 0.3f : 1f, TargetId = 0 * 100 + i, Name = $"a{i}",
                });
            // spot 2: 4 photos of a wall nobody else saw -> a separate piece
            for (int i = 0; i < 4; i++)
                photos.Add(new CheckPhoto
                {
                    Features = OrbLite.Extract(Crop(island, 1000, i * 110, 40, W, H), W, H), CameraToWorldGL = Pose(2f + i * 0.1f, 0),
                    Sharpness = 1f, TargetId = 1 * 100 + i, Name = $"b{i}",
                });
            // a walls-pass photo of something unrelated
            photos.Add(new CheckPhoto
            {
                Features = OrbLite.Extract(World(W, H, 55), W, H), CameraToWorldGL = Pose(0.4f, 0), Sharpness = 1f,
                TargetId = 9000, Name = "c0",
            });

            var rep = new RoomCheck().Run(photos);
            Assert.That(rep.Partial, Is.False);
            Assert.That(rep.Linked, Is.EqualTo(8), "the 8 photos of spot 1 form the main group");
            Assert.That(rep.Worst, Is.EqualTo(Severity.Bad));
            var spot2 = rep.Issues.Find(i => i.Title.StartsWith("Spot 2 doesn't connect"));
            Assert.That(spot2, Is.Not.Null);
            Assert.That(spot2.Photos.Count, Is.EqualTo(4));
            Assert.That(spot2.RemoveOnFix, Is.False, "a disconnected spot is bridged, not deleted");
            var straggler = rep.Issues.Find(i => i.Title.Contains("doesn't fit"));
            Assert.That(straggler.Photos, Is.EqualTo(new[] { 12 }));
            var soft = rep.Issues.Find(i => i.Title.Contains("blurrier"));
            Assert.That(soft.Photos, Is.EqualTo(new[] { 3 }));
            Assert.That(rep.PhotosToRemove(), Is.EqualTo(new[] { 3, 12 }));
        }

        [Test]
        public void CoverageIssuesAndAllGood()
        {
            var main = World(1400, 400, 3);
            var photos = new List<CheckPhoto>();
            for (int i = 0; i < 6; i++)
                photos.Add(new CheckPhoto
                {
                    Features = OrbLite.Extract(Crop(main, 1400, i * 110, 40, W, H), W, H), CameraToWorldGL = Pose(i * 0.1f, 0),
                    Sharpness = 1f, TargetId = i,
                });
            var good = new CoverageSummary
            {
                BandFill = new[] { new[] { 1f, 0.9f, 0.9f } }, StationShots = new[] { 60 }, FloorFraction = 0.9f, WallsMeters = 6f,
            };
            var rep = new RoomCheck().Run(photos, good);
            Assert.That(rep.Worst, Is.EqualTo(Severity.Good));
            Assert.That(rep.Issues[0].Title, Is.EqualTo("6 of 6 photos fit together"));

            var thin = new CoverageSummary
            {
                BandFill = new[] { new[] { 1f, 0.9f, 0.4f } }, StationShots = new[] { 50 }, FloorFraction = 0.5f, WallsMeters = 0f,
            };
            rep = new RoomCheck().Run(photos, thin);
            Assert.That(rep.Issues.Exists(i => i.Title == "Spot 1: ceiling band only 40%" && i.Station == 0), Is.True);
            Assert.That(rep.Issues.Exists(i => i.Title.StartsWith("Only 50% of the room")), Is.True);
            Assert.That(rep.Issues.Exists(i => i.Title == "No walls pass yet"), Is.True);
            Assert.That(rep.PhotosToRemove(), Is.Empty);
        }

        [Test]
        public void UnfillReopensTheSectorAndSpotsCanBeResumed()
        {
            var p = new StationPlanner();
            var fwd = new Vector3(Mathf.Sin(7.5f * Mathf.Deg2Rad), 0, Mathf.Cos(7.5f * Mathf.Deg2Rad));
            var lens = new Vector3(0, 1.4f, 0) + fwd * 0.4f;
            p.StartStation(lens, fwd);
            var m = p.Evaluate(lens, fwd);
            int id = p.MarkStationShot(m, lens, fwd);
            Assert.That(p.Current.Filled[0, 0], Is.True);
            p.EndStation();
            Assert.That(p.Unfill(id), Is.True);
            Assert.That(p.Stations[0].Filled[0, 0], Is.False);
            Assert.That(p.Stations[0].Shots, Is.EqualTo(0));
            Assert.That(p.Unfill(id), Is.False, "already open");
            Assert.That(p.Unfill(9003), Is.False, "walls-pass photos have no sector");
            Assert.That(p.NearestStation(lens, fwd), Is.EqualTo(0));
            Assert.That(p.NearestStation(lens + new Vector3(2f, 0, 0), fwd), Is.EqualTo(-1));
            p.ResumeStation(0);
            Assert.That(p.StationPlacement(p.Evaluate(lens, fwd), 3f), Is.EqualTo(GateReason.Ok), "the sector can be shot again");
        }
    }
}
