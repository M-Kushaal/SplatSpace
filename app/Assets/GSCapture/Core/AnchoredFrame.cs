using UnityEngine;

namespace GSCapture.Core
{
    /// <summary>
    /// A local frame that follows an AR anchor. ARCore keeps correcting its map while the user walks, so a
    /// fixed world position slowly slides off the real object; an anchor moves with those corrections.
    /// Geometry (cylinder, dome) is defined relative to <see cref="Origin"/> (translation only: anchors on
    /// horizontal planes stay gravity-aligned, and map corrections are mostly translation).
    /// <see cref="Display"/> eases toward the anchor so correction steps glide instead of jumping.
    /// </summary>
    public sealed class AnchoredFrame
    {
        /// <summary>Latest anchor position (used for all capture math).</summary>
        public Vector3 Origin { get; private set; }

        /// <summary>Smoothed position for rendering.</summary>
        public Vector3 Display { get; private set; }

        /// <summary>Time constant of the display smoothing, seconds.</summary>
        public float SmoothSeconds { get; }

        public AnchoredFrame(Vector3 origin, float smoothSeconds = 0.15f)
        {
            Origin = Display = origin;
            SmoothSeconds = smoothSeconds;
        }

        /// <summary>Feed the anchor's current world position; returns the new display position.</summary>
        public Vector3 Update(Vector3 anchorPosition, float dt)
        {
            Origin = anchorPosition;
            float a = SmoothSeconds <= 0f ? 1f : 1f - Mathf.Exp(-Mathf.Max(dt, 0f) / SmoothSeconds);
            Display = Vector3.Lerp(Display, Origin, a);
            return Display;
        }

        public Vector3 ToLocal(Vector3 world) => world - Origin;

        public Vector3 ToWorld(Vector3 local) => local + Origin;
    }
}
