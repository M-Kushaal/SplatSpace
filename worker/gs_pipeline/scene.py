"""Scene captures (whole rooms): the user stands at a few spots ("stations") and turns with the phone at
arm's length, so the lens moves on ~0.8 m circles. Object-only assumptions (one look-at target, an
orbit, a crop sphere) don't apply; these helpers replace them.

Depth needs the lens to move: photos taken while only rotating about the lens are a panorama, so COLMAP
can't triangulate and 3DGS turns into a blurry bubble. `parallax_check` rejects that early.
"""

import numpy as np

LOOK_DEPTH_M = 1.5          # typical distance to what a room photo shows (walls 1-4 m away)
MIN_SPREAD_M = 0.15         # all lenses within this of their median = pure rotation
LOW_SPREAD_M = 0.4


def capture_mode(manifest: dict) -> str:
    """'scene' or 'object' (captures from before schema 1.1 are objects)."""
    return "scene" if manifest.get("capture_mode") == "scene" else "object"


def forward_gl(c2w: np.ndarray) -> np.ndarray:
    """Viewing direction of an OpenGL camera-to-world (camera looks down -z)."""
    f = -c2w[:3, 2]
    return f / np.linalg.norm(f)


def select_pairs_scene(names: list[str], c2ws: list[np.ndarray], k_nearest: int = 12, k_cross: int = 12,
                       time_window: int = 4, max_angle_deg: float = 70.0, cross_min_m: float = 0.6,
                       epochs: list[int] | None = None) -> list[tuple[str, str]]:
    """Image pairs worth matching in a room: neighbours in time + cameras that look at the same spot.

    "Same spot" = distance between points LOOK_DEPTH_M in front of each camera, among cameras whose
    viewing directions are within max_angle_deg. That covers both views from one station and views of
    the same wall from different stations (incl. loop closure when the user returns to the start).
    The nearest look-points are almost always from the same station, so k_cross extra pairs are taken
    only among cameras >= cross_min_m away; without them stations would be reconstructed separately.
    """
    n = len(names)
    centers = np.array([c[:3, 3] for c in c2ws])
    fwd = np.array([forward_gl(c) for c in c2ws])
    look = centers + LOOK_DEPTH_M * fwd
    dist = np.linalg.norm(look[:, None] - look[None], axis=-1)
    dist[fwd @ fwd.T < np.cos(np.radians(max_angle_deg))] = np.inf
    if epochs is not None:  # AR poses of different epochs aren't comparable (map jump): pair those by looks instead
        e = np.asarray(epochs)
        dist[e[:, None] != e[None]] = np.inf
    far = np.linalg.norm(centers[:, None] - centers[None], axis=-1) >= cross_min_m
    pairs = set()
    for i in range(n):
        near = [j for j in np.argsort(dist[i])[:k_nearest + 1] if j != i]
        cross = [j for j in np.argsort(np.where(far[i], dist[i], np.inf))[:k_cross]]
        for j in near + cross:
            if np.isfinite(dist[i, j]):
                pairs.add((min(i, int(j)), max(i, int(j))))
        for j in range(i + 1, min(n, i + time_window + 1)):
            pairs.add((i, j))
    return [(names[i], names[j]) for i, j in sorted(pairs)]


def camera_spread(centers: np.ndarray) -> float:
    """How far the lens travelled: 90th percentile distance of camera centres from their median (m)."""
    return float(np.percentile(np.linalg.norm(centers - np.median(centers, axis=0), axis=1), 90))


def parallax_check(centers: np.ndarray) -> tuple[list[str], list[str], float]:
    """(errors, warnings, spread)."""
    spread = camera_spread(centers)
    errors, warnings = [], []
    if spread < MIN_SPREAD_M:
        errors.append(f"cameras barely moved (spread {spread * 100:.0f} cm): turning in place gives no depth. "
                      "Hold the phone at arm's length while turning, and capture from several spots")
    elif spread < LOW_SPREAD_M:
        warnings.append(f"cameras moved only {spread * 100:.0f} cm; capture from more spots for better depth")
    return errors, warnings, spread


def scene_info(manifest: dict) -> dict:
    return (manifest.get("extensions") or {}).get("scene") or {}


def crop_box(points: np.ndarray | None, centers: np.ndarray, lo: float = 2.0, hi: float = 98.0,
             margin: float = 0.5) -> tuple[np.ndarray, np.ndarray]:
    """Axis-aligned box holding the room: robust extent of the sparse points (+ margin) and all cameras."""
    if points is not None and len(points) >= 50:
        a, b = np.percentile(points, lo, axis=0) - margin, np.percentile(points, hi, axis=0) + margin
    else:
        a, b = centers.min(0) - 3.0, centers.max(0) + 3.0
    return np.minimum(a, centers.min(0) - margin), np.maximum(b, centers.max(0) + margin)


def floor_from_points(points: np.ndarray, camera_y: float, bin_m: float = 0.05) -> float | None:
    """The floor is the lowest *dense* horizontal layer of points below the cameras. A low percentile is fooled
    by stray points (reflections, mismatches): on the second room it put the floor 3.2 m below the phone instead
    of the real 1.6 m."""
    y = points[:, 1]
    y = y[y < camera_y - 0.5]
    if len(y) < 50:
        return None
    edges = np.arange(y.min(), y.max() + bin_m, bin_m)
    if len(edges) < 2:
        return float(np.median(y))
    hist, edges = np.histogram(y, bins=edges)
    dense = np.nonzero(hist >= 0.5 * hist.max())[0]
    k = int(dense[0])
    return float(0.5 * (edges[k] + edges[k + 1]))


def floor_height(manifest: dict, points: np.ndarray | None, centers: np.ndarray) -> float:
    """Floor y: the app's AR floor plane if recorded, else the lowest dense layer of points, else 1.4 m below
    the cameras (phone held at chest height)."""
    y = scene_info(manifest).get("floor_y")
    if y is not None:
        return float(y)
    if points is not None and len(points) >= 50:
        f = floor_from_points(points, float(np.median(centers[:, 1])))
        if f is not None:
            return f
    return float(np.median(centers[:, 1]) - 1.4)


def tiny_image(path, w: int = 32, h: int = 18) -> np.ndarray:
    """What a photo looks like, in 576 numbers: box-downscaled grey, zero mean, unit length."""
    from PIL import Image

    with Image.open(path) as im:
        t = np.asarray(im.convert("L").resize((w, h), Image.BOX), dtype=np.float32).ravel()
    t -= t.mean()
    n = np.linalg.norm(t)
    return t / n if n > 1e-6 else t


def lookalike_pairs(names: list[str], tinies: np.ndarray, k: int = 6, skip_time: int = 4) -> list[tuple[str, str]]:
    """Pairs of photos that look alike, regardless of their AR poses. After a shake ARCore can restart its map
    (the second room jumped 16 m mid-capture); AR-chosen pairs then never compare photos from before and
    after the jump, and COLMAP builds the room in separate pieces. Time neighbours are skipped (already paired)."""
    sim = tinies @ tinies.T
    n = len(names)
    idx = np.arange(n)
    sim[np.abs(idx[:, None] - idx[None]) <= skip_time] = -np.inf
    pairs = set()
    for i in range(n):
        for j in np.argsort(-sim[i])[:k]:
            if np.isfinite(sim[i, j]):
                pairs.add((min(i, int(j)), max(i, int(j))))
    return [(names[i], names[j]) for i, j in sorted(pairs)]
