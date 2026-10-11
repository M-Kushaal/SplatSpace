using System.Collections.Generic;
using GSCapture.Core;
using GSCapture.UI;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GSCapture
{
    /// <summary>
    /// Scene (room) capture, the app's second capture type. CaptureApp owns the AR session, the shared
    /// quality gate (tracking, motion, light, blur) and frame saving; this class decides *where* a photo is
    /// worth taking (StationPlanner), tracks which parts of the room still need a spot (FloorCoverage),
    /// coaches the user, draws the spot rings in AR, and drives SceneHud.
    ///
    /// The first room (161 photos, 4 spots, no ceiling band) had holes and "fur" wherever few photos looked;
    /// a whole room needs ~400-600 photos: 4+ spots spread over the room, 3 bands each, and a walls pass.
    /// </summary>
    public sealed class SceneCaptureFlow
    {
        public const int MinFrames = 200, MaxFrames = 700, MinStations = 4, MinStationShots = 24;
        public const float GoodCoverage = 0.85f;

        public readonly StationPlanner Planner = new StationPlanner();
        public readonly FloorCoverage Floor = new FloorCoverage();
        public ScenePhase Phase { get; private set; }
        public float FloorY { get; private set; }

        readonly SceneHud hud;
        readonly ARAnchorManager anchors;
        readonly Material lineMaterial;
        readonly List<GameObject> spotRings = new List<GameObject>();      // anchor roots, index = spot index
        readonly List<Vector3> lastAnchorPos = new List<Vector3>();
        readonly List<float> ringFloorOffset = new List<float>();
        ARPlane floorPlane;
        SceneMatch lastMatch;
        bool lastWasStation;
        Vector2? suggestedSpot;
        readonly SliceHold manualHold = new SliceHold();

        /// <summary>Seconds the phone must stay on the same slice (direction + tilt) before Manual capture unlocks:
        /// it is for slices the automatic capture can't get, not a way to skip it.</summary>
        public const float ManualHoldSeconds = 3f;

        public float ManualHeld => manualHold.Held;
        public bool ManualReady => ManualHeld >= ManualHoldSeconds;

        /// <summary>Track how long the phone has pointed at the same slice (call every frame).</summary>
        public void UpdateManualHold(Vector3 lens, Vector3 fwd, float now) => manualHold.Update(Planner.ViewKey(lens, fwd), now);

        /// <summary>After a manual photo the next one needs another full hold.</summary>
        public void ResetManualHold(float now) => manualHold.Restart(now);

        public SceneCaptureFlow(SceneHud hud, ARAnchorManager anchors, Material lineMaterial)
        {
            this.hud = hud;
            this.anchors = anchors;
            this.lineMaterial = lineMaterial;
        }

        public void Begin(Vector3 lens, Vector3 fwd, float floorY, ARPlane floor)
        {
            FloorY = floorY;
            floorPlane = floor;
            StartStation(lens, fwd);
            hud.Show(true);
        }

        /// <summary>Start a spot here, or resume the earlier spot the user is standing at (to re-fill sectors).</summary>
        public void StartStation(Vector3 lens, Vector3 fwd)
        {
            int near = Planner.NearestStation(lens, fwd);
            if (near >= 0)
            {
                Planner.ResumeStation(near);
                Phase = ScenePhase.Spin;
                return;
            }
            var s = Planner.StartStation(lens, fwd);
            Phase = ScenePhase.Spin;
            spotRings.Add(MakeSpotAnchor(s.AnchorPosition));
            lastAnchorPos.Add(s.AnchorPosition);
            ringFloorOffset.Add(FloorY - s.AnchorPosition.y);
        }

        public void LeaveStation()
        {
            Planner.EndStation();
            Phase = ScenePhase.Walking;
        }

        public void ToggleCloseups()
        {
            Planner.EndStation();
            Phase = Phase == ScenePhase.Closeups ? ScenePhase.Walking : ScenePhase.Closeups;
        }

        /// <summary>Where-to-shoot verdict for this frame; remembered for <see cref="Commit"/>.</summary>
        public GateReason Placement(Vector3 lens, Vector3 fwd, float surfaceDistance)
        {
            lastWasStation = Phase == ScenePhase.Spin;
            if (lastWasStation)
            {
                lastMatch = Planner.Evaluate(lens, fwd);
                return Planner.StationPlacement(lastMatch, surfaceDistance);
            }
            return Planner.CloseupPlacement(lens, fwd, surfaceDistance);
        }

        /// <summary>The last <see cref="Placement"/> was for a station sector (photos are buffered, best per sector).</summary>
        public bool LastWasStation => lastWasStation;
        public SceneMatch LastMatch => lastMatch;

        /// <summary>Buffer key of a station sector (unique within the current station).</summary>
        public int SectorKey(in SceneMatch m) => m.Band * Planner.Config.Sectors + m.Sector;

        /// <summary>The photo was saved: mark its sector/viewpoint. Returns the frame's target_id.</summary>
        public int Commit(Vector3 lens, Vector3 fwd)
        {
            return lastWasStation ? Planner.MarkStationShot(lastMatch, lens, fwd) : Planner.MarkCloseup(lens, fwd);
        }

        /// <summary>A manual (shutter-button) photo: fills the sector it points at if that's open, else it counts as a
        /// walls-pass / close-up photo. Returns its target_id.</summary>
        public int CommitManual(Vector3 lens, Vector3 fwd)
        {
            if (Phase == ScenePhase.Spin && Planner.Current != null)
            {
                var m = Planner.Evaluate(lens, fwd);
                if (m.Band < 0) m.Band = Planner.NearestBand(m.PitchDeg); // between bands: the closest one
                if (!Planner.Current.Filled[m.Band, m.Sector]) return Planner.MarkStationShot(m, lens, fwd);
            }
            return Planner.MarkCloseup(lens, fwd);
        }

        /// <summary>A buffered station photo is being saved: mark the sector it was taken for.</summary>
        public int CommitStation(in SceneMatch m, Vector3 lens, Vector3 fwd)
        {
            if (Planner.Current == null || Planner.Current.Filled[m.Band, m.Sector]) return -1;
            return Planner.MarkStationShot(m, lens, fwd);
        }

        int UsableStations()
        {
            int n = 0;
            foreach (var s in Planner.Stations) if (s.Shots >= MinStationShots) n++;
            return n;
        }

        public bool CanFinish(int frames) => frames >= MinFrames && UsableStations() >= MinStations;

        bool RoomCovered => Floor.KnownCount > 0 && Floor.Fraction >= GoodCoverage && UsableStations() >= MinStations;

        public string FinishBlockedLabel(int frames)
        {
            int spots = Mathf.Max(0, MinStations - UsableStations());
            return spots > 0 ? $"{spots} more spot{(spots > 1 ? "s" : "")}" : $"{Mathf.Max(0, MinFrames - frames)} more photos";
        }

        /// <summary>Ring value: the current spot's fill while spinning, else the average over spots.</summary>
        public float Coverage
        {
            get
            {
                if (Planner.Current != null) return Planner.Current.Fill;
                if (Planner.Stations.Count == 0) return 0f;
                float f = 0f;
                foreach (var s in Planner.Stations) f += s.Fill;
                return f / Planner.Stations.Count;
            }
        }

        public string CoverageLabel => Planner.Current != null ? "of this spot" : "avg. per spot";

        /// <summary>Coaching for a failed placement (scene wording for the sector reasons).</summary>
        public string Coach(GateReason r)
        {
            if (Phase != ScenePhase.Spin && r == GateReason.AlreadyCaptured) return "Keep walking slowly along the wall";
            if (r == GateReason.BetweenSlots) return "Turn to the yellow sector";
            if (r == GateReason.AlreadyCaptured) return "Done here - keep turning";
            return CaptureGate.Coaching(r);
        }

        /// <summary>The next-move line under the compass.</summary>
        public string Hint(Vector3 lens, Vector3 fwd, int frames)
        {
            if (frames >= MaxFrames) return "✓  That's plenty of photos - tap Finish";
            switch (Phase)
            {
                case ScenePhase.Spin:
                    if (Planner.CurrentDone)
                        return RoomCovered ? "✓  Spots done - tap Walls pass, the last step" : "✓  Spot done - tap Next spot, go to the orange dot";
                    var m = Planner.Evaluate(lens, fwd);
                    if (!Planner.NextTarget(m, out int band, out float dAz)) return "";
                    if (band != m.Band)
                        return band == (int)SceneBand.Down ? "↓  Tilt the phone down toward the floor" :
                               band == (int)SceneBand.Up ? "↑  Tilt the phone up toward the ceiling" : "→  Hold the phone level, at chest height";
                    if (Mathf.Abs(dAz) < 8f) return "●  Keep turning slowly through the yellow sector";
                    return dAz > 0 ? "↻  Turn right (whole body, arm out)" : "↺  Turn left (whole body, arm out)";
                case ScenePhase.Walking:
                    if (RoomCovered) return "✓  Spots done - tap Walls pass, the last step";
                    if (suggestedSpot.HasValue)
                    {
                        float d = (suggestedSpot.Value - new Vector2(lens.x, lens.z)).magnitude;
                        if (d > 0.6f) return $"Walk to the orange dot on the map ({d:0.0} m)";
                    }
                    int near = Planner.NearestStation(lens, fwd);
                    if (near >= 0 && Planner.Stations[near].Fill < 1f) return $"Tap Resume spot {near + 1} to re-shoot its missing photos";
                    if (!Planner.CanStartStationAt(lens, fwd)) return "Walk 1-2 m away from the last spot";
                    return "Good spot - tap Start spot here";
                default:
                    return Planner.WallsPathMeters < 3f
                        ? "Walk along the walls ~1 m away, facing them, phone level"
                        : "Then once more with the phone tilted up to where wall meets ceiling";
            }
        }

        public void UpdateHud(Vector3 lens, Vector3 fwd, IEnumerable<Vector3> arPoints, bool mapDue)
        {
            var station = Planner.Current;
            var m = Planner.Evaluate(lens, fwd);
            int tBand = -1, tSector = -1;
            if (station != null && Planner.NextTarget(m, out int band, out float dAz))
            {
                tBand = band;
                float width = 360f / Planner.Config.Sectors;
                tSector = Mathf.FloorToInt(Mathf.Repeat(m.AzimuthDeg + dAz, 360f) / width) % Planner.Config.Sectors;
            }
            string title = Phase == ScenePhase.Spin ? $"Spot {Planner.Stations.Count}" :
                           Phase == ScenePhase.Walking ? "Walk" : "Walls";
            string sub = Phase == ScenePhase.Spin ? $"{Mathf.RoundToInt(station.Fill * 100)}%" :
                         Phase == ScenePhase.Walking ? $"{Planner.Stations.Count} spots · room {Mathf.RoundToInt(Floor.Fraction * 100)}%"
                                                     : $"{Planner.WallsPathMeters:0.0} m";
            hud.SetCompass(m.AzimuthDeg, station, tBand, tSector, m.Band, title, sub);
            hud.SetManual(ManualReady, ManualHoldSeconds - ManualHeld);
            int resume = Phase == ScenePhase.Spin ? -1 : Planner.NearestStation(lens, fwd);
            bool resumable = resume >= 0 && Planner.Stations[resume].Fill < 1f;
            hud.SetButtons(Phase, Planner.CanStartStationAt(lens, fwd) || resumable, Planner.CurrentDone, resumable ? resume : -1,
                UsableStations() >= MinStations, RoomCovered);
            for (int i = 0; i < spotRings.Count; i++)
            {
                if (spotRings[i] == null) continue;
                var lr = spotRings[i].GetComponentInChildren<LineRenderer>();
                bool current = station != null && i == station.Index;
                lr.enabled = Planner.Stations[i].Epoch == Planner.Epoch; // spots from an older AR map would be misplaced
                lr.startColor = lr.endColor = current ? Theme.WithAlpha(Theme.Accent, 0.95f) : Theme.WithAlpha(Theme.Success, 0.6f);
            }
            if (!mapDue) return;
            var pts = new List<Vector2>();
            if (arPoints != null)
            {
                int k = 0;
                foreach (var p in arPoints)
                    if (k++ % 3 == 0 && p.y > FloorY + 0.15f) pts.Add(new Vector2(p.x, p.z)); // above the floor = walls, furniture
            }
            var spots = new List<Vector2>();
            foreach (var s in Planner.Stations)
                if (s.Epoch == Planner.Epoch) spots.Add(new Vector2(s.Center.x, s.Center.z));
            Floor.Update(pts, spots);
            suggestedSpot = Floor.SuggestSpot(out Vector2 next, out _) ? next : (Vector2?)null;
            hud.SetMap(pts, spots, new Vector2(lens.x, lens.z), StationPlanner.AzimuthDeg(fwd), Planner.Config.ArmOffset,
                Floor, suggestedSpot);
        }

        /// <summary>Coverage facts for the room check (Core.Quality.RoomCheck).</summary>
        public GSCapture.Core.Quality.CoverageSummary CheckCoverage()
        {
            int n = Planner.Stations.Count;
            var cov = new GSCapture.Core.Quality.CoverageSummary
            {
                BandFill = new float[n][], StationShots = new int[n],
                FloorFraction = Floor.KnownCount > 0 ? Floor.Fraction : -1f,
                WallsMeters = Planner.WallsPathMeters,
            };
            for (int i = 0; i < n; i++)
            {
                var st = Planner.Stations[i];
                cov.StationShots[i] = st.Shots;
                cov.BandFill[i] = new float[3];
                for (int b = 0; b < 3; b++) cov.BandFill[i][b] = (float)st.FilledInBand(b) / Planner.Config.Sectors;
            }
            return cov;
        }

        /// <summary>
        /// Copy every spot anchor's pose (ARCore moves anchors when it corrects its map) into the planner and move
        /// the rings with it, so planner, rings and map always agree. Returns the largest jump of a current-epoch
        /// anchor since the last frame (m), for the PoseGuard.
        /// </summary>
        public float SyncAnchors()
        {
            float jump = 0f;
            for (int i = 0; i < spotRings.Count && i < Planner.Stations.Count; i++)
            {
                var root = spotRings[i];
                if (root == null) continue;
                var a = root.GetComponent<ARAnchor>();
                if (a == null || a.trackingState != TrackingState.Tracking) continue;
                var st = Planner.Stations[i];
                Vector3 pos = root.transform.position;
                if (st.Epoch == Planner.Epoch) jump = Mathf.Max(jump, (pos - lastAnchorPos[i]).magnitude);
                lastAnchorPos[i] = pos;
                Planner.SetStationAnchor(i, pos, root.transform.rotation);
                var ring = root.transform.Find("SpotRing");
                if (ring != null) ring.localPosition = new Vector3(st.LocalCenter.x, ringFloorOffset[i], st.LocalCenter.z);
            }
            return jump;
        }

        /// <summary>extensions.scene for the manifest (OpenGL world: z flipped), incl. pose events and spot epochs.</summary>
        public string ExtensionJson(IReadOnlyList<(float time, PoseEvent kind, float size)> poseEvents = null)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var stations = new List<(Vector3, int)>();
            foreach (var s in Planner.Stations)
                stations.Add((PoseConverter.ToOpenGLPoint(new Vector3(s.Center.x, FloorY, s.Center.z)), s.Shots));
            string json = CaptureBundleWriter.SceneExtensionJson(FloorY, stations, Planner.CloseupCount);
            var sb = new System.Text.StringBuilder(json, 0, json.Length - 1, json.Length + 256);
            sb.Append(", \"station_epochs\": [");
            for (int i = 0; i < Planner.Stations.Count; i++) sb.Append(i > 0 ? ", " : "").Append(Planner.Stations[i].Epoch);
            sb.Append("], \"pose_events\": [");
            if (poseEvents != null)
                for (int i = 0; i < poseEvents.Count; i++)
                    sb.Append(i > 0 ? ", " : "").Append("{\"t\": ").Append(poseEvents[i].time.ToString("0.00", inv))
                      .Append(", \"kind\": \"").Append(poseEvents[i].kind).Append("\", \"size\": ")
                      .Append(poseEvents[i].size.ToString("0.###", inv)).Append("}");
            return sb.Append("]}").ToString();
        }

        /// <summary>A "stand here" ring on the floor, on its own free AR anchor. Not attached to a plane: plane
        /// detection is off during room captures, and plane-attached anchors then stopped following the map.</summary>
        GameObject MakeSpotAnchor(Vector3 center)
        {
            var root = new GameObject("SpotAnchor");
            root.transform.SetPositionAndRotation(center, Quaternion.identity);
            try
            {
                root.AddComponent<ARAnchor>(); // ARCore keeps this pose locked to the real room
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[GSCapture] spot anchor failed: {e.Message}");
            }
            var go = new GameObject("SpotRing");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = new Vector3(0f, FloorY - center.y, 0f);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = lineMaterial;
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = 64;
            lr.widthMultiplier = 0.012f;
            float r = Planner.Config.ArmOffset;
            for (int i = 0; i < 64; i++)
            {
                float t = 2 * Mathf.PI * i / 64;
                lr.SetPosition(i, new Vector3(Mathf.Cos(t) * r, 0.01f, Mathf.Sin(t) * r)); // local to the ring
            }
            return root;
        }

        public void Clear()
        {
            foreach (var r in spotRings)
            {
                if (r == null) continue;
                var a = r.GetComponent<ARAnchor>();
                if (a != null && anchors != null && anchors.TryRemoveAnchor(a)) continue;
                Object.Destroy(r);
            }
            spotRings.Clear();
            hud.Show(false);
        }
    }
}
