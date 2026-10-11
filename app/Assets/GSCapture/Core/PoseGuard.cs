using System.Collections.Generic;
using UnityEngine;

namespace GSCapture.Core
{
    /// <summary>Why the pose guard paused capture.</summary>
    public enum PoseEvent
    {
        None,
        Shake,          // gyro spike: paused before ARCore can lose track; poses are still fine
        TrackingLost,   // ARCore stopped tracking (covered camera, fast motion, blank view)
        Relocalized,    // tracking came back after a loss: ARCore may have snapped its map
        Teleport,       // the AR camera moved faster than a hand can move a phone
        GyroMismatch,   // the AR camera turned much more than the gyro says the phone turned
        MapCorrection,  // a spot anchor jumped: ARCore re-shaped its map
    }

    public struct PoseGuardConfig
    {
        public float ShakeRadPerSec;      // gyro magnitude that pauses shooting immediately
        public float CalmRadPerSec;       // below this the phone counts as steady again
        public float MaxSpeed;            // m/s a hand-held phone doesn't exceed for more than a couple of frames
        public int TeleportFrames;
        public float MaxFrameJump;        // m in a single frame = a jump, whatever the frame time
        public float MismatchDeg;         // AR rotation beyond the gyro's over the window
        public float WindowSeconds;
        public float AnchorJump;          // m a spot anchor may move in one frame
        public float StableSeconds;       // tracking + calm for this long before resuming

        public static PoseGuardConfig Default => new PoseGuardConfig
        {
            ShakeRadPerSec = 3f, CalmRadPerSec = 1f, MaxSpeed = 1.5f, TeleportFrames = 2, MaxFrameJump = 0.15f,
            MismatchDeg = 8f, WindowSeconds = 0.2f, AnchorJump = 0.1f, StableSeconds = 1f,
        };
    }

    /// <summary>
    /// Watches the AR pose every frame and pauses the capture when it can no longer be trusted. An accidental
    /// shake on a room capture made ARCore's map jump: the spots and photos after it no longer lined up with the
    /// ones before. The guard catches that in three independent ways (pose moving impossibly fast, pose turning
    /// without the gyro turning, tracking lost and regained / anchors jumping), starts a new tracking "epoch"
    /// (the PC links photos across epochs by image matching, not AR), and only lets capture resume once
    /// tracking has been steady and - after a real jump - the caller confirmed the position visually
    /// (Core.Quality.RelocCheck). Pure logic, no AR types.
    /// </summary>
    public sealed class PoseGuard
    {
        public PoseGuardConfig Config { get; }
        public bool Paused { get; private set; }
        public bool NeedsVisualCheck { get; private set; }  // a real jump: the caller must confirm the position
        public PoseEvent Reason { get; private set; }
        public int Epoch { get; private set; }
        public float StableSeconds { get; private set; }
        public float PausedSeconds { get; private set; }
        public readonly List<(float time, PoseEvent kind, float size)> Events = new List<(float, PoseEvent, float)>();

        readonly Queue<(float t, Quaternion rot, float gyroAngle)> window = new Queue<(float, Quaternion, float)>();
        float gyroIntegral;
        bool hasLast, lost;
        Vector3 lastPos;
        float lastT;
        int fastFrames;

        public PoseGuard(PoseGuardConfig? config = null)
        {
            Config = config ?? PoseGuardConfig.Default;
        }

        /// <summary>Steady long enough to resume (after a jump, also needs <see cref="ConfirmVisually"/>).</summary>
        public bool ReadyToResume => Paused && StableSeconds >= Config.StableSeconds;

