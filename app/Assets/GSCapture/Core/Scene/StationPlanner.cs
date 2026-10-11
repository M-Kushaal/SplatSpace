using System.Collections.Generic;
using UnityEngine;

namespace GSCapture.Core
{
    /// <summary>Viewing bands of a station spin: phone level, tilted down at floor/furniture, tilted up at the
    /// ceiling. All three are required: the first room (level + down only) came out with a holey, "furry" ceiling.</summary>
    public enum SceneBand { Level = 0, Down = 1, Up = 2 }

    /// <summary>Spin = turning at a spot; Walking = between spots; Closeups = the walls pass (walk along the walls,
    /// facing them) plus free detail photos.</summary>
    public enum ScenePhase { Spin, Walking, Closeups }

    public struct SceneConfig
    {
        public int Sectors;                // azimuth sectors per band (24 = every 15 deg)
        public float LevelPitchDeg, DownPitchDeg, UpPitchDeg;
        public float BandHalfWidthDeg;     // pitch tolerance around a band
        public float SectorToleranceDeg;   // how far from a sector's centre a shot may be (half a sector = anywhere in it)
        public float ArmOffset;            // assumed lens-to-body distance when a station starts
        public float MinBodyDistance;      // lens closer than this to the body = wrist twist, no parallax
        public float MaxBodyDistance;      // farther = the user walked away from the station
        public float MinStationGap;        // a new station must be this far from all others
        public float StationDoneFill;      // fraction of required sectors that makes a station "done"
        public float CloseupMinMove, CloseupMinTurnDeg; // a close-up must differ this much from every kept frame
        public float MinSurfaceDistance;   // closer to a wall than this: out of focus, few features

        public static SceneConfig Default => new SceneConfig
        {
            Sectors = 24,
            LevelPitchDeg = 0f,
            DownPitchDeg = -35f,
            UpPitchDeg = 40f,
            BandHalfWidthDeg = 12f,
            SectorToleranceDeg = 7.5f, // the sharpest frame inside the sector is kept (BestFrameBuffer)
            ArmOffset = 0.4f,
            MinBodyDistance = 0.25f,
            MaxBodyDistance = 1.2f,     // generous: AR heading drift moves the lens circle a little
            MinStationGap = 0.8f,
            StationDoneFill = 0.85f,
            CloseupMinMove = 0.15f,
            CloseupMinTurnDeg = 12f,
            MinSurfaceDistance = 0.4f,
        };
    }

    /// <summary>Where the camera is relative to the current station's spin.</summary>
    public struct SceneMatch
    {
        public int Band;              // SceneBand, or -1 when the pitch is between bands
        public int Sector;            // azimuth sector of the viewing direction
        public float SectorOffsetDeg; // signed distance from the sector centre
        public float PitchDeg;
        public float AzimuthDeg;      // relative to the current station's first view (absolute without a station)
        public float BodyDistance;    // horizontal lens distance from the station centre
        public bool Filled;
    }

    /// <summary>
    /// One spot. Its geometry is stored relative to the spot's AR anchor, not in ARCore's world coordinates:
    /// when ARCore corrects or re-localises its map (an accidental shake jumped the second room 16 m), the
    /// anchor moves with the real room and so does everything here - spot centre, sector origin, lens circle.
    /// Without an anchor (tests, or before it exists) the anchor pose is where the spot started.
    /// </summary>
    public sealed class Station
    {
        public int Index;
        public Vector3 LocalCenter;          // the user's body, in the anchor frame
        public readonly bool[,] Filled;      // [band, sector]
        public readonly List<Vector3> Lenses = new List<Vector3>();    // anchor frame
        public readonly List<Vector3> Forwards = new List<Vector3>();  // anchor frame
        public int Shots;
        public int Epoch;                    // tracking epoch the spot belongs to (PoseGuard)
        public Vector3 AnchorPosition { get; private set; }
        public Quaternion AnchorRotation { get; private set; } = Quaternion.identity;
        readonly float yawOriginLocal;

        public Station(int index, Vector3 center, int sectors, float yawOriginDeg = 0f)
        {
            Index = index;
            AnchorPosition = center;
            LocalCenter = Vector3.zero;
            yawOriginLocal = yawOriginDeg;
            Filled = new bool[3, sectors];
        }

        /// <summary>The user's body in today's world coordinates (follows the anchor).</summary>
        public Vector3 Center
        {
            get => ToWorld(LocalCenter);
            set => LocalCenter = ToLocal(value);
        }

