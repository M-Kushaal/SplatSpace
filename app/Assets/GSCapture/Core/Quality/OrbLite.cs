using System;
using System.Collections.Generic;

namespace GSCapture.Core.Quality
{
    /// <summary>Features of one photo: positions in thumbnail pixels + 256-bit binary descriptors (4 ulongs each).</summary>
    public sealed class FrameFeatures
    {
        public float[] X, Y;
        public ulong[] Desc;
        public int Width, Height;            // thumbnail size the positions refer to
        public float Brightness;             // mean luminance 0..255
        public float Texture;                // fraction of 4x4 blocks with real detail (blank wall ~ 0)
        public float[] Tiny;                 // 32x18 normalised thumbnail: what the photo looks like, for pairing
                                             // photos without trusting AR poses (they jump after a shake)
        public int Count => X.Length;
    }

    /// <summary>
    /// Small, dependency-free feature extractor for the on-phone room check: FAST-9 corners (spread over a grid,
    /// two pyramid levels) + BRIEF descriptors made of 5x5 box-mean comparisons (box means instead of single
    /// pixels = built-in smoothing). No rotation invariance: the phone is held upright, so roll stays ~0.
    /// Runs on a ~480 px grey thumbnail: ~10-20 ms per photo on a phone.
    /// </summary>
    public static class OrbLite
    {
        public const int ThumbWidth = 480, DefaultMaxFeatures = 400;
        const int FastThreshold = 12, Margin = 16, PatchRadius = 13, Bits = 256;

        static readonly int[] CircleX = { 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2, -3, -3, -3, -2, -1 };
        static readonly int[] CircleY = { -3, -3, -2, -1, 0, 1, 2, 3, 3, 3, 2, 1, 0, -1, -2, -3 };
        static readonly sbyte[] Pattern = MakePattern(); // Bits pairs of (dx1, dy1, dx2, dy2)

        static sbyte[] MakePattern()
        {
            var rng = new Random(20261010);
            var p = new sbyte[Bits * 4];
            for (int i = 0; i < p.Length; i++)
            {
                // ~Gaussian (sum of uniforms), sigma ~ 6 px, clipped to the patch
                double g = 0;
                for (int k = 0; k < 4; k++) g += rng.NextDouble() - 0.5;
                p[i] = (sbyte)Math.Max(-PatchRadius, Math.Min(PatchRadius, Math.Round(g * 10.4)));
            }
            return p;
        }

