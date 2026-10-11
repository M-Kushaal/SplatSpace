using System.Collections.Generic;
using Unity.Collections;

namespace GSCapture.Core
{
    public struct ImageQuality
    {
        public double Sharpness;  // variance of the Laplacian of block means
        public float Brightness;  // mean luminance, 0..255
    }

    /// <summary>
    /// Sharpness = variance of the Laplacian on the luminance (Y) plane, computed on means of
    /// block x block pixel tiles. Averaging first matters: in low light, per-pixel sensor noise
    /// inflates a raw Laplacian and makes blurry frames look sharp (seen on the first phone capture).
    /// Absolute values depend on scene and device, so frames are judged against the recent median
    /// (SharpnessTracker) rather than a fixed threshold.
    /// </summary>
    public static class BlurMeter
    {
        public static ImageQuality Measure(NativeArray<byte> y, int width, int height, int rowStride,
                                           int pixelStride = 1, int block = 4)
        {
            int gw = width / block, gh = height / block;
            var q = new ImageQuality();
            if (gw < 3 || gh < 3) return q;

            var means = new float[gw * gh];
            double total = 0;
            float inv = 1f / (block * block);
            for (int by = 0; by < gh; by++)
            {
                for (int bx = 0; bx < gw; bx++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < block; dy++)
                    {
                        int row = (by * block + dy) * rowStride;
                        for (int dx = 0; dx < block; dx++) sum += y[row + (bx * block + dx) * pixelStride];
                    }
                    means[by * gw + bx] = sum * inv;
                    total += sum;
                }
            }
            q.Brightness = (float)(total / ((double)gw * gh * block * block));

            double s = 0, s2 = 0;
            long n = 0;
            for (int by = 1; by < gh - 1; by++)
            {
                for (int bx = 1; bx < gw - 1; bx++)
                {
                    int i = by * gw + bx;
                    double lap = 4.0 * means[i] - means[i - 1] - means[i + 1] - means[i - gw] - means[i + gw];
                    s += lap;
                    s2 += lap * lap;
                    n++;
                }
            }
            double mean = s / n;
            q.Sharpness = s2 / n - mean * mean;
            return q;
        }
    }

    public sealed class SharpnessTracker
    {
        readonly int capacity;
        readonly Queue<double> recent = new Queue<double>();
        readonly List<double> scratch = new List<double>();

        public SharpnessTracker(int capacity = 30)
        {
            this.capacity = capacity;
        }

        public int Count => recent.Count;

        /// <summary>Forget the baseline (the light changed: old scores aren't comparable any more).</summary>
        public void Clear() => recent.Clear();

        public void Add(double score)
        {
            recent.Enqueue(score);
            while (recent.Count > capacity) recent.Dequeue();
        }

        /// <summary>score / median of recent scores; 1 until enough history exists.</summary>
        public float Ratio(double score)
        {
            if (recent.Count < 5) return 1f;
            scratch.Clear();
            scratch.AddRange(recent);
            scratch.Sort();
            double median = scratch[scratch.Count / 2];
            return median <= 1e-9 ? 1f : (float)(score / median);
        }
    }
}
