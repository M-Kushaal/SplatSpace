using System.Collections.Generic;
using UnityEngine;

namespace GSCapture.Core
{
    /// <summary>
    /// The set of target viewpoints around the object: rings at fixed elevations, evenly spaced
    /// in azimuth, all at an ideal distance that frames the object. The user "fills" a slot by
    /// holding the camera near it, aimed at the object.
    /// </summary>
    public sealed class ViewpointDome
    {
        public struct Ring
        {
            public float ElevationDeg;
            public int Count;

            public Ring(float elevationDeg, int count)
            {
                ElevationDeg = elevationDeg;
                Count = count;
            }
        }

        public struct Slot
        {
            public int Index;
            public int Ring;
            public float ElevationDeg;
            public float AzimuthDeg;
            public Vector3 Direction; // unit vector from the object center toward the viewpoint
            public bool Filled;
        }

        public struct Match
        {
            public int SlotIndex;      // closest slot, -1 if none
            public float SlotAngleDeg; // angle between camera direction and that slot
            public float DistanceRatio; // camera distance / ideal distance
            public float AimAngleDeg;   // angle between camera forward and the object center
        }

        public static readonly Ring[] DefaultRings =
        {
            new Ring(15f, 24), new Ring(40f, 24), new Ring(65f, 16), new Ring(85f, 4),
        };

        public Vector3 Center { get; }
        public float ObjectRadius { get; }
        public float ObjectHeight { get; }
        public float IdealDistance { get; }
        public IReadOnlyList<Slot> Slots => slots;
        public int FilledCount { get; private set; }
        public float Coverage => slots.Count == 0 ? 0f : (float)FilledCount / slots.Count;

        readonly List<Slot> slots = new List<Slot>();

        /// <param name="baseCenter">Center of the object's footprint on the supporting plane.</param>
        /// <param name="minFovDeg">The camera's smaller field of view (vertical in landscape).</param>
        public ViewpointDome(Vector3 baseCenter, float objectRadius, float objectHeight, float minFovDeg,
                             Ring[] rings = null, float fillFraction = 0.7f)
        {
            ObjectRadius = objectRadius;
            ObjectHeight = objectHeight;
            Center = baseCenter + Vector3.up * (objectHeight * 0.5f);
            float sphere = Mathf.Sqrt(objectRadius * objectRadius + 0.25f * objectHeight * objectHeight);
            float halfAngle = 0.5f * fillFraction * minFovDeg * Mathf.Deg2Rad;
            IdealDistance = Mathf.Max(0.25f, sphere / Mathf.Sin(halfAngle));

            rings = rings ?? DefaultRings;
            for (int r = 0; r < rings.Length; r++)
            {
                for (int k = 0; k < rings[r].Count; k++)
                {
                    float az = 360f * k / rings[r].Count + (r % 2) * 180f / rings[r].Count; // stagger rings
                    slots.Add(new Slot
                    {
                        Index = slots.Count,
                        Ring = r,
                        ElevationDeg = rings[r].ElevationDeg,
                        AzimuthDeg = az,
                        Direction = DirectionFor(rings[r].ElevationDeg, az),
                    });
                }
            }
        }

        public static Vector3 DirectionFor(float elevationDeg, float azimuthDeg)
        {
            float el = elevationDeg * Mathf.Deg2Rad, az = azimuthDeg * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(el) * Mathf.Sin(az), Mathf.Sin(el), Mathf.Cos(el) * Mathf.Cos(az));
        }

        public Vector3 SlotPosition(int index)
        {
            return Center + slots[index].Direction * IdealDistance;
        }

        public Match Evaluate(Vector3 cameraPosition, Vector3 cameraForward)
        {
            Vector3 offset = cameraPosition - Center;
            float dist = offset.magnitude;
            var m = new Match { SlotIndex = -1, SlotAngleDeg = 180f, DistanceRatio = dist / IdealDistance };
            m.AimAngleDeg = Vector3.Angle(cameraForward, -offset);
            if (dist < 1e-4f) return m;

            Vector3 dir = offset / dist;
            float best = -2f;
            for (int i = 0; i < slots.Count; i++)
            {
                float d = Vector3.Dot(dir, slots[i].Direction);
                if (d > best)
                {
                    best = d;
                    m.SlotIndex = i;
                }
            }
            m.SlotAngleDeg = Mathf.Acos(Mathf.Clamp(best, -1f, 1f)) * Mathf.Rad2Deg;
            return m;
        }

        public void MarkFilled(int index)
        {
            if (index < 0 || index >= slots.Count || slots[index].Filled) return;
            var s = slots[index];
            s.Filled = true;
            slots[index] = s;
            FilledCount++;
        }

        /// <summary>Closest unfilled slot to the camera's current viewing direction, -1 when all are filled.</summary>
        public int SuggestNext(Vector3 cameraPosition)
        {
            Vector3 dir = (cameraPosition - Center).normalized;
            int bestIdx = -1;
            float best = -2f;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Filled) continue;
                float d = Vector3.Dot(dir, slots[i].Direction);
                if (d > best)
                {
                    best = d;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        /// <summary>Where the camera currently is, in the dome's elevation/azimuth terms.</summary>
        public void CameraAngles(Vector3 cameraPosition, out float elevationDeg, out float azimuthDeg)
        {
            Vector3 d = (cameraPosition - Center).normalized;
            elevationDeg = Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;
            azimuthDeg = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }
    }
}
