namespace GSCapture.Core
{
    /// <summary>
    /// How long the phone has stayed on the same slice (direction + tilt band). Manual capture unlocks only after a
    /// long hold, so it is used for slices the automatic capture can't get rather than to skip it. A brief wobble
    /// onto a neighbouring slice (shorter than <see cref="GraceSeconds"/>) doesn't restart the count.
    /// Pure logic.
    /// </summary>
    public sealed class SliceHold
    {
        public float GraceSeconds = 0.5f;
        public float Held { get; private set; }

        int key = -1, candidate = -1;
        float since, candidateSince;

        public void Update(int sliceKey, float now)
        {
            if (sliceKey == key)
                candidate = -1;
            else if (sliceKey != candidate)
            {
                candidate = sliceKey;
                candidateSince = now;
            }
            else if (key < 0 || now - candidateSince >= GraceSeconds)
            {
                key = sliceKey;
                since = candidateSince;
                candidate = -1;
            }
            if (key < 0)
            {
                key = sliceKey;
                since = now;
            }
            Held = now - since;
        }

        /// <summary>Start a new count (after a manual photo was taken).</summary>
        public void Restart(float now)
        {
            since = now;
            Held = 0f;
        }
    }
}
