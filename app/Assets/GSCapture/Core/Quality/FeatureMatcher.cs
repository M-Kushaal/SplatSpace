using System;

namespace GSCapture.Core.Quality
{
    /// <summary>
    /// Do two photos see the same 3D structure? Mutual-best Hamming matches with a ratio test, verified by
    /// RANSAC on the fundamental matrix (normalised 8-point, Sampson error) -- the same kind of check COLMAP
    /// uses to decide which photos fit together. Only the photos are used, not the AR poses: AR heading drifted
    /// ~35 deg (up to ~2 m) over the first room, which would wrongly reject good pairs between spots.
    /// Thread-safe (no shared state); the room check runs pairs in parallel.
    /// </summary>
    public static class FeatureMatcher
    {
        public const int MaxHamming = 64, MinPutative = 15;
        public const float Ratio = 0.8f, SampsonPx = 2f;
        const int MaxIterations = 256;

        public static int PopCount(ulong v)
        {
            v -= (v >> 1) & 0x5555555555555555UL;
            v = (v & 0x3333333333333333UL) + ((v >> 2) & 0x3333333333333333UL);
            v = (v + (v >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
            return (int)((v * 0x0101010101010101UL) >> 56);
        }

        static int Hamming(ulong[] a, int i, ulong[] b, int j)
        {
            int ai = i * 4, bj = j * 4;
            return PopCount(a[ai] ^ b[bj]) + PopCount(a[ai + 1] ^ b[bj + 1]) + PopCount(a[ai + 2] ^ b[bj + 2]) +
                   PopCount(a[ai + 3] ^ b[bj + 3]);
        }

        /// <summary>Putative matches (index pairs) that are mutual best, pass the ratio test and are close enough.</summary>
        public static (int a, int b)[] Match(FrameFeatures fa, FrameFeatures fb)
        {
            int na = fa.Count, nb = fb.Count;
            if (na == 0 || nb == 0) return Array.Empty<(int, int)>();
            var bestA = new int[na];
            var d1A = new int[na];
            var d2A = new int[na];
            var bestB = new int[nb];
            var d1B = new int[nb];
            for (int j = 0; j < nb; j++) d1B[j] = int.MaxValue;
            for (int i = 0; i < na; i++)
            {
                int b1 = int.MaxValue, b2 = int.MaxValue, bj = -1;
                for (int j = 0; j < nb; j++)
                {
                    int d = Hamming(fa.Desc, i, fb.Desc, j);
                    if (d < b1) { b2 = b1; b1 = d; bj = j; }
                    else if (d < b2) b2 = d;
                    if (d < d1B[j]) { d1B[j] = d; bestB[j] = i; }
                }
                bestA[i] = bj;
                d1A[i] = b1;
                d2A[i] = b2;
            }
            var list = new System.Collections.Generic.List<(int, int)>();
            for (int i = 0; i < na; i++)
            {
                int j = bestA[i];
                if (j < 0 || bestB[j] != i || d1A[i] > MaxHamming) continue;
                if (d2A[i] != int.MaxValue && d1A[i] >= Ratio * d2A[i]) continue;
                list.Add((i, j));
            }
            return list.ToArray();
        }

        /// <summary>Number of matches consistent with one epipolar geometry (0 if too few to tell).</summary>
        public static int GeometricInliers(FrameFeatures fa, FrameFeatures fb, int seed = 1)
        {
            var m = Match(fa, fb);
            return m.Length < MinPutative ? 0 : RansacFundamental(fa, fb, m, seed);
        }

        public static int RansacFundamental(FrameFeatures fa, FrameFeatures fb, (int a, int b)[] m, int seed = 1)
        {
            int n = m.Length;
            if (n < 8) return 0;
            // normalise: centre the image, scale so the long side spans [-1, 1]
            float s = 2f / Math.Max(fa.Width, fa.Height);
            var p1 = new double[n * 2];
            var p2 = new double[n * 2];
            for (int k = 0; k < n; k++)
            {
                p1[2 * k] = (fa.X[m[k].a] - fa.Width * 0.5f) * s;
                p1[2 * k + 1] = (fa.Y[m[k].a] - fa.Height * 0.5f) * s;
                p2[2 * k] = (fb.X[m[k].b] - fb.Width * 0.5f) * s;
                p2[2 * k + 1] = (fb.Y[m[k].b] - fb.Height * 0.5f) * s;
            }
            double thr2 = (SampsonPx * s) * (SampsonPx * s);
            var rng = new Random(seed);
            var sample = new int[8];
            var F = new double[9];
            int best = 0, iterations = MaxIterations;
            for (int it = 0; it < iterations; it++)
            {
                for (int k = 0; k < 8; k++)
                {
                    int r;
                    bool dup;
                    do
                    {
                        r = rng.Next(n);
                        dup = false;
                        for (int q = 0; q < k; q++) if (sample[q] == r) { dup = true; break; }
                    } while (dup);
                    sample[k] = r;
                }
                if (!EightPoint(p1, p2, sample, F)) continue;
                int inl = 0;
                for (int k = 0; k < n; k++)
                    if (Sampson(F, p1[2 * k], p1[2 * k + 1], p2[2 * k], p2[2 * k + 1]) < thr2) inl++;
                if (inl > best)
                {
                    best = inl;
                    // adaptive stop: enough iterations to have drawn one all-inlier sample with 99% confidence
                    double w = (double)inl / n, w8 = Math.Pow(w, 8);
                    if (w8 > 1e-9)
                        iterations = Math.Min(MaxIterations, (int)Math.Ceiling(Math.Log(0.01) / Math.Log(1 - Math.Min(w8, 0.999999))));
                }
            }
            return best;
        }

        static double Sampson(double[] F, double x1, double y1, double x2, double y2)
        {
            double fx0 = F[0] * x1 + F[1] * y1 + F[2], fx1 = F[3] * x1 + F[4] * y1 + F[5], fx2 = F[6] * x1 + F[7] * y1 + F[8];
            double ft0 = F[0] * x2 + F[3] * y2 + F[6], ft1 = F[1] * x2 + F[4] * y2 + F[7];
            double e = x2 * fx0 + y2 * fx1 + fx2;
            double den = fx0 * fx0 + fx1 * fx1 + ft0 * ft0 + ft1 * ft1;
            return den < 1e-18 ? double.MaxValue : e * e / den;
        }

        /// <summary>F (row-major) from 8 correspondences: the eigenvector of AᵀA with the smallest eigenvalue,
        /// found by inverse iteration on a lightly regularised matrix (a 9x9 Cholesky, no SVD needed).</summary>
        static bool EightPoint(double[] p1, double[] p2, int[] idx, double[] F)
        {
            var M = new double[81];
            var row = new double[9];
            foreach (int k in idx)
            {
                double x1 = p1[2 * k], y1 = p1[2 * k + 1], x2 = p2[2 * k], y2 = p2[2 * k + 1];
                row[0] = x2 * x1; row[1] = x2 * y1; row[2] = x2;
                row[3] = y2 * x1; row[4] = y2 * y1; row[5] = y2;
                row[6] = x1; row[7] = y1; row[8] = 1;
                for (int r = 0; r < 9; r++)
                    for (int c = 0; c < 9; c++) M[r * 9 + c] += row[r] * row[c];
            }
            double tr = 0;
            for (int i = 0; i < 9; i++) tr += M[i * 9 + i];
            for (int i = 0; i < 9; i++) M[i * 9 + i] += 1e-10 * tr + 1e-15;
            if (!Cholesky(M)) return false;
            var x = new double[9];
            for (int i = 0; i < 9; i++) x[i] = 1.0 / 3.0 + 0.01 * i;
            for (int iter = 0; iter < 6; iter++)
            {
                CholeskySolve(M, x);
                double norm = 0;
                for (int i = 0; i < 9; i++) norm += x[i] * x[i];
                norm = Math.Sqrt(norm);
                if (norm < 1e-300 || double.IsNaN(norm)) return false;
                for (int i = 0; i < 9; i++) x[i] /= norm;
            }
            Array.Copy(x, F, 9);
            return true;
        }

        /// <summary>In-place lower Cholesky factor of a symmetric positive-definite 9x9 matrix.</summary>
        static bool Cholesky(double[] a)
        {
            for (int j = 0; j < 9; j++)
            {
                double d = a[j * 9 + j];
                for (int k = 0; k < j; k++) d -= a[j * 9 + k] * a[j * 9 + k];
                if (d <= 0) return false;
                d = Math.Sqrt(d);
                a[j * 9 + j] = d;
                for (int i = j + 1; i < 9; i++)
                {
                    double v = a[i * 9 + j];
                    for (int k = 0; k < j; k++) v -= a[i * 9 + k] * a[j * 9 + k];
                    a[i * 9 + j] = v / d;
                }
            }
            return true;
        }

        static void CholeskySolve(double[] L, double[] b)
        {
            for (int i = 0; i < 9; i++)
            {
                double v = b[i];
                for (int k = 0; k < i; k++) v -= L[i * 9 + k] * b[k];
                b[i] = v / L[i * 9 + i];
            }
            for (int i = 8; i >= 0; i--)
            {
                double v = b[i];
                for (int k = i + 1; k < 9; k++) v -= L[k * 9 + i] * b[k];
                b[i] = v / L[i * 9 + i];
            }
        }
    }
}
