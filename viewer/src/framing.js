// Pure camera-framing math (no three.js / DOM) so it can be unit tested with `node --test`.

/** Value at fraction p (0..1) of an ascending-sorted numeric array (linear interpolation). */
export function percentile(sorted, p) {
  if (sorted.length === 0) return NaN;
  const x = Math.min(Math.max(p, 0), 1) * (sorted.length - 1);
  const i = Math.floor(x);
  const f = x - i;
  return i + 1 < sorted.length ? sorted[i] * (1 - f) + sorted[i + 1] * f : sorted[i];
}

/**
 * Bounds of the bulk of the splats, ignoring stray "floaters": per-axis lo..hi percentiles of the
 * splat centres. `centers` is a flat [x0,y0,z0, x1,y1,z1, ...] array. Large inputs are subsampled.
 */
export function robustBounds(centers, { lo = 0.02, hi = 0.98, maxSamples = 200000 } = {}) {
  const n = Math.floor(centers.length / 3);
  if (n === 0) throw new Error("no splats");
  const stride = Math.max(1, Math.floor(n / maxSamples));
  const m = Math.ceil(n / stride);
  const axes = [new Float64Array(m), new Float64Array(m), new Float64Array(m)];
  let k = 0;
  for (let i = 0; i < n; i += stride, k++) {
    axes[0][k] = centers[3 * i];
    axes[1][k] = centers[3 * i + 1];
    axes[2][k] = centers[3 * i + 2];
  }
  const min = [], max = [];
  for (const a of axes) {
    const s = a.subarray(0, k).sort();
    min.push(percentile(s, lo));
    max.push(percentile(s, hi));
  }
  const center = [0, 1, 2].map((d) => (min[d] + max[d]) / 2);
  const size = [0, 1, 2].map((d) => max[d] - min[d]);
  const radius = 0.5 * Math.hypot(size[0], size[1], size[2]);
  return { min, max, center, size, radius: Math.max(radius, 1e-6) };
}

/** Camera distance at which a sphere of `radius` fits the view (both vertical and horizontal FOV). */
export function fitDistance(radius, vfovDeg, aspect, margin = 1.12) {
  const v = (vfovDeg * Math.PI) / 180;
  const h = 2 * Math.atan(Math.tan(v / 2) * aspect);
  const half = Math.min(v, h) / 2;
  return (radius * margin) / Math.sin(half);
}

/** Camera position looking at `center` from `distance`, azimuth around +Y (0 = +Z), elevation above. */
export function orbitPosition(center, distance, azimuthDeg = 35, elevationDeg = 20) {
  const az = (azimuthDeg * Math.PI) / 180;
  const el = (elevationDeg * Math.PI) / 180;
  return [
    center[0] + distance * Math.cos(el) * Math.sin(az),
    center[1] + distance * Math.sin(el),
    center[2] + distance * Math.cos(el) * Math.cos(az),
  ];
}

/** easeInOutCubic for camera animations. */
export function ease(t) {
  t = Math.min(Math.max(t, 0), 1);
  return t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
}

// ---------------------------------------------------------------- scene (room) walk-through

export const EYE_HEIGHT = 1.2; // metres above the floor (scene splats have the floor at y = 0)
export const WALK_SPEED = 1.2; // metres per second while a walk key is held
export const WALK_LOOK = 0.05; // orbit target this far ahead of the eye: dragging turns the head

/** Is this a scene (room) capture? From the training metrics, or forced with ?mode=scene|object. */
export function isScene(metrics, mode) {
  if (mode === "scene") return true;
  if (mode === "object") return false;
  return metrics?.capture_mode === "scene";
}

/**
 * First-person start view inside a room. Uses the pipeline's suggested view (median capture spot,
 * first photo's direction) when present, else the middle of the room at eye height looking along -z.
 * Returns { eye, target } with target WALK_LOOK ahead, looking level.
 */
export function walkStart(bounds, view = null) {
  // Start where the phone mostly was (pipeline view), at EYE_HEIGHT above the floor: the pipeline puts the
  // floor at y = 0. Without a view, the room's middle with the floor at the robust bottom of the splats,
  // kept under the (robust) ceiling.
  const eye = view?.eye ? [view.eye[0], EYE_HEIGHT, view.eye[2]]
    : [bounds.center[0], Math.max(Math.min(bounds.min[1] + EYE_HEIGHT, bounds.max[1] - 0.1), bounds.min[1] + 0.3), bounds.center[2]];
  let f = view?.forward ? [view.forward[0], 0, view.forward[2]] : [0, 0, -1];
  const n = Math.hypot(f[0], f[2]);
  f = n > 1e-6 ? [f[0] / n, 0, f[2] / n] : [0, 0, -1];
  return { eye, target: [eye[0] + f[0] * WALK_LOOK, eye[1], eye[2] + f[2] * WALK_LOOK] };
}

/**
 * Walk mode turns zoom input into steps: OrbitControls dollies the eye toward its (very close) target;
 * the log of that radius change becomes a forward (+) / backward (-) step in metres.
 */
export function dollyStep(radiusBefore, radiusAfter, metresPerUnit = 4) {
  if (!(radiusBefore > 0 && radiusAfter > 0)) return 0;
  return Math.log(radiusBefore / radiusAfter) * metresPerUnit;
}
