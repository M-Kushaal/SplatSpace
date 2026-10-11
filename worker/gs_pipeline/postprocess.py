"""CPU post-processing of a trained splat for viewing: upright + centered, cropped to the object, faint
Gaussians dropped. Runs on the exported PLY, so no GPU re-run is needed.

Rotating a splat must also rotate its view-dependent color (SH bands 1-3); otherwise colors
"swim" when orbiting. Each band is rotated by a matrix solved numerically from the SH basis.
"""

from pathlib import Path

import numpy as np

from .manifest import load_frames
from .ply_io import read_gaussians_ply, write_gaussians_ply
from .validate import object_bounds, scene_target

# Real SH basis constants, same convention as 3DGS / gsplat.
C1 = 0.4886025119029199
C2 = [1.0925484305920792, -1.0925484305920792, 0.31539156525252005, -1.0925484305920792, 0.5462742152960396]
C3 = [-0.5900435899266435, 2.890611442640554, -0.4570457994644658, 0.3731763325901154,
      -0.4570457994644658, 1.445305721320277, -0.5900435899266435]
BANDS = [(0, 3), (3, 8), (8, 15)]  # index ranges of SH bands 1..3 inside shN


def sh_basis(d: np.ndarray) -> np.ndarray:
    """SH basis values (without the DC term) for unit directions d (M,3) -> (M,15)."""
    x, y, z = d[:, 0], d[:, 1], d[:, 2]
    xx, yy, zz = x * x, y * y, z * z
    return np.stack([
        -C1 * y, C1 * z, -C1 * x,
        C2[0] * x * y, C2[1] * y * z, C2[2] * (2 * zz - xx - yy), C2[3] * x * z, C2[4] * (xx - yy),
        C3[0] * y * (3 * xx - yy), C3[1] * x * y * z, C3[2] * y * (4 * zz - xx - yy),
        C3[3] * z * (2 * zz - 3 * xx - 3 * yy), C3[4] * x * (4 * zz - xx - yy), C3[5] * z * (xx - yy),
        C3[6] * x * (xx - 3 * yy),
    ], axis=1)


def sh_rotation_matrices(R: np.ndarray) -> list[np.ndarray]:
    """Per-band T such that coefficients c' = T @ c reproduce color(R d) == old color(d)."""
    d = np.random.default_rng(0).normal(size=(256, 3))
    d /= np.linalg.norm(d, axis=1, keepdims=True)
    A, B = sh_basis(d @ R.T), sh_basis(d)
    return [np.linalg.lstsq(A[:, a:b], B[:, a:b], rcond=None)[0] for a, b in BANDS]


def rotate_shN(shN: np.ndarray, R: np.ndarray) -> np.ndarray:
    out = shN.copy()
    for (a, b), T in zip(BANDS, sh_rotation_matrices(R)):
        if shN.shape[1] >= b:
            out[:, a:b] = np.einsum("ij,njc->nic", T, shN[:, a:b])
    return out


def rotmat_to_quat_wxyz(R: np.ndarray) -> np.ndarray:
    w = np.sqrt(max(0.0, 1 + R[0, 0] + R[1, 1] + R[2, 2])) / 2
    x = np.copysign(np.sqrt(max(0.0, 1 + R[0, 0] - R[1, 1] - R[2, 2])) / 2, R[2, 1] - R[1, 2])
    y = np.copysign(np.sqrt(max(0.0, 1 - R[0, 0] + R[1, 1] - R[2, 2])) / 2, R[0, 2] - R[2, 0])
    z = np.copysign(np.sqrt(max(0.0, 1 - R[0, 0] - R[1, 1] + R[2, 2])) / 2, R[1, 0] - R[0, 1])
    return np.array([w, x, y, z])


def quat_multiply(q1: np.ndarray, q2: np.ndarray) -> np.ndarray:
    """Hamilton product q1 * q2, wxyz; q2 may be (N,4)."""
    w1, x1, y1, z1 = q1
    w2, x2, y2, z2 = q2[..., 0], q2[..., 1], q2[..., 2], q2[..., 3]
    return np.stack([w1 * w2 - x1 * x2 - y1 * y2 - z1 * z2, w1 * x2 + x1 * w2 + y1 * z2 - z1 * y2,
                     w1 * y2 - x1 * z2 + y1 * w2 + z1 * x2, w1 * z2 + x1 * y2 - y1 * x2 + z1 * w2], axis=-1)


