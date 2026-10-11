using System.Collections.Generic;
using UnityEngine;

namespace GSCapture.Core.Quality
{
    /// <summary>An earlier photo the live view can be compared with: its features and where AR says it was taken
    /// (in today's coordinates, i.e. re-expressed through its spot anchor).</summary>
    public struct RelocCandidate
    {
        public FrameFeatures Features;
        public Vector3 Position, Forward;
    }

    public struct RelocResult
    {
        public bool Matched;       // the live view shows the same thing as an earlier photo
        public bool Consistent;    // ... and AR agrees the phone is looking at the same spot
        public int Candidate, Inliers;
        public float LookDistance, AngleDeg;
    }

    /// <summary>
    /// After a pose jump, is ARCore back in the same map? The live camera view is matched (OrbLite +
    /// FeatureMatcher) against earlier photos. A strong match means the phone sees what that photo saw; if AR
    /// also puts the phone looking at the same place (look-points within LookTolerance, directions within
    /// AngleTolerance) the map is the old one and capture can continue safely. A strong match with an AR pose
    /// somewhere else means the map moved (the second room's map jumped 16 m) - then capture must not silently
    /// continue in the old coordinates.
    /// </summary>
    public static class RelocCheck
    {
        public const int MinInliers = 25;
        public const float LookDepth = 1.5f, LookTolerance = 0.75f, AngleTolerance = 30f;

        /// <summary>Which earlier photos to try: the ones AR says look the same way (if AR is right) plus the most
        /// recent ones (the user was told to point back at what they just scanned).</summary>
        public static List<int> PickCandidates(IReadOnlyList<RelocCandidate> c, Vector3 pos, Vector3 fwd, int byPose = 15,
                                               int recent = 40)
        {
            var picked = new List<int>();
            var seen = new HashSet<int>();
            Vector3 look = pos + LookDepth * fwd.normalized;
            var order = new List<(float d, int i)>();
            for (int i = 0; i < c.Count; i++)
                if (c[i].Features != null) order.Add(((c[i].Position + LookDepth * c[i].Forward.normalized - look).sqrMagnitude, i));
            order.Sort((a, b) => a.d.CompareTo(b.d));
            for (int k = 0; k < Mathf.Min(byPose, order.Count); k++) if (seen.Add(order[k].i)) picked.Add(order[k].i);
            for (int i = c.Count - 1, n = 0; i >= 0 && n < recent; i--)
                if (c[i].Features != null) { n++; if (seen.Add(i)) picked.Add(i); }
            return picked;
        }

        /// <summary>Earlier photos that look most like the live view (tiny-image similarity) - found even when AR is
        /// completely wrong about where the phone is.</summary>
        public static List<int> PickByLooks(FrameFeatures live, IReadOnlyList<RelocCandidate> c, int k = 12)
        {
            var order = new List<(float s, int i)>();
            if (live?.Tiny == null) return new List<int>();
            for (int i = 0; i < c.Count; i++)
            {
                var t = c[i].Features?.Tiny;
                if (t == null) continue;
                float d = 0;
                for (int j = 0; j < t.Length; j++) d += t[j] * live.Tiny[j];
                order.Add((d, i));
            }
            order.Sort((a, b) => b.s.CompareTo(a.s));
            var pick = new List<int>();
            for (int n = 0; n < Mathf.Min(k, order.Count); n++) pick.Add(order[n].i);
            return pick;
        }

        public static RelocResult Run(FrameFeatures live, Vector3 pos, Vector3 fwd, IReadOnlyList<RelocCandidate> c,
                                      IList<int> candidates)
        {
            var best = new RelocResult { Candidate = -1 };
            if (live == null) return best;
            foreach (int i in candidates)
            {
                int inl = FeatureMatcher.GeometricInliers(live, c[i].Features, i + 7);
                if (inl < MinInliers) continue;
                float lookDist = (pos + LookDepth * fwd.normalized - (c[i].Position + LookDepth * c[i].Forward.normalized)).magnitude;
                float angle = Vector3.Angle(fwd, c[i].Forward);
                bool consistent = lookDist <= LookTolerance && angle <= AngleTolerance;
                // prefer a consistent match; among equals, the strongest
                if (best.Candidate < 0 || (consistent && !best.Consistent) || (consistent == best.Consistent && inl > best.Inliers))
                    best = new RelocResult
                    {
                        Matched = true, Consistent = consistent, Candidate = i, Inliers = inl, LookDistance = lookDist,
                        AngleDeg = angle,
                    };
            }
            return best;
        }
    }
}
