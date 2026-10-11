using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace GSCapture.Core.Quality
{
    /// <summary>One saved photo, as the room check sees it.</summary>
    public sealed class CheckPhoto
    {
        public FrameFeatures Features;
        public Matrix4x4 CameraToWorldGL;
        public float Sharpness;   // full-resolution blur score (BlurMeter), compared within its spot
        public int TargetId;      // station*100 + band*30 + sector, or 9000+ for walls-pass / close-up photos
        public string Name;
        public int Epoch;         // PoseGuard tracking epoch: AR poses are only comparable within one

        public int Station => TargetId >= 0 && TargetId < 9000 ? TargetId / 100 : -1;
        public int Band => TargetId >= 0 && TargetId < 9000 ? TargetId % 100 / 30 : -1;
    }

    /// <summary>What the planner knows about coverage (scene captures; null when checking a bundle on the PC).</summary>
    public sealed class CoverageSummary
    {
        public float[][] BandFill;    // per station: fill of level, down, up
        public int[] StationShots;
        public float FloorFraction = -1f;
        public float WallsMeters = -1f;
    }

    public enum Severity { Good, Info, Warn, Bad }

    public sealed class CheckIssue
    {
        public Severity Severity;
        public string Title, Fix;
        public List<int> Photos = new List<int>();  // indices into the checked photo list
        public bool RemoveOnFix;                    // "Fix now" deletes these photos (their sectors re-open)
        public int Station = -1;                    // where to go back to, if any
    }

    public sealed class RoomReport
    {
        public int Photos, Linked, PairsTried, PairsLinked;
        public bool Partial;
        public long Millis;
        public int[] Links;             // linked pairs per photo
        public int[] Component;         // 0 = the largest group of photos that fit together
        public readonly List<CheckIssue> Issues = new List<CheckIssue>();
        public Severity Worst
        {
            get
            {
                var w = Severity.Good;
                foreach (var i in Issues) if (i.Severity > w) w = i.Severity;
                return w;
            }
        }

        public List<int> PhotosToRemove()
        {
            var set = new SortedSet<int>();
            foreach (var i in Issues)
                if (i.RemoveOnFix) foreach (int p in i.Photos) set.Add(p);
            return new List<int>(set);
        }

        public string Summary => $"{Linked} of {Photos} photos fit together" + (Partial ? " (partial check)" : "");
    }

    /// <summary>
    /// The on-phone "room check" that runs when the user taps Finish, so problems are fixed while still in the
    /// room: photos that don't overlap with anything (COLMAP will drop them), spots that don't connect to the
    /// rest (COLMAP builds separate pieces), soft / dark photos, blank views, thin bands and coverage gaps.
    /// Pairs are chosen like the PC pipeline does (worker/gs_pipeline/scene.py select_pairs_scene) so the
    /// verdict mirrors what refinement will see. Pure logic + parallel matching, no AR or UI types.
    /// </summary>
    public sealed class RoomCheck
    {
        public int KNearest = 4, KCross = 3, TimeWindow = 2, KLookAlike = 5, MinInliers = 20;
        public long BudgetMillis = 15000;
        public float SoftRatio = 0.5f, MinBrightness = 35f, MaxBrightness = 230f, MinTexture = 0.03f;
        public float MinBandFill = 0.7f, MinFloor = 0.85f, MinWallsMeters = 3f;
        public int MinStationShots = 24;

        const float LookDepth = 1.5f, MaxAngleDeg = 70f, CrossMinM = 0.6f;

        static Vector3 Center(Matrix4x4 m) => new Vector3(m.m03, m.m13, m.m23);
        static Vector3 Forward(Matrix4x4 m) => new Vector3(-m.m02, -m.m12, -m.m22).normalized;

        /// <summary>Pairs worth matching: neighbours in time + cameras looking at the same spot (incl. other spots).</summary>
        public List<(int, int)> SelectPairs(IReadOnlyList<CheckPhoto> photos)
        {
            int n = photos.Count;
            var c = new Vector3[n];
            var f = new Vector3[n];
            var look = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                c[i] = Center(photos[i].CameraToWorldGL);
                f[i] = Forward(photos[i].CameraToWorldGL);
                look[i] = c[i] + LookDepth * f[i];
            }
            float cosMax = Mathf.Cos(MaxAngleDeg * Mathf.Deg2Rad);
            var pairs = new HashSet<(int, int)>();
            var near = new List<(float d, int j)>(n);
            var cross = new List<(float d, int j)>(n);
            for (int i = 0; i < n; i++)
            {
                near.Clear();
                cross.Clear();
                for (int j = 0; j < n; j++)
                {
                    // AR poses from different epochs can be metres apart for the same view: pair those by looks only
                    if (j == i || photos[j].Epoch != photos[i].Epoch || Vector3.Dot(f[i], f[j]) < cosMax) continue;
                    float d = (look[i] - look[j]).sqrMagnitude;
                    near.Add((d, j));
                    if ((c[i] - c[j]).magnitude >= CrossMinM) cross.Add((d, j));
                }
                near.Sort((a, b) => a.d.CompareTo(b.d));
                cross.Sort((a, b) => a.d.CompareTo(b.d));
                for (int k = 0; k < Math.Min(KNearest, near.Count); k++) pairs.Add(Ordered(i, near[k].j));
                for (int k = 0; k < Math.Min(KCross, cross.Count); k++) pairs.Add(Ordered(i, cross[k].j));
                for (int j = i + 1; j < Math.Min(n, i + TimeWindow + 1); j++) pairs.Add((i, j));
            }
            // Photos that look alike, whatever AR says: after a shake ARCore can restart its map (the second room
            // jumped 16 m), and AR-chosen pairs then never compare photos from before and after the jump.
            var sim = new List<(float s, int j)>(n);
            for (int i = 0; i < n; i++)
            {
                var ti = photos[i].Features?.Tiny;
                if (ti == null) continue;
                sim.Clear();
                for (int j = 0; j < n; j++)
                {
                    var tj = photos[j].Features?.Tiny;
                    if (tj == null || Math.Abs(i - j) <= TimeWindow) continue;
                    float d = 0;
                    for (int k = 0; k < ti.Length; k++) d += ti[k] * tj[k];
                    sim.Add((d, j));
                }
                sim.Sort((a, b) => b.s.CompareTo(a.s));
                for (int k = 0; k < Math.Min(KLookAlike, sim.Count); k++) pairs.Add(Ordered(i, sim[k].j));
            }
            var list = new List<(int, int)>(pairs);
            list.Sort();
            return list;
        }

        static (int, int) Ordered(int a, int b) => a < b ? (a, b) : (b, a);

        public RoomReport Run(IReadOnlyList<CheckPhoto> photos, CoverageSummary coverage = null,
                              CancellationToken cancel = default)
        {
            var sw = Stopwatch.StartNew();
            int n = photos.Count;
            var rep = new RoomReport { Photos = n, Links = new int[n], Component = new int[n] };
            var pairs = SelectPairs(photos);
            var inliers = new int[pairs.Count];
            var done = new bool[pairs.Count];
            Parallel.For(0, pairs.Count, new ParallelOptions { CancellationToken = cancel }, (k, state) =>
            {
                if (sw.ElapsedMilliseconds > BudgetMillis) { state.Stop(); return; }
                var (a, b) = pairs[k];
                var fa = photos[a].Features;
                var fb = photos[b].Features;
                inliers[k] = fa == null || fb == null ? 0 : FeatureMatcher.GeometricInliers(fa, fb, k + 1);
                done[k] = true;
            });

            // links + union-find over linked pairs
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            var unchecked_ = new bool[n];
            for (int k = 0; k < pairs.Count; k++)
            {
                var (a, b) = pairs[k];
                if (!done[k])
                {
                    unchecked_[a] = unchecked_[b] = true;
                    continue;
                }
                rep.PairsTried++;
                if (inliers[k] < MinInliers) continue;
                rep.PairsLinked++;
                rep.Links[a]++;
                rep.Links[b]++;
                parent[Find(a)] = Find(b);
            }
            rep.Partial = rep.PairsTried < pairs.Count;

            // components, largest first = 0
            var sizes = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                sizes[r] = sizes.TryGetValue(r, out int s) ? s + 1 : 1;
            }
            var roots = new List<int>(sizes.Keys);
            roots.Sort((x, y) => sizes[y].CompareTo(sizes[x]));
            var order = new Dictionary<int, int>();
            for (int i = 0; i < roots.Count; i++) order[roots[i]] = i;
            for (int i = 0; i < n; i++)
            {
                rep.Component[i] = order[Find(i)];
                if (rep.Component[i] == 0 && rep.Links[i] > 0) rep.Linked++;
            }

            AddPhotoIssues(photos, rep, unchecked_);
            if (coverage != null) AddCoverageIssues(coverage, rep);
            rep.Issues.Sort((x, y) => y.Severity.CompareTo(x.Severity));
            if (rep.Issues.Count == 0 || rep.Worst < Severity.Warn)
                rep.Issues.Insert(0, new CheckIssue { Severity = Severity.Good, Title = rep.Summary, Fix = "Looks good - save it" });
            rep.Millis = sw.ElapsedMilliseconds;
            return rep;
        }

        void AddPhotoIssues(IReadOnlyList<CheckPhoto> photos, RoomReport rep, bool[] unchecked_)
        {
            int n = photos.Count;
            var isolated = new CheckIssue
            {
                Severity = Severity.Bad, RemoveOnFix = true,
                Fix = "They'll be removed - re-shoot them (keep turning slowly, include furniture or edges)",
            };
            var soft = new CheckIssue { Severity = Severity.Warn, RemoveOnFix = true, Fix = "They'll be removed - re-shoot them holding steadier" };
            var exposure = new CheckIssue { Severity = Severity.Warn, RemoveOnFix = true, Fix = "They'll be removed - re-shoot them (turn on lights / avoid pointing at windows)" };
            var blank = new CheckIssue { Severity = Severity.Info, Fix = "OK for walls and ceilings, but include an edge or furniture where you can" };

            // Photos outside the main group: COLMAP drops them (or builds a separate piece). A spot whose photos are
            // mostly outside it is one problem ("doesn't connect"); stragglers are listed one by one.
            var outside = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                if (rep.Component[i] == 0 || (unchecked_[i] && rep.Links[i] == 0)) continue;
                int st = photos[i].Station;
                if (!outside.TryGetValue(st, out var l)) outside[st] = l = new List<int>();
                l.Add(i);
            }
            foreach (var kv in outside)
            {
                if (kv.Value.Count < 3)
                {
                    isolated.Photos.AddRange(kv.Value);
                    continue;
                }
                int st = kv.Key;
                rep.Issues.Add(new CheckIssue
                {
                    Severity = Severity.Bad, Photos = kv.Value, Station = st,
                    Title = st >= 0 ? $"Spot {st + 1} doesn't connect to the rest of the room ({kv.Value.Count} photos)"
                                    : $"{kv.Value.Count} walls-pass photos don't connect to the rest of the room",
                    Fix = st >= 0 ? $"Walk slowly from spot {st + 1} toward the nearest other spot, taking walls-pass photos on the way"
                                  : "Walk the walls pass again, starting where you can see a spot you already did",
                });
            }
            isolated.Photos.Sort();

            for (int i = 0; i < n; i++)
            {
                var p = photos[i];
                // Blur is judged against the neighbours in capture order: they show similar things, whereas a spot's
                // median mixes busy shelves with blank walls (on the first room that flagged 15 fine photos, this 1).
                var nb = new List<float>(6);
                for (int j = Math.Max(0, i - 3); j <= Math.Min(n - 1, i + 3); j++)
                    if (j != i && photos[j].Station == p.Station) nb.Add(photos[j].Sharpness);
                if (nb.Count >= 2)
                {
                    nb.Sort();
                    float med = nb[nb.Count / 2];
                    if (med > 0 && p.Sharpness < SoftRatio * med) soft.Photos.Add(i);
                }
                if (p.Features != null)
                {
                    if (p.Features.Brightness < MinBrightness || p.Features.Brightness > MaxBrightness) exposure.Photos.Add(i);
                    else if (p.Features.Texture < MinTexture) blank.Photos.Add(i);
                }
            }
            soft.Photos.RemoveAll(isolated.Photos.Contains);
            exposure.Photos.RemoveAll(i => isolated.Photos.Contains(i) || soft.Photos.Contains(i));

            if (isolated.Photos.Count > 0)
            {
                isolated.Title = isolated.Photos.Count == 1 ? "1 photo doesn't fit with the rest"
                                                           : $"{isolated.Photos.Count} photos don't fit with the rest";
                isolated.Station = MostCommonStation(photos, isolated.Photos);
                rep.Issues.Add(isolated);
            }
            if (soft.Photos.Count > 0)
            {
                soft.Title = soft.Photos.Count == 1 ? "1 photo is blurrier than its neighbours"
                                                   : $"{soft.Photos.Count} photos are blurrier than their neighbours";
                soft.Station = MostCommonStation(photos, soft.Photos);
                rep.Issues.Add(soft);
            }
            if (exposure.Photos.Count > 0)
            {
                exposure.Title = $"{exposure.Photos.Count} photo{Plural(exposure.Photos.Count)} too dark or too bright";
                exposure.Station = MostCommonStation(photos, exposure.Photos);
                rep.Issues.Add(exposure);
            }
            if (blank.Photos.Count > 0)
            {
                blank.Title = $"{blank.Photos.Count} photo{Plural(blank.Photos.Count)} show mostly blank wall or ceiling";
                rep.Issues.Add(blank);
            }
        }

        void AddCoverageIssues(CoverageSummary cov, RoomReport rep)
        {
            string[] bandNames = { "level", "down", "ceiling" };
            for (int s = 0; cov.BandFill != null && s < cov.BandFill.Length; s++)
            {
                int shots = cov.StationShots != null && s < cov.StationShots.Length ? cov.StationShots[s] : 0;
                if (shots > 0 && shots < MinStationShots)
                {
                    rep.Issues.Add(new CheckIssue
                    {
                        Severity = Severity.Warn, Station = s, Title = $"Spot {s + 1} has only {shots} photos",
                        Fix = $"Go back to spot {s + 1} and finish the turn - or it adds little",
                    });
                    continue;
                }
                for (int b = 0; b < 3; b++)
                    if (shots > 0 && cov.BandFill[s][b] < MinBandFill)
                    {
                        rep.Issues.Add(new CheckIssue
                        {
                            Severity = Severity.Warn, Station = s,
                            Title = $"Spot {s + 1}: {bandNames[b]} band only {Mathf.RoundToInt(cov.BandFill[s][b] * 100)}%",
                            Fix = $"Go back to spot {s + 1} and fill the {bandNames[b]} band",
                        });
                        break;
                    }
            }
            if (cov.FloorFraction >= 0 && cov.FloorFraction < MinFloor)
                rep.Issues.Add(new CheckIssue
                {
                    Severity = Severity.Warn, Title = $"Only {Mathf.RoundToInt(cov.FloorFraction * 100)}% of the room is near a spot",
                    Fix = "Add a spot at the orange dot on the map",
                });
            if (cov.WallsMeters >= 0 && cov.WallsMeters < MinWallsMeters)
                rep.Issues.Add(new CheckIssue
                {
                    Severity = Severity.Info, Title = "No walls pass yet",
                    Fix = "Walk along the walls ~1 m away, facing them - it fills corners and the ceiling edge",
                });
        }

        static int MostCommonStation(IReadOnlyList<CheckPhoto> photos, List<int> idx)
        {
            var counts = new Dictionary<int, int>();
            foreach (int i in idx) counts[photos[i].Station] = counts.TryGetValue(photos[i].Station, out int c) ? c + 1 : 1;
            int best = -1, bestN = 0;
            foreach (var kv in counts) if (kv.Value > bestN) { best = kv.Key; bestN = kv.Value; }
            return best;
        }

        static string Plural(int n) => n == 1 ? "" : "s";
    }
}