def upright_rotation(up: np.ndarray) -> np.ndarray:
    """Smallest rotation taking `up` to +y."""
    a = up / np.linalg.norm(up)
    b = np.array([0.0, 1.0, 0.0])
    v, c = np.cross(a, b), float(a @ b)
    if np.linalg.norm(v) < 1e-9:
        return np.eye(3) if c > 0 else np.diag([1.0, -1.0, -1.0])
    vx = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + vx + vx @ vx * (1 / (1 + c))


def transform_gaussians(g: dict, R: np.ndarray, t: np.ndarray) -> dict:
    """Apply x -> R x + t to a splat (positions, orientations, view-dependent color)."""
    q = quat_multiply(rotmat_to_quat_wxyz(R), g["quats_wxyz"] / np.linalg.norm(g["quats_wxyz"], axis=1,
                                                                               keepdims=True))
    return {**g, "means": g["means"] @ R.T + t, "quats_wxyz": q, "shN": rotate_shN(g["shN"], R)}


def floater_mask(g: dict, k: int = 8, isolation: float = 4.0, faint: float = 0.35, size_aware: bool = False) -> np.ndarray:
    """True for Gaussians to keep. Floaters / background haze are isolated (mean distance to their k
    nearest neighbours > `isolation` x the median spacing) AND faint (opacity < `faint`). On the first
    steel-bottle capture they had 4% opacity at ~16 mm spacing vs. 20-30% at 1-3 mm on real surfaces.

    size_aware (rooms): a Gaussian at least as big as the gap to its neighbours is not isolated - it covers
    that gap. Far, slanted surfaces (the sloped ceiling ends of the third room) are made of few, big,
    semi-transparent Gaussians; without this they were all removed as "floaters" and left see-through holes."""
    from scipy.spatial import cKDTree

    means = g["means"]
    if len(means) <= k:
        return np.ones(len(means), bool)
    d, _ = cKDTree(means).query(means, k=k + 1)
    spacing = d[:, 1:].mean(axis=1)
    opacity = 1 / (1 + np.exp(-g["opacities_logit"]))
    isolated = spacing > isolation * np.median(spacing)
    if size_aware:
        isolated &= spacing > 2.0 * np.exp(g["scales_log"]).max(axis=1)
    return ~(isolated & (opacity < faint))


def needle_mask(g: dict, max_len: float = 0.25, max_ratio: float = 15.0, cameras: np.ndarray | None = None) -> np.ndarray:
    """True for Gaussians to keep. Needles ("fur" on the first room's ceiling) are long (largest axis
    > max_len m) AND thin in both other directions (largest / middle axis > max_ratio). Real surfaces are
    flat discs (two long axes), so big discs on blank walls are kept."""
    s = np.sort(np.exp(g["scales_log"]), axis=1)
    limit = np.full(len(s), max_len)
    if cameras is not None and len(cameras):
        # far from every camera, surfaces are seen at a slant and legitimately need longer Gaussians
        from scipy.spatial import cKDTree

        dist, _ = cKDTree(cameras).query(g["means"])
        limit = max_len * np.maximum(1.0, dist / 1.5)
    return ~((s[:, 2] > limit) & (s[:, 2] > max_ratio * np.maximum(s[:, 1], 1e-9)))


def clean(splat_ply, out_ply, **kw) -> dict:
    """Remove floaters from an existing splat file (no manifest needed)."""
    g = read_gaussians_ply(splat_ply)
    keep = floater_mask(g, **kw)
    write_gaussians_ply(out_ply, **{k: v[keep] for k, v in g.items()})
    return {"in": len(keep), "out": int(keep.sum()), "removed": int((~keep).sum()),
            "out_mb": round(Path(out_ply).stat().st_size / 1e6, 1)}