        /// <summary>Box-downscale RGBA (4 bytes/pixel, rows of width*4) to grey, long side ~ThumbWidth.</summary>
        public static byte[] GrayThumb(IList<byte> rgba, int width, int height, out int tw, out int th, int target = ThumbWidth)
        {
            int f = Math.Max(1, (int)Math.Round((double)Math.Max(width, height) / target));
            tw = width / f;
            th = height / f;
            var g = new byte[tw * th];
            int n = f * f;
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < f; dy++)
                    {
                        int row = ((y * f + dy) * width + x * f) * 4;
                        for (int dx = 0; dx < f; dx++)
                        {
                            int i = row + dx * 4;
                            sum += (rgba[i] * 77 + rgba[i + 1] * 150 + rgba[i + 2] * 29) >> 8;
                        }
                    }
                    g[y * tw + x] = (byte)(sum / n);
                }
            return g;
        }

        /// <summary>Same as the IList overload, straight from the camera's converted RGBA buffer (no 8 MB copy).</summary>
        public static byte[] GrayThumb(Unity.Collections.NativeArray<byte> rgba, int width, int height, out int tw, out int th,
                                       int target = ThumbWidth)
        {
            int f = Math.Max(1, (int)Math.Round((double)Math.Max(width, height) / target));
            tw = width / f;
            th = height / f;
            var g = new byte[tw * th];
            int n = f * f;
            for (int y = 0; y < th; y++)
                for (int x = 0; x < tw; x++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < f; dy++)
                    {
                        int row = ((y * f + dy) * width + x * f) * 4;
                        for (int dx = 0; dx < f; dx++)
                        {
                            int i = row + dx * 4;
                            sum += (rgba[i] * 77 + rgba[i + 1] * 150 + rgba[i + 2] * 29) >> 8;
                        }
                    }
                    g[y * tw + x] = (byte)(sum / n);
                }
            return g;
        }

        public static FrameFeatures Extract(byte[] gray, int w, int h, int maxFeatures = DefaultMaxFeatures)
        {
            var xs = new List<float>(maxFeatures);
            var ys = new List<float>(maxFeatures);
            var desc = new List<ulong>(maxFeatures * 4);
            int n0 = maxFeatures * 7 / 10;
            ExtractLevel(gray, w, h, 1, n0, xs, ys, desc);
            int hw = w / 2, hh = h / 2;
            var half = new byte[hw * hh];
            for (int y = 0; y < hh; y++)
                for (int x = 0; x < hw; x++)
                {
                    int i = 2 * y * w + 2 * x;
                    half[y * hw + x] = (byte)((gray[i] + gray[i + 1] + gray[i + w] + gray[i + w + 1]) >> 2);
                }
            ExtractLevel(half, hw, hh, 2, maxFeatures - xs.Count, xs, ys, desc);
            Stats(gray, w, h, out float brightness, out float texture);
            return new FrameFeatures
            {
                X = xs.ToArray(), Y = ys.ToArray(), Desc = desc.ToArray(), Width = w, Height = h,
                Brightness = brightness, Texture = texture, Tiny = TinyImage(gray, w, h),
            };
        }

        public const int TinyW = 32, TinyH = 18;

        /// <summary>Box-downscaled 32x18 grey image, zero mean, unit length: dot product = appearance similarity.</summary>
        public static float[] TinyImage(byte[] g, int w, int h)
        {
            var t = new float[TinyW * TinyH];
            for (int ty = 0; ty < TinyH; ty++)
                for (int tx = 0; tx < TinyW; tx++)
                {
                    int x0 = tx * w / TinyW, x1 = Math.Max(x0 + 1, (tx + 1) * w / TinyW);
                    int y0 = ty * h / TinyH, y1 = Math.Max(y0 + 1, (ty + 1) * h / TinyH);
                    float s = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) s += g[y * w + x];
                    t[ty * TinyW + tx] = s / ((x1 - x0) * (y1 - y0));
                }
            float mean = 0;
            foreach (float v in t) mean += v;
            mean /= t.Length;
            float norm = 0;
            for (int i = 0; i < t.Length; i++) { t[i] -= mean; norm += t[i] * t[i]; }
            norm = (float)Math.Sqrt(norm);
            if (norm > 1e-6f) for (int i = 0; i < t.Length; i++) t[i] /= norm;
            return t;
        }

        /// <summary>Mean luminance and the fraction of 4x4 blocks whose Laplacian (of block means) shows detail.</summary>
        public static void Stats(byte[] g, int w, int h, out float brightness, out float texture)
        {
            const int b = 4;
            int gw = w / b, gh = h / b;
            var m = new float[gw * gh];
            double total = 0;
            for (int by = 0; by < gh; by++)
                for (int bx = 0; bx < gw; bx++)
                {
                    int s = 0;
                    for (int dy = 0; dy < b; dy++)
                        for (int dx = 0; dx < b; dx++) s += g[(by * b + dy) * w + bx * b + dx];
                    m[by * gw + bx] = s / 16f;
                    total += s;
                }
            brightness = gw * gh > 0 ? (float)(total / (gw * gh * 16.0)) : 0f;
            int detailed = 0, cnt = 0;
            for (int by = 1; by < gh - 1; by++)
                for (int bx = 1; bx < gw - 1; bx++)
                {
                    int i = by * gw + bx;
                    float lap = 4 * m[i] - m[i - 1] - m[i + 1] - m[i - gw] - m[i + gw];
                    if (Math.Abs(lap) > 6f) detailed++;
                    cnt++;
                }
            texture = cnt > 0 ? (float)detailed / cnt : 0f;
        }

        static void ExtractLevel(byte[] g, int w, int h, int scale, int budget, List<float> xs, List<float> ys, List<ulong> desc)
        {
            if (budget <= 0 || w <= 2 * Margin || h <= 2 * Margin) return;
            var score = new float[w * h];
            var flags = new int[32];
            for (int y = Margin; y < h - Margin; y++)
                for (int x = Margin; x < w - Margin; x++)
                {
                    int c = g[y * w + x], hi = c + FastThreshold, lo = c - FastThreshold;
                    // quick reject: a 9-pixel arc covers at least 2 of the 4 compass points
                    int b = 0, d = 0;
                    for (int k = 0; k < 16; k += 4)
                    {
                        int v = g[(y + CircleY[k]) * w + x + CircleX[k]];
                        if (v > hi) b++;
                        else if (v < lo) d++;
                    }
                    if (b < 2 && d < 2) continue;
                    float sb = 0, sd = 0;
                    for (int k = 0; k < 16; k++)
                    {
                        int v = g[(y + CircleY[k]) * w + x + CircleX[k]];
                        flags[k] = flags[k + 16] = v > hi ? 1 : v < lo ? -1 : 0;
                        if (v > hi) sb += v - hi;
                        else if (v < lo) sd += lo - v;
                    }
                    int run = 0, best = 0, sign = 0;
                    for (int k = 0; k < 32; k++)
                    {
                        if (flags[k] != 0 && flags[k] == sign) run++;
                        else
                        {
                            sign = flags[k];
                            run = flags[k] != 0 ? 1 : 0;
                        }
                        if (run > best) best = run;
                    }
                    if (best >= 9) score[y * w + x] = Math.Max(sb, sd);
                }

            // 3x3 non-max suppression, then the strongest corners per grid cell so features cover the image
            const int gx = 8, gy = 6;
            var cells = new List<(float s, int x, int y)>[gx * gy];
            for (int i = 0; i < cells.Length; i++) cells[i] = new List<(float, int, int)>();
            for (int y = Margin; y < h - Margin; y++)
                for (int x = Margin; x < w - Margin; x++)
                {
                    float s = score[y * w + x];
                    if (s <= 0) continue;
                    bool max = true;
                    for (int dy = -1; dy <= 1 && max; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            if ((dx != 0 || dy != 0) && score[(y + dy) * w + x + dx] > s) { max = false; break; }
                    if (max) cells[Math.Min(gy - 1, y * gy / h) * gx + Math.Min(gx - 1, x * gx / w)].Add((s, x, y));
                }
            int perCell = Math.Max(2, budget / (gx * gy));
            var picked = new List<(float s, int x, int y)>();
            var spare = new List<(float s, int x, int y)>();
            foreach (var cell in cells)
            {
                cell.Sort((a, b) => b.s.CompareTo(a.s));
                for (int i = 0; i < cell.Count; i++) (i < perCell ? picked : spare).Add(cell[i]);
            }
            if (picked.Count < budget)
            {
                spare.Sort((a, b) => b.s.CompareTo(a.s));
                for (int i = 0; i < spare.Count && picked.Count < budget; i++) picked.Add(spare[i]);
            }
            else if (picked.Count > budget)
            {
                picked.Sort((a, b) => b.s.CompareTo(a.s));
                picked.RemoveRange(budget, picked.Count - budget);
            }

            var integral = Integral(g, w, h);
            int iw = w + 1;
            foreach (var (_, x, y) in picked)
            {
                xs.Add(x * scale + (scale - 1) * 0.5f);
                ys.Add(y * scale + (scale - 1) * 0.5f);
                for (int word = 0; word < 4; word++)
                {
                    ulong bits = 0;
                    for (int k = 0; k < 64; k++)
                    {
                        int p = (word * 64 + k) * 4;
                        int a = Box(integral, iw, x + Pattern[p], y + Pattern[p + 1]);
                        int c = Box(integral, iw, x + Pattern[p + 2], y + Pattern[p + 3]);
                        if (a < c) bits |= 1UL << k;
                    }
                    desc.Add(bits);
                }
            }
        }

        static int[] Integral(byte[] g, int w, int h)
        {
            int iw = w + 1;
            var s = new int[iw * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                int row = 0;
                for (int x = 0; x < w; x++)
                {
                    row += g[y * w + x];
                    s[(y + 1) * iw + x + 1] = s[y * iw + x + 1] + row;
                }
            }
            return s;
        }

        /// <summary>Sum of the 5x5 box centred on (x, y).</summary>
        static int Box(int[] s, int iw, int x, int y)
        {
            int x0 = x - 2, y0 = y - 2, x1 = x + 3, y1 = y + 3;
            return s[y1 * iw + x1] - s[y0 * iw + x1] - s[y1 * iw + x0] + s[y0 * iw + x0];
        }
    }
}