        /// <summary>Azimuth of the spot's first view (sectors are measured from it), in world coordinates.</summary>
        public float YawOriginDeg => Mathf.Repeat(yawOriginLocal + AnchorRotation.eulerAngles.y, 360f);

        /// <summary>The spot's anchor moved (ARCore map correction): everything here moves with it.</summary>
        public void SetAnchor(Vector3 position, Quaternion rotation)
        {
            AnchorPosition = position;
            AnchorRotation = rotation;
        }

        public Vector3 ToLocal(Vector3 world) => Quaternion.Inverse(AnchorRotation) * (world - AnchorPosition);
        public Vector3 ToWorld(Vector3 local) => AnchorPosition + AnchorRotation * local;
        public Vector3 DirToLocal(Vector3 dir) => Quaternion.Inverse(AnchorRotation) * dir;

        /// <summary>Filled fraction of all three bands (level, down, up).</summary>
        public float Fill
        {
            get
            {
                int n = Filled.GetLength(1), f = 0;
                for (int b = 0; b < 3; b++)
                    for (int s = 0; s < n; s++)
                        if (Filled[b, s]) f++;
                return (float)f / (3 * n);
            }
        }

        public int FilledInBand(int band)
        {
            int f = 0;
            for (int s = 0; s < Filled.GetLength(1); s++)
                if (Filled[band, s]) f++;
            return f;
        }
    }

    /// <summary>
    /// Guidance for scene (room) captures. Depth needs the lens to move, so the user turns in place with the
    /// phone at arm's length (lens on a ~0.8 m circle) at several spots ("stations"), filling 24 azimuth
    /// sectors x 3 pitch bands per station, then walks along the walls (the walls pass). Sectors are counted
    /// from the station's first view, so the first photo is always on a sector centre and ARCore's slow
    /// heading drift (~35 deg over the first room) only matters within one spin. Pure logic, no AR types.
    /// </summary>
    public sealed class StationPlanner
    {
        public SceneConfig Config { get; }
        public IReadOnlyList<Station> Stations => stations;
        public Station Current { get; private set; }
        public int CloseupCount { get; private set; }

        /// <summary>
        /// Current tracking epoch. After a jump that couldn't be confirmed visually, spots from earlier epochs live
        /// in a different AR map: they keep their photos but are ignored for "nearest spot" and spacing.
        /// </summary>
        public int Epoch { get; set; }
        public float WallsPathMeters { get; private set; } // distance walked during the walls pass
        public int TotalShots => kept.Count;

        readonly List<Station> stations = new List<Station>();
        readonly List<(Vector3 lens, Vector3 fwd)> kept = new List<(Vector3, Vector3)>();
        Vector3? lastCloseupLens;

        public StationPlanner(SceneConfig? config = null)
        {
            Config = config ?? SceneConfig.Default;
        }

        public static float AzimuthDeg(Vector3 fwd)
        {
            float a = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            return a < 0 ? a + 360f : a;
        }

