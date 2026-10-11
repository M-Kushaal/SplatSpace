namespace GSCapture.Core
{
    public enum GateReason
    {
        Ok,
        Busy,
        NotTracking,
        TooDark,
        NotAimed,
        TooClose,
        TooFar,
        BetweenSlots,
        AlreadyCaptured,
        MovingTooFast,
        Settling,
        Blurry,
        // scene (room) captures
        TooCloseToBody,     // lens near the body: turning only the wrist gives no parallax
        LeftStation,        // walked away from the current spot
        TooCloseToSurface,  // a wall/object fills the view too close to focus
        AdjustingLight,     // exposure is re-adjusting (e.g. turned toward a window): wait a moment
    }

    public struct GateInput
    {
        public bool Busy;               // previous frame still being saved / too soon after it
        public bool Tracking;           // AR session fully tracking
        public float AimAngleDeg;       // camera forward vs. direction to object center
        public float DistanceRatio;     // camera distance / ideal distance
        public float SlotAngleDeg;      // angular distance to the nearest slot
        public bool SlotFilled;
        public float AngularSpeed;      // rad/s (gyro)
        public float LinearSpeed;       // m/s (AR pose)
        public float SteadySeconds;     // how long both speeds have been under their limits
        public float SharpnessRatio;    // blur score / recent median, 1 = typical
        public float Brightness;        // mean luminance 0..255 (checked with the image, like blur)
    }

    public struct GateConfig
    {
        public float MaxAimAngleDeg;
        public float MinDistanceRatio;
        public float MaxDistanceRatio;
        public float MaxSlotAngleDeg;
        public float MaxAngularSpeed;
        public float MaxLinearSpeed;
        public float MinSteadySeconds;
        public float MinSharpnessRatio;
        public float MinBrightness;

        // Motion limits come from the first real capture (OnePlus, indoor light): at 0.2 rad/s a single
        // frame of pose/image latency is ~10 px of error at f~1480 px, and exposure blur is similar.
        // 0.08 rad/s keeps both to a few px.
        public static GateConfig Default => new GateConfig
        {
            MaxAimAngleDeg = 20f,
            MinDistanceRatio = 0.6f,
            MaxDistanceRatio = 1.6f,
            MaxSlotAngleDeg = 8f,
            MaxAngularSpeed = 0.08f,
            MaxLinearSpeed = 0.06f,
            MinSteadySeconds = 0.3f,
            MinSharpnessRatio = 0.8f,
            MinBrightness = 45f,
        };

        // Rooms: COLMAP re-estimates every pose from the photos, so AR pose lag no longer matters and only
        // real blur does (BlurMeter + BestFrameBuffer keep the sharpest frame per sector). On the first room
        // the 0.08 rad/s limit blocked 2.5 min of a ~10 min capture while only 31 frames were actually blurry.
        // The motion limits are now just safety caps.
        public static GateConfig Scene
        {
            get
            {
                var c = Default;
                c.MaxAngularSpeed = 0.35f;
                c.MaxLinearSpeed = 0.25f;
                c.MinSteadySeconds = 0.1f;
                return c;
            }
        }
    }

    /// <summary>Decides whether the current frame is worth keeping, and what to tell the user if not.</summary>
    public static class CaptureGate
    {
        /// <summary>Everything except blur (which needs the camera image, so it is checked last).</summary>
        public static GateReason EvaluateGeometry(in GateInput i, in GateConfig c)
        {
            if (i.Busy) return GateReason.Busy;
            if (!i.Tracking) return GateReason.NotTracking;
            if (i.AimAngleDeg > c.MaxAimAngleDeg) return GateReason.NotAimed;
            if (i.DistanceRatio < c.MinDistanceRatio) return GateReason.TooClose;
            if (i.DistanceRatio > c.MaxDistanceRatio) return GateReason.TooFar;
            if (i.SlotAngleDeg > c.MaxSlotAngleDeg) return GateReason.BetweenSlots;
            if (i.SlotFilled) return GateReason.AlreadyCaptured;
            if (i.AngularSpeed > c.MaxAngularSpeed || i.LinearSpeed > c.MaxLinearSpeed) return GateReason.MovingTooFast;
            if (i.SteadySeconds < c.MinSteadySeconds) return GateReason.Settling;
            return GateReason.Ok;
        }

        /// <summary>
        /// Scene captures: the placement verdict comes from StationPlanner instead of the object dome;
        /// tracking, motion and settling are checked exactly like object captures.
        /// </summary>
        public static GateReason EvaluateSceneGeometry(in GateInput i, GateReason placement, in GateConfig c)
        {
            if (i.Busy) return GateReason.Busy;
            if (!i.Tracking) return GateReason.NotTracking;
            if (placement != GateReason.Ok) return placement;
            if (i.AngularSpeed > c.MaxAngularSpeed || i.LinearSpeed > c.MaxLinearSpeed) return GateReason.MovingTooFast;
            if (i.SteadySeconds < c.MinSteadySeconds) return GateReason.Settling;
            return GateReason.Ok;
        }

        /// <summary>Light + blur, once the geometry is Ok (shared by object and scene captures).</summary>
        public static GateReason EvaluateImage(in GateInput i, in GateConfig c)
        {
            if (i.Brightness < c.MinBrightness) return GateReason.TooDark;
            return i.SharpnessRatio < c.MinSharpnessRatio ? GateReason.Blurry : GateReason.Ok;
        }

        public static GateReason Evaluate(in GateInput i, in GateConfig c)
        {
            var g = EvaluateGeometry(i, c);
            if (g != GateReason.Ok) return g;
            if (i.Brightness < c.MinBrightness) return GateReason.TooDark;
            return i.SharpnessRatio < c.MinSharpnessRatio ? GateReason.Blurry : GateReason.Ok;
        }

        public static string Coaching(GateReason r)
        {
            switch (r)
            {
                case GateReason.Ok: return "Captured";
                case GateReason.Busy: return "";
                case GateReason.NotTracking: return "Tracking lost - move back to an area you already scanned";
                case GateReason.NotAimed: return "Point the camera at the object";
                case GateReason.TooClose: return "Move back a little";
                case GateReason.TooFar: return "Move closer";
                case GateReason.BetweenSlots: return "Move to the highlighted marker";
                case GateReason.AlreadyCaptured: return "Already captured here - go to the yellow marker";
                case GateReason.MovingTooFast: return "Slow down - hold steady";
                case GateReason.Settling: return "Hold still...";
                case GateReason.TooDark: return "Too dark - turn on more lights";
                case GateReason.Blurry: return "Too blurry - hold steady (or add light)";
                case GateReason.TooCloseToBody: return "Hold the phone out at arm's length - turn your whole body, not your wrist";
                case GateReason.LeftStation: return "Go back to your spot - or tap Next spot";
                case GateReason.TooCloseToSurface: return "Too close - step back from the wall";
                case GateReason.AdjustingLight: return "Adjusting to the light…";
                default: return r.ToString();
            }
        }
    }
}
