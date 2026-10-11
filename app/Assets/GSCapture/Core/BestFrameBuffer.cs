using System;

namespace GSCapture.Core
{
    /// <summary>
    /// Keeps the sharpest frame seen for one target (a station sector) instead of the first acceptable one, so
    /// the user can keep turning instead of stopping and holding still on every sector. A candidate is
    /// committed when the camera moves on to another target, when the window has been open for
    /// <see cref="MaxWindowSeconds"/>, or on <see cref="Flush"/>. Frames that lose are handed to `discard`
    /// (they own native image buffers). Pure logic: the payload type is up to the caller.
    /// </summary>
    public sealed class BestFrameBuffer<T>
    {
        public float MaxWindowSeconds { get; }
        public bool HasPending { get; private set; }
        public int PendingKey { get; private set; }
        public float PendingScore { get; private set; }

        readonly Action<T> discard;
        T best;
        float openedAt;

        public BestFrameBuffer(Action<T> discard, float maxWindowSeconds = 0.6f)
        {
            this.discard = discard;
            MaxWindowSeconds = maxWindowSeconds;
        }

        /// <summary>Would a frame with this score replace the pending one (or open a new window)? Lets the
        /// caller skip converting images that can't win.</summary>
        public bool Wants(int key, float score) => !HasPending || key != PendingKey || score > PendingScore;

        /// <summary>Offer a frame for target `key`. Returns true and sets `ready` when an earlier target's best
        /// frame must be committed now (the camera moved on). The offered payload is kept or discarded.</summary>
        public bool Offer(int key, float score, float time, T payload, out T ready)
        {
            ready = default;
            bool committed = false;
            if (HasPending && key != PendingKey)
            {
                ready = best;
                committed = true;
                HasPending = false;
            }
            if (!HasPending)
            {
                best = payload;
                PendingKey = key;
                PendingScore = score;
                openedAt = time;
                HasPending = true;
            }
            else if (score > PendingScore)
            {
                discard?.Invoke(best);
                best = payload;
                PendingScore = score;
            }
            else
            {
                discard?.Invoke(payload);
            }
            return committed;
        }

        /// <summary>Commit the pending frame once its window has been open long enough.</summary>
        public bool Tick(float time, out T ready)
        {
            ready = default;
            if (!HasPending || time - openedAt < MaxWindowSeconds) return false;
            return Flush(out ready);
        }

        /// <summary>Commit the pending frame now (leaving a spot, finishing).</summary>
        public bool Flush(out T ready)
        {
            ready = best;
            if (!HasPending) return false;
            HasPending = false;
            best = default;
            return true;
        }

        /// <summary>Drop the pending frame without committing it (capture discarded).</summary>
        public void Clear()
        {
            if (HasPending) discard?.Invoke(best);
            HasPending = false;
            best = default;
        }
    }
}