def postprocess(splat_ply, manifest_path, out_ply, radius: float | None = None, min_opacity: float = 0.02,
                remove_floaters: bool = True) -> dict:
    """Upright + center on the capture target, crop to a sphere, drop faint Gaussians.

    radius defaults to 1.3 x the bounding sphere of the (point-refined) object cylinder if present, else
    0.35 x median camera distance.
    """
    frames, manifest = load_frames(manifest_path)
    c2ws = [f.c2w_gl for f in frames]
    from .scene import capture_mode

    if capture_mode(manifest) == "scene":
        return postprocess_scene(splat_ply, manifest_path, manifest, c2ws, out_ply, min_opacity, remove_floaters)
    target, _ = scene_target(manifest, manifest_path, c2ws)
    up = np.mean([c[:3, 1] for c in c2ws], axis=0)  # camera y (OpenGL) = image up
    if radius is None:
        b = object_bounds(manifest_path, manifest)
        if b is not None:
            radius = 1.3 * b.sphere_radius
        else:
            radius = 0.35 * float(np.median([np.linalg.norm(c[:3, 3] - target) for c in c2ws]))

    g = read_gaussians_ply(splat_ply)
    n_in = len(g["means"])
    keep = (np.linalg.norm(g["means"] - target, axis=1) < radius)
    keep &= 1 / (1 + np.exp(-g["opacities_logit"])) >= min_opacity
    g = {k: v[keep] for k, v in g.items()}
    floaters = 0
    if remove_floaters:
        fk = floater_mask(g)
        floaters = int((~fk).sum())
        g = {k: v[fk] for k, v in g.items()}

    R = upright_rotation(up)
    g = transform_gaussians(g, R, -R @ target)
    write_gaussians_ply(out_ply, **g)
    return {"in": n_in, "out": len(g["means"]), "floaters_removed": floaters, "radius": round(radius, 4),
            "out_mb": round(Path(out_ply).stat().st_size / 1e6, 1)}


def postprocess_scene(splat_ply, manifest_path, manifest: dict, c2ws: list, out_ply, min_opacity: float = 0.02,
                      remove_floaters: bool = True) -> dict:
    """Rooms: keep everything inside the room box, put the floor at y = 0 and the room centre at x = z = 0.

    The AR world is already gravity-aligned (refine aligns COLMAP back to it), so no rotation is needed.
    Also returns a suggested first-person start view for the viewer (in output coordinates).
    The output file keeps the legacy name splat_object.ply so artifacts/app/viewer don't change.
    """
    from .scene import crop_box, floor_height, forward_gl
    from .validate import load_points

    centers = np.array([c[:3, 3] for c in c2ws])
    pts = load_points(manifest_path, manifest)
    lo, hi = crop_box(pts, centers, lo=0.5, hi=99.5, margin=0.75)  # generous: thin far corners are part of the room
    floor = floor_height(manifest, pts, centers)
    g = read_gaussians_ply(splat_ply)
    n_in = len(g["means"])
    keep = np.all((g["means"] >= lo) & (g["means"] <= hi), axis=1)
    keep &= 1 / (1 + np.exp(-g["opacities_logit"])) >= min_opacity
    nk = needle_mask(g, cameras=centers)
    needles = int((keep & ~nk).sum())
    keep &= nk
    g = {k: v[keep] for k, v in g.items()}
    floaters = 0
    if remove_floaters:
        fk = floater_mask(g, size_aware=True)
        floaters = int((~fk).sum())
        g = {k: v[fk] for k, v in g.items()}

    shift = -np.array([0.5 * (lo[0] + hi[0]), floor, 0.5 * (lo[2] + hi[2])])
    g["means"] = g["means"] + shift
    write_gaussians_ply(out_ply, **g)
    eye = np.median(centers, axis=0) + shift
    return {"in": n_in, "out": len(g["means"]), "floaters_removed": floaters, "needles_removed": needles,
            "box_m": [round(float(v), 2) for v in (hi - lo)],
            "view": {"eye": [round(float(v), 3) for v in eye],
                     "forward": [round(float(v), 4) for v in forward_gl(c2ws[0])]},
            "out_mb": round(Path(out_ply).stat().st_size / 1e6, 1)}
