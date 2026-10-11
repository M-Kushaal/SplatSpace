using System.Collections.Generic;
using UnityEngine;

namespace GSCapture.Core
{
    /// <summary>
    /// Which parts of the room have had a spot close by. The room's footprint is a 0.5 m grid of floor cells
    /// that contain AR feature points (walls, furniture). A cell is covered when a spot is within
    /// <see cref="Reach"/> of it: walls farther than that from every spot are seen only at a distance and at
    /// grazing angles, which is where the first room got holes and "fur". The suggested next spot is the
    /// one that would cover the most uncovered cells (greedy set cover). Pure logic, no AR types.
    /// </summary>
    public sealed class FloorCoverage
    {
        public readonly float Cell, Reach;
        readonly int minPointsPerCell;
        readonly Dictionary<Vector2Int, int> counts = new Dictionary<Vector2Int, int>();
        readonly List<Vector2Int> known = new List<Vector2Int>();
        readonly HashSet<Vector2Int> covered = new HashSet<Vector2Int>();

        public FloorCoverage(float cell = 0.5f, float reach = 1.5f, int minPointsPerCell = 2)
        {
            Cell = cell;
            Reach = reach;
            this.minPointsPerCell = minPointsPerCell;
        }

        public IReadOnlyList<Vector2Int> KnownCells => known;
        public bool IsCovered(Vector2Int c) => covered.Contains(c);
        public int KnownCount => known.Count;
        public int CoveredCount => covered.Count;

        /// <summary>Covered fraction of the known footprint (0 while nothing is known).</summary>
        public float Fraction => known.Count == 0 ? 0f : (float)covered.Count / known.Count;

        public Vector2Int CellOf(Vector2 p) => new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell));
        public Vector2 CenterOf(Vector2Int c) => new Vector2((c.x + 0.5f) * Cell, (c.y + 0.5f) * Cell);

        /// <summary>Rebuild from floor-plane positions (x, z) of AR points and spot centres.</summary>
        public void Update(IEnumerable<Vector2> points, IList<Vector2> spots)
        {
            counts.Clear();
            known.Clear();
            covered.Clear();
            if (points != null)
                foreach (var p in points)
                {
                    var c = CellOf(p);
                    counts.TryGetValue(c, out int n);
                    counts[c] = n + 1;
                }
            foreach (var kv in counts)
                if (kv.Value >= minPointsPerCell) known.Add(kv.Key);
            float r2 = Reach * Reach;
            foreach (var c in known)
            {
                var center = CenterOf(c);
                if (spots == null) break;
                foreach (var s in spots)
                    if ((center - s).sqrMagnitude <= r2)
                    {
                        covered.Add(c);
                        break;
                    }
            }
        }

        /// <summary>Where to stand next: the free cell (no wall/furniture points, inside the footprint's extent)
        /// whose reach holds the most uncovered cells. False when everything is covered. `gain` = cells it covers.</summary>
        public bool SuggestSpot(out Vector2 spot, out int gain)
        {
            spot = Vector2.zero;
            gain = 0;
            var open = new List<Vector2>();
            Vector2Int lo = new Vector2Int(int.MaxValue, int.MaxValue), hi = new Vector2Int(int.MinValue, int.MinValue);
            foreach (var c in known)
            {
                lo = Vector2Int.Min(lo, c);
                hi = Vector2Int.Max(hi, c);
                if (!covered.Contains(c)) open.Add(CenterOf(c));
            }
            if (open.Count == 0) return false;
            float r2 = Reach * Reach;
            for (int x = lo.x; x <= hi.x; x++)
                for (int y = lo.y; y <= hi.y; y++)
                {
                    var c = new Vector2Int(x, y);
                    if (counts.TryGetValue(c, out int n) && n >= minPointsPerCell) continue; // occupied: can't stand there
                    var candidate = CenterOf(c);
                    int g = 0;
                    foreach (var o in open)
                        if ((o - candidate).sqrMagnitude <= r2) g++;
                    if (g > gain)
                    {
                        gain = g;
                        spot = candidate;
                    }
                }
            if (gain == 0) spot = open[0]; // no free cell nearby (e.g. a very cluttered corner): go to the gap itself
            gain = Mathf.Max(gain, 1);
            return true;
        }
    }
}