        public static float PitchDeg(Vector3 fwd) => Mathf.Asin(Mathf.Clamp(fwd.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;

        static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        /// <summary>Body position guessed from the lens: one arm's length behind it, horizontally.</summary>
        public Vector3 BodyFromLens(Vector3 lens, Vector3 fwd)
        {
            Vector3 f = Flat(fwd);
            if (f.sqrMagnitude < 1e-6f) return lens;
            return lens - f.normalized * Config.ArmOffset;
        }

        /// <summary>Distance from the would-be station to the nearest existing one (infinity if none).</summary>
        public float GapToNearestStation(Vector3 lens, Vector3 fwd)
        {
            Vector3 c = BodyFromLens(lens, fwd);
            float best = float.PositiveInfinity;
            foreach (var s in stations)
                if (s.Epoch == Epoch) best = Mathf.Min(best, Flat(c - s.Center).magnitude);
            return best;
        }

        public bool CanStartStationAt(Vector3 lens, Vector3 fwd) => GapToNearestStation(lens, fwd) >= Config.MinStationGap;

        public Station StartStation(Vector3 lens, Vector3 fwd)
        {
            Current = new Station(stations.Count, BodyFromLens(lens, fwd), Config.Sectors, AzimuthDeg(fwd)) { Epoch = Epoch };
            stations.Add(Current);
            return Current;
        }

        /// <summary>Leave station mode (close-ups / walking to the next spot).</summary>
        public void EndStation() => Current = null;

        /// <summary>Index of the spot the user is standing at (within MinStationGap), or -1.</summary>
        public int NearestStation(Vector3 lens, Vector3 fwd)
        {
            Vector3 c = BodyFromLens(lens, fwd);
            int best = -1;
            float bestD = Config.MinStationGap;
            foreach (var s in stations)
            {
                if (s.Epoch != Epoch) continue;
                float d = Flat(c - s.Center).magnitude;
                if (d < bestD) { bestD = d; best = s.Index; }
            }
            return best;
        }

        public void SetStationAnchor(int index, Vector3 position, Quaternion rotation) =>
            stations[index].SetAnchor(position, rotation);

        /// <summary>Go back to an earlier spot (e.g. to re-shoot sectors the room check removed). Sectors keep
        /// their original origin, so the compass lines up with what was already captured.</summary>
        public Station ResumeStation(int index)
        {
            Current = stations[index];
            return Current;
        }

        /// <summary>A station photo was deleted (room check "Fix now"): its sector needs a photo again.</summary>
        public bool Unfill(int targetId)
        {
            if (targetId < 0 || targetId >= 9000) return false;
            int st = targetId / 100, band = targetId % 100 / 30, sector = targetId % 30;
            if (st >= stations.Count || band > 2 || sector >= Config.Sectors || !stations[st].Filled[band, sector]) return false;
            stations[st].Filled[band, sector] = false;
            stations[st].Shots--;
            return true;
        }

        public bool CurrentDone => Current != null && Current.Fill >= Config.StationDoneFill;

        public SceneMatch Evaluate(Vector3 lens, Vector3 fwd)
        {
            fwd = fwd.normalized;
            var m = new SceneMatch { Band = -1, PitchDeg = PitchDeg(fwd), AzimuthDeg = AzimuthDeg(fwd) };
            float width = 360f / Config.Sectors;
            if (Current != null) // the first view sits on the centre of sector 0
                m.AzimuthDeg = Mathf.Repeat(m.AzimuthDeg - Current.YawOriginDeg + 0.5f * width, 360f);
            m.Sector = Mathf.Clamp(Mathf.FloorToInt(m.AzimuthDeg / width), 0, Config.Sectors - 1);
            m.SectorOffsetDeg = m.AzimuthDeg - (m.Sector + 0.5f) * width;
            float[] pitches = { Config.LevelPitchDeg, Config.DownPitchDeg, Config.UpPitchDeg };
            for (int b = 0; b < 3; b++)
                if (Mathf.Abs(m.PitchDeg - pitches[b]) <= Config.BandHalfWidthDeg) m.Band = b;
            if (Current != null)
            {
                m.BodyDistance = Flat(lens - Current.Center).magnitude;
                m.Filled = m.Band >= 0 && Current.Filled[m.Band, m.Sector];
            }
            return m;
        }

        /// <summary>Geometry verdict for a station shot (motion / blur / light are checked by CaptureGate).</summary>
        public GateReason StationPlacement(in SceneMatch m, float surfaceDistance)
        {
            if (Current == null) return GateReason.LeftStation;
            if (m.BodyDistance > Config.MaxBodyDistance) return GateReason.LeftStation;
            if (Current.Shots >= 2 && m.BodyDistance < Config.MinBodyDistance) return GateReason.TooCloseToBody;
            if (surfaceDistance < Config.MinSurfaceDistance) return GateReason.TooCloseToSurface;
            if (m.Band < 0 || Mathf.Abs(m.SectorOffsetDeg) > Config.SectorToleranceDeg) return GateReason.BetweenSlots;
            if (m.Filled) return GateReason.AlreadyCaptured;
            return GateReason.Ok;
        }

        /// <summary>Close-ups: any view that differs enough from every kept frame (position or direction).</summary>
        public GateReason CloseupPlacement(Vector3 lens, Vector3 fwd, float surfaceDistance)
        {
            if (surfaceDistance < Config.MinSurfaceDistance) return GateReason.TooCloseToSurface;
            return IsNovel(lens, fwd) ? GateReason.Ok : GateReason.AlreadyCaptured;
        }

        public bool IsNovel(Vector3 lens, Vector3 fwd)
        {
            float cosTurn = Mathf.Cos(Config.CloseupMinTurnDeg * Mathf.Deg2Rad);
            fwd = fwd.normalized;
            foreach (var (l, f) in kept)
                if ((lens - l).magnitude < Config.CloseupMinMove && Vector3.Dot(fwd, f) > cosTurn) return false;
            return true;
        }

        /// <summary>Record a station shot; returns its target id (station*100 + band*30 + sector).</summary>
        public int MarkStationShot(in SceneMatch m, Vector3 lens, Vector3 fwd)
        {
            Current.Filled[m.Band, m.Sector] = true;
            Current.Shots++;
            Current.Lenses.Add(Current.ToLocal(lens));
            Current.Forwards.Add(Current.DirToLocal(fwd.normalized));
            kept.Add((lens, fwd.normalized));
            RefitCenter(Current);
            return Current.Index * 100 + m.Band * 30 + m.Sector;
        }

        /// <summary>Record a close-up; returns its target id (9000 + n).</summary>
        public int MarkCloseup(Vector3 lens, Vector3 fwd)
        {
            kept.Add((lens, fwd.normalized));
            if (lastCloseupLens.HasValue)
            {
                float step = Flat(lens - lastCloseupLens.Value).magnitude;
                if (step < 1f) WallsPathMeters += step; // a longer jump = a new stretch of wall, not walked
            }
            lastCloseupLens = lens;
            return 9000 + CloseupCount++;
        }

        /// <summary>
        /// Once the user has turned >= 60 deg, the body is where the lens circle's centre is. Keeps the
        /// arm's-length check honest: someone twisting only the wrist has the lens almost still, so the
        /// centre lands on the lens and TooCloseToBody fires.
        /// </summary>
        void RefitCenter(Station s)
        {
            if (s.Lenses.Count < 6) return;
            float span = 0f;
            foreach (var f in s.Forwards) span = Mathf.Max(span, Vector3.Angle(Flat(s.Forwards[0]), Flat(f)));
            if (span < 60f) return;
            Vector3 mean = Vector3.zero;
            foreach (var l in s.Lenses) mean += l;
            mean /= s.Lenses.Count;
            float spread = 0f;
            foreach (var l in s.Lenses) spread = Mathf.Max(spread, Flat(l - mean).magnitude);
            if (spread < 0.1f)
            {
                s.LocalCenter = new Vector3(mean.x, s.LocalCenter.y, mean.z); // turned on the spot: no circle
                return;
            }
            if (FitCircleXZ(s.Lenses, out Vector2 c, out float r) && r <= Config.MaxBodyDistance)
                s.LocalCenter = new Vector3(c.x, s.LocalCenter.y, c.y);
        }

        /// <summary>Least-squares circle through points projected on the floor plane (Kasa fit).</summary>
        public static bool FitCircleXZ(IList<Vector3> pts, out Vector2 center, out float radius)
        {
            center = Vector2.zero;
            radius = 0f;
            int n = pts.Count;
            if (n < 3) return false;
            double mx = 0, mz = 0;
            foreach (var p in pts) { mx += p.x; mz += p.z; }
            mx /= n;
            mz /= n;
            double suu = 0, svv = 0, suv = 0, suuu = 0, svvv = 0, suvv = 0, svuu = 0;
            foreach (var p in pts)
            {
                double u = p.x - mx, v = p.z - mz;
                suu += u * u; svv += v * v; suv += u * v;
                suuu += u * u * u; svvv += v * v * v; suvv += u * v * v; svuu += v * u * u;
            }
            double det = suu * svv - suv * suv;
            if (System.Math.Abs(det) < 1e-9) return false;
            double bu = 0.5 * (suuu + suvv), bv = 0.5 * (svvv + svuu);
            double uc = (bu * svv - bv * suv) / det, vc = (suu * bv - suv * bu) / det;
            center = new Vector2((float)(uc + mx), (float)(vc + mz));
            radius = (float)System.Math.Sqrt(uc * uc + vc * vc + (suu + svv) / n);
            return true;
        }

        /// <summary>Nearest unfilled sector in the current station: the band the phone is in first, then level,
        /// down, up.</summary>
        public bool NextTarget(in SceneMatch m, out int band, out float deltaAzimuthDeg)
        {
            band = -1;
            deltaAzimuthDeg = 0f;
            if (Current == null) return false;
            float width = 360f / Config.Sectors;
            var order = new List<int>(4);
            if (m.Band >= 0 && Current.FilledInBand(m.Band) < Config.Sectors) order.Add(m.Band);
            for (int b = 0; b < 3; b++)
                if (!order.Contains(b)) order.Add(b);
            foreach (int b in order)
            {
                float best = float.PositiveInfinity;
                for (int s = 0; s < Config.Sectors; s++)
                {
                    if (Current.Filled[b, s]) continue;
                    float d = Mathf.DeltaAngle(m.AzimuthDeg, (s + 0.5f) * width);
                    if (Mathf.Abs(d) < Mathf.Abs(best)) best = d;
                }
                if (!float.IsPositiveInfinity(best))
                {
                    band = b;
                    deltaAzimuthDeg = best;
                    return true;
                }
            }
            return false;
        }
    }
}