        /// <summary>
        /// Feed one frame. gyro = rotation rate magnitude (rad/s); anchorJump = largest movement of any spot
        /// anchor since the previous frame (m). Returns the event that fired this frame (None if nothing new).
        /// </summary>
        public PoseEvent Update(float t, Vector3 pos, Quaternion rot, float gyro, bool tracking, float anchorJump = 0f)
        {
            float dt = hasLast ? Mathf.Max(t - lastT, 1e-4f) : 0f;
            PoseEvent ev = PoseEvent.None;

            if (!tracking)
            {
                if (!lost)
                {
                    lost = true;
                    ev = Fire(t, PoseEvent.TrackingLost, 0f, jump: false);
                }
                ResetMotion();
                StableSeconds = 0f;
                if (Paused) PausedSeconds += dt;
                lastT = t;
                return ev;
            }
            if (lost)
            {
                lost = false;
                ev = Fire(t, PoseEvent.Relocalized, 0f, jump: true);
                ResetMotion();
            }

            if (hasLast && ev == PoseEvent.None)
            {
                float step = (pos - lastPos).magnitude;
                fastFrames = dt < 0.1f && step / dt > Config.MaxSpeed ? fastFrames + 1 : 0;
                // a single-frame jump only counts between consecutive frames (not after a pause, e.g. the
                // room-check sheet, during which the user may have walked)
                if ((step > Config.MaxFrameJump && dt < 0.1f) || fastFrames > Config.TeleportFrames)
                    ev = Fire(t, PoseEvent.Teleport, step, jump: true);
                else if (anchorJump > Config.AnchorJump)
                    ev = Fire(t, PoseEvent.MapCorrection, anchorJump, jump: true);
            }

            // rotation: AR vs gyro over a short window (|net rotation| can never exceed the integral of |rate|)
            gyroIntegral += gyro * dt;
            window.Enqueue((t, rot, gyroIntegral));
            while (window.Count > 1 && t - window.Peek().t > Config.WindowSeconds) window.Dequeue();
            if (ev == PoseEvent.None && window.Count > 1)
            {
                var old = window.Peek();
                float arDeg = Quaternion.Angle(old.rot, rot);
                float gyroDeg = (gyroIntegral - old.gyroAngle) * Mathf.Rad2Deg;
                if (arDeg - gyroDeg > Config.MismatchDeg) ev = Fire(t, PoseEvent.GyroMismatch, arDeg - gyroDeg, jump: true);
            }
            if (ev == PoseEvent.None && gyro > Config.ShakeRadPerSec && !Paused)
                ev = Fire(t, PoseEvent.Shake, gyro, jump: false);

            if (Paused)
            {
                PausedSeconds += dt;
                StableSeconds = ev == PoseEvent.None && gyro < Config.CalmRadPerSec ? StableSeconds + dt : 0f;
            }
            hasLast = true;
            lastPos = pos;
            lastT = t;
            return ev;
        }

        PoseEvent Fire(float t, PoseEvent kind, float size, bool jump)
        {
            Events.Add((t, kind, size));
            if (jump)
            {
                Epoch++;
                NeedsVisualCheck = true;
            }
            if (!Paused || jump) Reason = kind;
            Paused = true;
            StableSeconds = 0f;
            return kind;
        }

        void ResetMotion()
        {
            window.Clear();
            fastFrames = 0;
            hasLast = false;
        }

        /// <summary>The live view matched earlier photos at a consistent position: safe to continue.</summary>
        public void ConfirmVisually() => NeedsVisualCheck = false;

        /// <summary>Resume shooting (call when <see cref="ReadyToResume"/> and the visual check is done or skipped).</summary>
        public void Resume()
        {
            Paused = false;
            NeedsVisualCheck = false;
            Reason = PoseEvent.None;
            StableSeconds = 0f;
            PausedSeconds = 0f;
        }

        /// <summary>The position couldn't be confirmed: carry on in a fresh epoch (the PC links it by images).</summary>
        public void ContinueAsNewEpoch()
        {
            Epoch++;
            Resume();
        }

        public static string Coaching(PoseEvent e, bool needsVisual)
        {
            if (e == PoseEvent.Shake) return "Whoa - hold the phone steady for a second";
            if (e == PoseEvent.TrackingLost) return "Tracking lost - point at something you already scanned";
            return needsVisual ? "Position jumped - point at an area you already captured and hold still"
                               : "Hold still for a second…";
        }
    }
}
