using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using GSCapture.Core;
using GSCapture.Core.Quality;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace GSCapture.EditorTools
{
    /// <summary>
    /// Runs the phone's room check (RoomCheck) on an unzipped capture bundle on the PC, to calibrate its
    /// thresholds against what COLMAP actually did with the same photos.
    ///   Menu: Capture > Check capture folder...
    ///   Batch: Unity -batchmode -quit -executeMethod GSCapture.EditorTools.CheckCaptureTool.RunFromEnv
    ///          (env GS_CHECK_DIR = bundle folder with manifest.json; report -> GS_CHECK_OUT or the log)
    /// </summary>
    public static class CheckCaptureTool
    {
        [MenuItem("Capture/Check capture folder...")]
        static void Menu()
        {
            string dir = EditorUtility.OpenFolderPanel("Capture bundle (folder with manifest.json)", "", "");
            if (!string.IsNullOrEmpty(dir)) Debug.Log(Run(dir));
        }

        public static void RunFromEnv()
        {
            string dir = Environment.GetEnvironmentVariable("GS_CHECK_DIR");
            string report = Run(dir);
            string outPath = Environment.GetEnvironmentVariable("GS_CHECK_OUT");
            if (!string.IsNullOrEmpty(outPath)) File.WriteAllText(outPath, report);
            Debug.Log(report);
        }

        static readonly Regex FramePath = new Regex("\"file_path\"\\s*:\\s*\"([^\"]+)\"");
        static readonly Regex Matrix = new Regex("\"transform_matrix\"\\s*:\\s*\\[(.*?)\\]\\s*\\]", RegexOptions.Singleline);
        static readonly Regex Number = new Regex("-?[0-9.]+(?:[eE][-+]?[0-9]+)?");

        static float Field(string block, string name, float fallback)
        {
            var m = Regex.Match(block, $"\"{name}\"\\s*:\\s*(-?[0-9.]+(?:[eE][-+]?[0-9]+)?)");
            return m.Success ? float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : fallback;
        }

        public static string Run(string dir)
        {
            string json = File.ReadAllText(Path.Combine(dir, "manifest.json"));
            int start = json.IndexOf("\"frames\"", StringComparison.Ordinal);
            var starts = new List<int>();
            foreach (Match m in FramePath.Matches(json, start)) starts.Add(m.Index);
            var photos = new List<CheckPhoto>();
            var poses = new List<CamPose>();
            var sw = Stopwatch.StartNew();
            long extractMs = 0;
            var tex = new Texture2D(2, 2);
            for (int k = 0; k < starts.Count; k++)
            {
                string block = json.Substring(starts[k], (k + 1 < starts.Count ? starts[k + 1] : json.Length) - starts[k]);
                string rel = FramePath.Match(block).Groups[1].Value;
                var nums = Number.Matches(Matrix.Match(block).Groups[1].Value);
                var c2w = new Matrix4x4();
                for (int i = 0; i < 16; i++) c2w[i / 4, i % 4] = float.Parse(nums[i].Value, CultureInfo.InvariantCulture);

                tex.LoadImage(File.ReadAllBytes(Path.Combine(dir, rel)));
                var px = tex.GetPixels32();
                int w = tex.width, h = tex.height;
                var rgba = new byte[px.Length * 4];
                var y = new NativeArray<byte>(px.Length, Allocator.Temp);
                for (int i = 0; i < px.Length; i++)
                {
                    rgba[4 * i] = px[i].r;
                    rgba[4 * i + 1] = px[i].g;
                    rgba[4 * i + 2] = px[i].b;
                    y[i] = (byte)((px[i].r * 77 + px[i].g * 150 + px[i].b * 29) >> 8);
                }
                double sharp = Field(block, "blur_score", -1f);
                if (sharp < 0) sharp = BlurMeter.Measure(y, w, h, w).Sharpness;
                y.Dispose();

                var t = Stopwatch.StartNew();
                var gray = OrbLite.GrayThumb(rgba, w, h, out int tw, out int th);
                var feats = OrbLite.Extract(gray, tw, th);
                extractMs += t.ElapsedMilliseconds;
                float sx = (float)tw / w;
                poses.Add(new CamPose
                {
                    C2wGL = c2w, Fx = Field(block, "fl_x", Field(json, "fl_x", 0)) * sx,
                    Fy = Field(block, "fl_y", Field(json, "fl_y", 0)) * sx, Cx = Field(block, "cx", Field(json, "cx", 0)) * sx,
                    Cy = Field(block, "cy", Field(json, "cy", 0)) * sx, Height = th, Valid = true,
                });
                photos.Add(new CheckPhoto
                {
                    Features = feats, CameraToWorldGL = c2w, Sharpness = (float)sharp,
                    TargetId = (int)Field(block, "target_id", 9000), Name = rel,
                    Epoch = (int)Field(block, "tracking_epoch", 0),
                });
            }
            UnityEngine.Object.DestroyImmediate(tex);
            long loadMs = sw.ElapsedMilliseconds;

            if (Environment.GetEnvironmentVariable("GS_CHECK_DEBUG") == "1")
            {
                // consecutive photos (same spot, next sector): they overlap heavily, so they must link
                var dbg = new StringBuilder("consecutive pairs (putative/inliers): ");
                var inl = new List<int>();
                for (int i = 0; i + 1 < photos.Count; i++)
                {
                    if (photos[i].Station < 0 || photos[i].Station != photos[i + 1].Station) continue;
                    var m = FeatureMatcher.Match(photos[i].Features, photos[i + 1].Features);
                    int g = m.Length < FeatureMatcher.MinPutative ? 0 : FeatureMatcher.RansacFundamental(photos[i].Features, photos[i + 1].Features, m);
                    inl.Add(g);
                    if (inl.Count <= 40) dbg.Append($"{m.Length}/{g} ");
                }
                inl.Sort();
                dbg.Append($" | inliers p10 {inl[inl.Count / 10]} p50 {inl[inl.Count / 2]} p90 {inl[inl.Count * 9 / 10]}, " +
                           $"<20: {inl.FindAll(v => v < 20).Count} of {inl.Count}; brightness median ");
                var br = new List<float>();
                foreach (var ph in photos) br.Add(ph.Features.Brightness);
                br.Sort();
                dbg.Append($"{br[br.Count / 2]:F0}, texture median ");
                var tx = new List<float>();
                foreach (var ph in photos) tx.Add(ph.Features.Texture);
                tx.Sort();
                dbg.Append($"{tx[tx.Count / 2]:F2}");
                Debug.Log(dbg.ToString());
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "gs_check_debug.txt"), dbg + Environment.NewLine);
            }

            string split = Environment.GetEnvironmentVariable("GS_CHECK_EPI_SPLIT"); // index of the first photo after a known jump
            if (!string.IsNullOrEmpty(split)) File.AppendAllText(Path.Combine(Path.GetTempPath(), "gs_check_debug.txt"),
                EpipolarReport(photos, poses, int.Parse(split)) + Environment.NewLine);

            var check = new RoomCheck();
            var rep = check.Run(photos);
            var sb = new StringBuilder();
            sb.AppendLine($"[RoomCheck] {dir}");
            sb.AppendLine($"photos {photos.Count}, features/photo {Avg(photos, p => p.Features.Count):F0}, " +
                          $"extract {extractMs / Math.Max(1, photos.Count)} ms/photo (load+decode total {loadMs} ms)");
            sb.AppendLine($"pairs {rep.PairsTried} tried, {rep.PairsLinked} linked; check {rep.Millis} ms; {rep.Summary}");
            var linkHist = new SortedDictionary<int, int>();
            foreach (int l in rep.Links) linkHist[Math.Min(l, 10)] = linkHist.TryGetValue(Math.Min(l, 10), out int c) ? c + 1 : 1;
            sb.Append("links per photo (10 = 10+): ");
            foreach (var kv in linkHist) sb.Append($"{kv.Key}:{kv.Value} ");
            sb.AppendLine();
            foreach (var issue in rep.Issues)
            {
                sb.Append($"  [{issue.Severity}] {issue.Title} -> {issue.Fix}");
                if (issue.Photos.Count > 0)
                {
                    sb.Append("  {");
                    for (int i = 0; i < Math.Min(12, issue.Photos.Count); i++)
                    {
                        var p = photos[issue.Photos[i]];
                        sb.Append($" {Path.GetFileNameWithoutExtension(p.Name)}(t{p.TargetId},links {rep.Links[issue.Photos[i]]})");
                    }
                    sb.Append(issue.Photos.Count > 12 ? " ...}" : " }");
                }
                sb.AppendLine();
            }
            sb.Append("per spot: ");
            var spots = new SortedDictionary<int, (int n, int linked, int main)>();
            for (int i = 0; i < photos.Count; i++)
            {
                spots.TryGetValue(photos[i].Station, out var s);
                spots[photos[i].Station] = (s.n + 1, s.linked + (rep.Links[i] > 0 ? 1 : 0), s.main + (rep.Component[i] == 0 ? 1 : 0));
            }
            foreach (var kv in spots) sb.Append($"{(kv.Key < 0 ? "walls" : "spot " + (kv.Key + 1))}: {kv.Value.n} photos, {kv.Value.linked} linked, {kv.Value.main} in main group; ");
            sb.AppendLine();
            return sb.ToString();
        }

        /// <summary>1 when the full re-check (epipolar + depth + rotation) accepts the pair, else the agreement scaled
        /// below the acceptance line so the percentiles stay informative.</summary>
        static float Verdict(FrameFeatures a, CamPose pa, FrameFeatures b, CamPose pb, List<(int a, int b)> m)
        {
            float agr = RelocCheck.EpipolarAgreement(a, pa, b, pb, m);
            float rot = RelocCheck.RotationError(a, pa, b, pb, m);
            bool ok = agr >= RelocCheck.MinAgreement && (rot < 0 || rot <= RelocCheck.MaxRotationErrorDeg);
            return ok ? 1f : Math.Min(agr, RelocCheck.MinAgreement - 0.01f);
        }

        /// <summary>AR-vs-image agreement for overlapping photo pairs on the same side of a known map jump vs across it.</summary>
        static string EpipolarReport(List<CheckPhoto> photos, List<CamPose> poses, int split)
        {
            var same = new List<float>();
            var across = new List<float>();
            int n = photos.Count;
            for (int i = 0; i + 1 < n; i++)
            {
                if ((i < split) != (i + 1 < split)) continue;
                var m = RelocCheck.VerifiedMatches(photos[i].Features, photos[i + 1].Features, i, out int inl);
                if (inl >= RelocCheck.MinInliers)
                    same.Add(Verdict(photos[i].Features, poses[i], photos[i + 1].Features, poses[i + 1], m));
            }
            for (int i = 0; i < split; i += 2)
            {
                var sims = new List<(float s, int j)>();
                for (int j = split; j < n; j++)
                {
                    float d = 0;
                    var a = photos[i].Features.Tiny;
                    var b = photos[j].Features.Tiny;
                    for (int k = 0; k < a.Length; k++) d += a[k] * b[k];
                    sims.Add((d, j));
                }
                sims.Sort((x, y) => y.s.CompareTo(x.s));
                for (int k = 0; k < Math.Min(4, sims.Count); k++)
                {
                    int j = sims[k].j;
                    var m = RelocCheck.VerifiedMatches(photos[i].Features, photos[j].Features, i + j, out int inl);
                    if (inl >= RelocCheck.MinInliers)
                        across.Add(Verdict(photos[i].Features, poses[i], photos[j].Features, poses[j], m));
                }
            }
            string P(List<float> v)
            {
                if (v.Count == 0) return "none";
                v.Sort();
                int ok = v.FindAll(x => x >= RelocCheck.MinAgreement).Count;
                return $"n={v.Count} p10={v[v.Count / 10]:F2} p50={v[v.Count / 2]:F2} p90={v[v.Count * 9 / 10]:F2} " +
                       $"accepted {ok}/{v.Count}";
            }
            return $"epipolar agreement | same map: {P(same)} | across the jump: {P(across)}";
        }

        static double Avg(List<CheckPhoto> l, Func<CheckPhoto, int> f)
        {
            double s = 0;
            foreach (var p in l) s += f(p);
            return l.Count > 0 ? s / l.Count : 0;
        }
    }
}
