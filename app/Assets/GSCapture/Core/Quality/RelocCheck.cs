using System;
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
        public CamPose Pose;        // full pose + intrinsics at thumbnail scale (for the epipolar check)
    }

    /// <summary>
    /// A camera as the photos record it: OpenGL camera-to-world (as in the manifest, image roll applied) and pinhole
    /// intrinsics in the pixels of the feature thumbnail. Feature rows run bottom-up (the camera buffer's order,
    /// see OrbLite), so y is flipped against the intrinsics' top-down convention.
    /// </summary>
    public struct CamPose
    {
        public Matrix4x4 C2wGL;
        public float Fx, Fy, Cx, Cy;
        public int Height;
        public bool Valid;
    }

    public struct RelocResult
    {
        public bool Matched;       // the live view shows the same thing as an earlier photo
        public bool Consistent;    // ... and AR agrees the phone is looking at the same spot
        public int Candidate, Inliers;
        public float LookDistance, AngleDeg;
        public float Agreement;     // share of matched points that lie where AR predicts (epipolar test)
        public int BestPartial;     // strongest candidate even below MinInliers (what to show the user), -1 = none
        public int BestPartialInliers;
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
        public const int MinInliers = 25, PartialInliers = 10;
        public const float LookDepth = 1.5f, LookTolerance = 0.75f, AngleTolerance = 30f;
        public const float EpipolarPx = 20f, MinAgreement = 0.4f;

        static Matrix4x4 CvC2w(Matrix4x4 c2wGL) => c2wGL * Matrix4x4.Scale(new Vector3(1f, -1f, -1f));

        public const float MinBaseline = 0.15f, MinDepth = 0.2f, MaxDepth = 10f, MaxNearBaseline = 1f, MinParallaxDeg = 2f;
        public const float MaxRotationErrorDeg = 8f;

        /// <summary>
        /// Angle (deg) between the relative rotation the photos imply (essential matrix from the matches) and the one
        /// AR claims. Catches a turned map from any viewpoint - including the case the epipolar test misses: cameras
        /// side by side and AR's heading off, which slides points along their epipolar lines. Returns -1 when the
        /// cameras are too close for the photos to fix a rotation reliably (the rotation-only test covers that).
        /// </summary>
        public static float RotationError(FrameFeatures a, CamPose pa, FrameFeatures b, CamPose pb, IList<(int a, int b)> matches)
        {
            if (matches.Count < 8 || !pa.Valid || !pb.Valid) return -1f;
            Matrix4x4 rel = CvC2w(pb.C2wGL).inverse * CvC2w(pa.C2wGL);
            if (new Vector3(rel.m03, rel.m13, rel.m23).magnitude < MinBaseline) return -1f;
            // linear 8-point on normalised (calibrated) coordinates: x2^T E x1 = 0
            var M = new double[81];
            var row = new double[9];
            foreach (var (ia, ib) in matches)
            {
                double x1 = (a.X[ia] - pa.Cx) / pa.Fx, y1 = ((pa.Height - 1) - a.Y[ia] - pa.Cy) / pa.Fy;
                double x2 = (b.X[ib] - pb.Cx) / pb.Fx, y2 = ((pb.Height - 1) - b.Y[ib] - pb.Cy) / pb.Fy;
                row[0] = x2 * x1; row[1] = x2 * y1; row[2] = x2; row[3] = y2 * x1; row[4] = y2 * y1; row[5] = y2;
                row[6] = x1; row[7] = y1; row[8] = 1;
                for (int r = 0; r < 9; r++)
                    for (int c = 0; c < 9; c++) M[r * 9 + c] += row[r] * row[c];
            }
            var evals = new double[9];
            var evecs = new double[81];
            Jacobi(M, 9, evals, evecs);
            int k = 0;
            for (int i = 1; i < 9; i++) if (evals[i] < evals[k]) k = i;
            var E = new double[9];
            for (int i = 0; i < 9; i++) E[i] = evecs[i * 9 + k];
            // SVD of E via the eigenvectors of E^T E
            var EtE = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    EtE[r * 3 + c] = E[r] * E[c] + E[3 + r] * E[3 + c] + E[6 + r] * E[6 + c];
            var sv = new double[3];
            var V = new double[9];
            Jacobi(EtE, 3, sv, V);
            int[] ord = { 0, 1, 2 };
            System.Array.Sort(ord, (x, y) => sv[y].CompareTo(sv[x]));      // descending
            double[] v1 = Col(V, ord[0]), v2 = Col(V, ord[1]);
            double[] v3 = Cross(v1, v2);
            double[] u1 = Norm(MulVec(E, v1)), u2 = Norm(MulVec(E, v2));
            double[] u3 = Cross(u1, u2);
            double[] U = { u1[0], u2[0], u3[0], u1[1], u2[1], u3[1], u1[2], u2[2], u3[2] };
            double[] Vt = { v1[0], v1[1], v1[2], v2[0], v2[1], v2[2], v3[0], v3[1], v3[2] };
            double[] W = { 0, -1, 0, 1, 0, 0, 0, 0, 1 };
            double[] Wt = { 0, 1, 0, -1, 0, 0, 0, 0, 1 };
            double[] Rar = { rel.m00, rel.m01, rel.m02, rel.m10, rel.m11, rel.m12, rel.m20, rel.m21, rel.m22 };
            double best = 180;
            foreach (var Wm in new[] { W, Wt })
            {
                var R = Mul(Mul(U, Wm), Vt);
                if (Det(R) < 0) for (int i = 0; i < 9; i++) R[i] = -R[i];
                best = Math.Min(best, AngleBetween(R, Rar));
            }
            return (float)best;
        }

        static double[] Col(double[] A, int c) => new[] { A[c], A[3 + c], A[6 + c] };
        static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        static double[] Norm(double[] a)
        {
            double n = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
            return n < 1e-15 ? a : new[] { a[0] / n, a[1] / n, a[2] / n };
        }
        static double[] MulVec(double[] A, double[] v) =>
            new[] { A[0] * v[0] + A[1] * v[1] + A[2] * v[2], A[3] * v[0] + A[4] * v[1] + A[5] * v[2], A[6] * v[0] + A[7] * v[1] + A[8] * v[2] };
        static double Det(double[] A) =>
            A[0] * (A[4] * A[8] - A[5] * A[7]) - A[1] * (A[3] * A[8] - A[5] * A[6]) + A[2] * (A[3] * A[7] - A[4] * A[6]);

        static double AngleBetween(double[] A, double[] B)
        {
            double tr = 0; // trace(A^T B)
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++) tr += A[r * 3 + c] * B[r * 3 + c];
            return Math.Acos(Math.Max(-1, Math.Min(1, (tr - 1) / 2))) * 180 / Math.PI;
        }

        /// <summary>Eigen-decomposition of a symmetric n x n matrix (cyclic Jacobi). Columns of vecs = eigenvectors.</summary>
        static void Jacobi(double[] Ain, int n, double[] vals, double[] vecs)
        {
            var A = (double[])Ain.Clone();
            for (int i = 0; i < n * n; i++) vecs[i] = 0;
            for (int i = 0; i < n; i++) vecs[i * n + i] = 1;
            for (int sweep = 0; sweep < 60; sweep++)
            {
                double off = 0;
                for (int p = 0; p < n; p++)
                    for (int q = p + 1; q < n; q++) off += A[p * n + q] * A[p * n + q];
                if (off < 1e-30) break;
                for (int p = 0; p < n; p++)
                    for (int q = p + 1; q < n; q++)
                    {
                        double apq = A[p * n + q];
                        if (Math.Abs(apq) < 1e-300) continue;
                        double theta = (A[q * n + q] - A[p * n + p]) / (2 * apq);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), sn = t * c;
                        for (int k = 0; k < n; k++)
                        {
                            double akp = A[k * n + p], akq = A[k * n + q];
                            A[k * n + p] = c * akp - sn * akq;
                            A[k * n + q] = sn * akp + c * akq;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double apk = A[p * n + k], aqk = A[q * n + k];
                            A[p * n + k] = c * apk - sn * aqk;
                            A[q * n + k] = sn * apk + c * aqk;
                        }
                        for (int k = 0; k < n; k++)
                        {
                            double vkp = vecs[k * n + p], vkq = vecs[k * n + q];
                            vecs[k * n + p] = c * vkp - sn * vkq;
                            vecs[k * n + q] = sn * vkp + c * vkq;
                        }
                    }
            }
            for (int i = 0; i < n; i++) vals[i] = A[i * n + i];
        }

        /// <summary>
        /// Share of the image matches that agree with where AR says the two cameras are - from any viewpoint, not just
        /// the one the earlier photo was taken from:
        ///  - cameras AR places &gt; 15 cm apart: the matches must lie on AR's epipolar lines AND triangulate to room-scale
        ///    distances (0.2-10 m) in front of both cameras. The depth test is what catches a pure shift: on the second
        ///    room the 16 m jump left the epipolar lines nearly right when standing near an old photo's spot, but puts
        ///    the triangulated points hundreds of metres away;
        ///  - cameras AR places (almost) at the same spot: the matches must follow AR's relative rotation.
        /// Limit: from one photo pair, a pure shift along the line between the two cameras looks like a change of
        /// scale and can't be seen. A restarted AR map is also turned about gravity, which is caught; short losses are
        /// covered by PoseGuard's gyro bridge, and the PC links photos by image matching regardless.
        /// </summary>
        public static float EpipolarAgreement(FrameFeatures a, CamPose pa, FrameFeatures b, CamPose pb,
                                              IList<(int a, int b)> matches, float px = EpipolarPx)
        {
            if (matches.Count == 0 || !pa.Valid || !pb.Valid) return 0f;
            Matrix4x4 rel = CvC2w(pb.C2wGL).inverse * CvC2w(pa.C2wGL);   // camera A -> camera B (OpenCV axes)
            double[] R = { rel.m00, rel.m01, rel.m02, rel.m10, rel.m11, rel.m12, rel.m20, rel.m21, rel.m22 };
            double tx = rel.m03, ty = rel.m13, tz = rel.m23;
            double baseline = Math.Sqrt(tx * tx + ty * ty + tz * tz);
            double thr2 = px * (double)px;
            int ok = 0;
            if (baseline < MinBaseline)
            {
                // same spot: pixel motion is (almost) pure rotation, x2 ~ Kb R Ka^-1 x1
                foreach (var (ia, ib) in matches)
                {
                    double xa = (a.X[ia] - pa.Cx) / pa.Fx, ya = ((pa.Height - 1) - a.Y[ia] - pa.Cy) / pa.Fy;
                    double X = R[0] * xa + R[1] * ya + R[2], Y = R[3] * xa + R[4] * ya + R[5], Z = R[6] * xa + R[7] * ya + R[8];
                    if (Z <= 1e-6) continue;
                    double u = pb.Fx * X / Z + pb.Cx, v = pb.Fy * Y / Z + pb.Cy;
                    double du = u - b.X[ib], dv = v - ((pb.Height - 1) - b.Y[ib]);
                    if (du * du + dv * dv < thr2) ok++;
                }
                return (float)ok / matches.Count;
            }
            double[] Tx = { 0, -tz, ty, tz, 0, -tx, -ty, tx, 0 };
            var E = Mul(Tx, R);
            double[] KaInv = { 1 / pa.Fx, 0, -pa.Cx / pa.Fx, 0, 1 / pa.Fy, -pa.Cy / pa.Fy, 0, 0, 1 };
            double[] KbInvT = { 1 / pb.Fx, 0, 0, 0, 1 / pb.Fy, 0, -pb.Cx / pb.Fx, -pb.Cy / pb.Fy, 1 };
            var Fm = Mul(Mul(KbInvT, E), KaInv);
            // camera B's centre in camera A's frame: c = -R^T t
            double cx = -(R[0] * tx + R[3] * ty + R[6] * tz), cy = -(R[1] * tx + R[4] * ty + R[7] * tz),
                   cz = -(R[2] * tx + R[5] * ty + R[8] * tz);
            foreach (var (ia, ib) in matches)
            {
                double x1 = a.X[ia], y1 = (pa.Height - 1) - a.Y[ia];   // bottom-up rows -> top-down pixels
                double x2 = b.X[ib], y2 = (pb.Height - 1) - b.Y[ib];
                double f0 = Fm[0] * x1 + Fm[1] * y1 + Fm[2], f1 = Fm[3] * x1 + Fm[4] * y1 + Fm[5], f2 = Fm[6] * x1 + Fm[7] * y1 + Fm[8];
                double g0 = Fm[0] * x2 + Fm[3] * y2 + Fm[6], g1 = Fm[1] * x2 + Fm[4] * y2 + Fm[7];
                double e = x2 * f0 + y2 * f1 + f2;
                double den = f0 * f0 + f1 * f1 + g0 * g0 + g1 * g1;
                if (den <= 1e-18 || e * e / den >= thr2) continue;
                // triangulate (midpoint of the two rays, in camera A's frame)
                double r1x = (x1 - pa.Cx) / pa.Fx, r1y = (y1 - pa.Cy) / pa.Fy, r1z = 1;
                double bx = (x2 - pb.Cx) / pb.Fx, by = (y2 - pb.Cy) / pb.Fy;  // ray in B, rotated into A: R^T rb
                double r2x = R[0] * bx + R[3] * by + R[6], r2y = R[1] * bx + R[4] * by + R[7], r2z = R[2] * bx + R[5] * by + R[8];
                double a11 = r1x * r1x + r1y * r1y + r1z * r1z, a12 = -(r1x * r2x + r1y * r2y + r1z * r2z);
                double a22 = r2x * r2x + r2y * r2y + r2z * r2z;
                double b1 = r1x * cx + r1y * cy + r1z * cz, b2 = -(r2x * cx + r2y * cy + r2z * cz);
                // nearby cameras + (almost) parallel rays: the point carries no depth information - don't hold it
                // against the match. A jump puts the cameras metres apart, where room-scale depth is required.
                double cosPar = -a12 / Math.Sqrt(a11 * a22);
                if (baseline <= MaxNearBaseline && cosPar > Math.Cos(MinParallaxDeg * Math.PI / 180))
                {
                    ok++;
                    continue;
                }
                double det = a11 * a22 - a12 * a12;
                if (Math.Abs(det) < 1e-12) continue;
                double d1 = (b1 * a22 - a12 * b2) / det, d2 = (a11 * b2 - a12 * b1) / det;
                if (d1 >= MinDepth && d1 <= MaxDepth && d2 >= MinDepth && d2 <= MaxDepth) ok++;
            }
            return (float)ok / matches.Count;
        }

        static double[] Mul(double[] A, double[] B)
        {
            var C = new double[9];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    C[r * 3 + c] = A[r * 3] * B[c] + A[r * 3 + 1] * B[3 + c] + A[r * 3 + 2] * B[6 + c];
            return C;
        }

        /// <summary>RANSAC-verified matches between two photos (empty if they don't overlap).</summary>
        public static List<(int a, int b)> VerifiedMatches(FrameFeatures a, FrameFeatures b, int seed, out int inliers)
        {
            var list = new List<(int, int)>();
            var m = FeatureMatcher.Match(a, b);
            inliers = 0;
            if (m.Length < FeatureMatcher.MinPutative) return list;
            inliers = FeatureMatcher.RansacFundamental(a, b, m, seed, out bool[] mask);
            for (int k = 0; k < m.Length; k++) if (mask[k]) list.Add(m[k]);
            return list;
        }

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
                                      IList<int> candidates, CamPose livePose = default)
        {
            var best = new RelocResult { Candidate = -1, BestPartial = -1 };
            if (live == null) return best;
            foreach (int i in candidates)
            {
                var matches = VerifiedMatches(live, c[i].Features, i + 7, out int inl);
                if (inl > best.BestPartialInliers)
                {
                    best.BestPartialInliers = inl;
                    best.BestPartial = inl >= PartialInliers ? i : best.BestPartial;
                }
                if (inl < MinInliers) continue;
                float lookDist = (pos + LookDepth * fwd.normalized - (c[i].Position + LookDepth * c[i].Forward.normalized)).magnitude;
                float angle = Vector3.Angle(fwd, c[i].Forward);
                float agreement = -1f;
                bool consistent;
                if (livePose.Valid && c[i].Pose.Valid)
                {
                    // from any viewpoint: do the matched points lie where AR predicts?
                    agreement = EpipolarAgreement(live, livePose, c[i].Features, c[i].Pose, matches);
                    float rotErr = RotationError(live, livePose, c[i].Features, c[i].Pose, matches);
                    consistent = agreement >= MinAgreement && (rotErr < 0 || rotErr <= MaxRotationErrorDeg);
                }
                else consistent = lookDist <= LookTolerance && angle <= AngleTolerance; // no intrinsics: same-viewpoint test
                // prefer a consistent match; among equals, the strongest
                if (best.Candidate < 0 || (consistent && !best.Consistent) || (consistent == best.Consistent && inl > best.Inliers))
                {
                    best.Matched = true;
                    best.Consistent = consistent;
                    best.Candidate = i;
                    best.Inliers = inl;
                    best.LookDistance = lookDist;
                    best.AngleDeg = angle;
                    best.Agreement = agreement;
                }
            }
            return best;
        }
    }
}
