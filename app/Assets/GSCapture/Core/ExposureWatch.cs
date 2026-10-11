using UnityEngine;

namespace GSCapture.Core
{
    /// <summary>
    /// Notices when the phone is re-adjusting its exposure (turning toward or away from a window): brightness swings,
    /// frames come out too bright/dark or soft for a moment, and the blur check compares them with a baseline made
    /// under different light - so it kept rejecting perfectly good frames near windows. While adjusting, the caller
    /// waits ("Adjusting to the light…"); once brightness has settled it starts a fresh blur baseline
    /// (<see cref="Update"/> returns true once). Pure logic.
    /// </summary>
    public sealed class ExposureWatch
    {
        public float ChangeFraction = 0.2f;   // brightness this far from the settled level = the light changed
        public float StableFraction = 0.05f;  // consecutive samples this close = settling
        public float SettleSeconds = 0.3f;

        public bool Adjusting { get; private set; }
        float settled = -1f, last = -1f, stableSince;

        /// <summary>Feed a brightness sample (0..255). Returns true exactly when the light has just settled after a
        /// change: the caller should reset its sharpness baseline.</summary>
        public bool Update(float time, float brightness)
        {
            if (settled < 0f)
            {
                settled = last = brightness;
                return false;
            }
            float scale = Mathf.Max(settled, 20f);
            bool stableStep = Mathf.Abs(brightness - last) <= StableFraction * Mathf.Max(last, 20f);
            last = brightness;
            if (!Adjusting)
            {
                if (Mathf.Abs(brightness - settled) > ChangeFraction * scale)
                {
                    Adjusting = true;
                    stableSince = time;
                }
                else settled = Mathf.Lerp(settled, brightness, 0.1f); // slow drift is not a change
                return false;
            }
            if (!stableStep) stableSince = time;
            if (time - stableSince < SettleSeconds) return false;
            Adjusting = false;
            settled = brightness;
            return true;
        }
    }
}
