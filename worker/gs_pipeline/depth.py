"""Monocular depth priors for scene (room) captures.

Blank ceilings and walls give COLMAP almost no points (the first room: 38.8k points, nearly none on the
ceiling), so 3DGS gets no Gaussians there and the room has holes, or it fakes them with needle-shaped
"fur". A monocular depth network still sees those surfaces. Its output is relative (unknown scale and
shift per image), so each map is aligned to the metric sparse points that project into that image.
The aligned maps are used twice:
1. dense initialisation: back-projected, multi-view-consistent points fill the blank surfaces;
2. a depth loss during training (train.py), so those surfaces stay flat instead of turning to fur.

    python -m gs_pipeline depth <bundle>/manifest.json --out <depth_dir>

writes <depth_dir>/<frame>.npy (float16 relative disparity), align.json and points3d_dense.ply.
"""

import json
from pathlib import Path

import numpy as np

from .cameras import project, scale_intrinsics, viewmat_from_c2w_gl
from .manifest import load_frames
from .validate import load_points

MODEL = "depth-anything/Depth-Anything-V2-Large-hf"
DEPTH_SIDE = 518          # long side of the saved depth maps (the network's native resolution)
MIN_INLIERS = 30          # fewer aligned sparse points than this: no prior for the frame
ALIGN_FILE = "align.json"
DENSE_FILE = "points3d_dense.ply"


# ---------------------------------------------------------------- pure helpers (unit-tested)

def depth_size(width: int, height: int, side: int = DEPTH_SIDE) -> tuple[int, int]:
    """(w, h) of a frame's depth map: long side = `side`, same aspect."""
    s = side / max(width, height)
    return max(1, round(width * s)), max(1, round(height * s))


def sparse_samples(points: np.ndarray, viewmat: np.ndarray, K: np.ndarray, w: int, h: int,
                   min_depth: float = 0.05) -> tuple[np.ndarray, np.ndarray]:
    """Integer pixel (col, row) Nx2 and depth N of the points that land inside a w x h image."""
    uv, z = project(points, viewmat, K)
    ok = (z > min_depth) & np.isfinite(uv).all(1) & (uv[:, 0] >= 0) & (uv[:, 0] < w) & (uv[:, 1] >= 0) & (uv[:, 1] < h)
    return np.floor(uv[ok]).astype(int), z[ok]


def fit_disparity(mono: np.ndarray, z: np.ndarray, iters: int = 300, rel_tol: float = 0.08,
                  seed: int = 0) -> tuple[float, float, np.ndarray]:
    """Robust fit of 1/z = a * mono + b (a > 0). Points behind occluders (the sparse cloud has no
    visibility info) are simply outliers. Returns (a, b, inlier mask); a = 0 if no fit was found."""
    target = 1.0 / z
    n = len(mono)
    best = np.zeros(n, bool)
    if n < 2:
        return 0.0, 0.0, best
    rng = np.random.default_rng(seed)
    for _ in range(iters):
        i, j = rng.choice(n, 2, replace=False)
        if abs(mono[i] - mono[j]) < 1e-6:
            continue
        a = (target[i] - target[j]) / (mono[i] - mono[j])
        if a <= 0:
            continue
        b = target[i] - a * mono[i]
        inl = np.abs(a * mono + b - target) < rel_tol * target
        if inl.sum() > best.sum():
            best = inl
    if best.sum() < 2:
        return 0.0, 0.0, best
    A = np.stack([mono[best], np.ones(best.sum())], 1)
    a, b = np.linalg.lstsq(A, target[best], rcond=None)[0]
    if a <= 0:
        return 0.0, 0.0, np.zeros(n, bool)
    inl = np.abs(a * mono + b - target) < rel_tol * target
    return float(a), float(b), inl


def metric_depth(mono: np.ndarray, a: float, b: float, max_depth: float = 30.0) -> np.ndarray:
    """Aligned metric depth (m); 0 where the aligned disparity is non-positive or the depth is absurd."""
    disp = a * mono.astype(np.float64) + b
    with np.errstate(divide="ignore"):
        d = np.where(disp > 1.0 / max_depth, 1.0 / disp, 0.0)
    return d


def backproject(depth: np.ndarray, K: np.ndarray, c2w_gl: np.ndarray, stride: int) -> tuple[np.ndarray, np.ndarray]:
    """World points (Nx3) of every `stride`-th pixel with depth > 0, and their (row, col) pixels."""
    rows, cols = np.mgrid[stride // 2:depth.shape[0]:stride, stride // 2:depth.shape[1]:stride]
    rows, cols = rows.ravel(), cols.ravel()
    z = depth[rows, cols]
    ok = z > 0
    rows, cols, z = rows[ok], cols[ok], z[ok]
    x = (cols + 0.5 - K[0, 2]) / K[0, 0] * z
    y = (rows + 0.5 - K[1, 2]) / K[1, 1] * z
    cam = np.stack([x, y, z], 1)                       # OpenCV camera frame
    c2w_cv = np.linalg.inv(viewmat_from_c2w_gl(c2w_gl))
    return cam @ c2w_cv[:3, :3].T + c2w_cv[:3, 3], np.stack([rows, cols], 1)


def consistent_views(points: np.ndarray, depths: list[np.ndarray], Ks: list[np.ndarray], viewmats: list[np.ndarray],
                     skip: int, rel_tol: float = 0.05) -> np.ndarray:
    """For each point, the number of other views whose depth map agrees with it within rel_tol."""
    count = np.zeros(len(points), int)
    for k, (d, K, vm) in enumerate(zip(depths, Ks, viewmats)):
        if k == skip or d is None:
            continue
        h, w = d.shape
        uv, z = project(points, vm, K)
        ok = (z > 0.05) & np.isfinite(uv).all(1) & (uv[:, 0] >= 0) & (uv[:, 0] < w) & (uv[:, 1] >= 0) & (uv[:, 1] < h)
        idx = np.nonzero(ok)[0]
        dz = d[np.floor(uv[idx, 1]).astype(int), np.floor(uv[idx, 0]).astype(int)]
        agree = (dz > 0) & (np.abs(z[idx] - dz) < rel_tol * dz)
        count[idx[agree]] += 1
    return count


def voxel_merge(xyz: np.ndarray, rgb: np.ndarray, voxel: float) -> tuple[np.ndarray, np.ndarray]:
    """One point per occupied voxel: the mean position and colour of the points inside it."""
    if len(xyz) == 0:
        return xyz, rgb
    keys = np.floor(xyz / voxel).astype(np.int64)
    _, inv, cnt = np.unique(keys, axis=0, return_inverse=True, return_counts=True)
    inv = inv.ravel()
    out_xyz = np.zeros((len(cnt), 3))
    out_rgb = np.zeros((len(cnt), 3))
    np.add.at(out_xyz, inv, xyz)
    np.add.at(out_rgb, inv, rgb.astype(np.float64))
    return (out_xyz / cnt[:, None]).astype(np.float32), np.round(out_rgb / cnt[:, None]).astype(np.uint8)


def dense_points(frames, depths: list[np.ndarray | None], colors: list[np.ndarray], stride: int = 8,
                 rel_tol: float = 0.05, min_agree: int = 2, voxel: float = 0.02) -> tuple[np.ndarray, np.ndarray]:
    """Fused, multi-view-consistent points from aligned depth maps (all at depth-map resolution)."""
    Ks = [None if d is None else scale_intrinsics(f.K, d.shape[1] / f.width, d.shape[0] / f.height)
          for f, d in zip(frames, depths)]
    vms = [viewmat_from_c2w_gl(f.c2w_gl) for f in frames]
    all_xyz, all_rgb = [], []
    for i, (f, d) in enumerate(zip(frames, depths)):
        if d is None:
            continue
        xyz, px = backproject(d, Ks[i], f.c2w_gl, stride)
        keep = consistent_views(xyz, depths, Ks, vms, skip=i, rel_tol=rel_tol) >= min_agree
        all_xyz.append(xyz[keep])
        all_rgb.append(colors[i][px[keep, 0], px[keep, 1]])
    if not all_xyz:
        return np.zeros((0, 3), np.float32), np.zeros((0, 3), np.uint8)
    return voxel_merge(np.concatenate(all_xyz), np.concatenate(all_rgb), voxel)


# ---------------------------------------------------------------- loading (used by train.py)

def frame_key(frame) -> str:
    return Path(frame.image_path).stem


def load_aligned(depth_dir, frames) -> list[np.ndarray | None]:
    """Aligned metric depth per frame at depth-map resolution (None where alignment failed)."""
    depth_dir = Path(depth_dir)
    align = json.loads((depth_dir / ALIGN_FILE).read_text())["frames"]
    out = []
    for f in frames:
        a = align.get(frame_key(f))
        path = depth_dir / f"{frame_key(f)}.npy"
        if not a or a["a"] <= 0 or a["inliers"] < MIN_INLIERS or not path.exists():
            out.append(None)
            continue
        out.append(metric_depth(np.load(path).astype(np.float32), a["a"], a["b"]))
    return out


def has_depth(depth_dir) -> bool:
    return depth_dir is not None and (Path(depth_dir) / ALIGN_FILE).exists()


# ---------------------------------------------------------------- pipeline (GPU for the network)

def predict_mono(frames, model_name: str = MODEL, device: str | None = None) -> list[np.ndarray]:
    """Relative disparity per frame at depth_size(), from Depth-Anything-V2 (bigger = nearer)."""
    import torch
    import torch.nn.functional as F
    from PIL import Image
    from transformers import AutoImageProcessor, AutoModelForDepthEstimation

    device = device or ("cuda" if torch.cuda.is_available() else "cpu")
    processor = AutoImageProcessor.from_pretrained(model_name)
    model = AutoModelForDepthEstimation.from_pretrained(model_name).to(device).eval()
    out = []
    for k, f in enumerate(frames):
        with Image.open(f.image_path) as im:
            inputs = processor(images=im.convert("RGB"), return_tensors="pt").to(device)
        with torch.no_grad():
            pred = model(**inputs).predicted_depth  # (1, h', w') relative disparity
        w, h = depth_size(f.width, f.height)
        pred = F.interpolate(pred[:, None].float(), size=(h, w), mode="bilinear", align_corners=False)[0, 0]
        out.append(pred.cpu().numpy())
        if k % 25 == 0:
            print(f"[depth] {k + 1}/{len(frames)} {f.image_path.name}", flush=True)
    return out


def compute_depth(manifest_path, out_dir=None, model_name: str = MODEL, stride: int = 8) -> dict:
    """Predict, align and fuse. Returns the report (also saved inside align.json)."""
    from PIL import Image

    from .ply_io import write_points_ply

    frames, manifest = load_frames(manifest_path)
    out = Path(out_dir) if out_dir else Path(manifest_path).parent / "depth"
    out.mkdir(parents=True, exist_ok=True)
    points = load_points(manifest_path, manifest)
    if points is None or len(points) < MIN_INLIERS:
        raise ValueError("depth priors need the refined sparse points (points3d.ply); run `refine` first")

    monos = predict_mono(frames, model_name)
    align, depths, colors = {}, [], []
    for f, mono in zip(frames, monos):
        np.save(out / f"{frame_key(f)}.npy", mono.astype(np.float16))
        h, w = mono.shape
        K = scale_intrinsics(f.K, w / f.width, h / f.height)
        px, z = sparse_samples(points, viewmat_from_c2w_gl(f.c2w_gl), K, w, h)
        a, b, inl = fit_disparity(mono[px[:, 1], px[:, 0]].astype(np.float64), z)
        rel = float(np.median(np.abs(1 / (a * mono[px[inl, 1], px[inl, 0]] + b) - z[inl]) / z[inl])) if inl.any() else None
        align[frame_key(f)] = {"a": a, "b": b, "points": int(len(z)), "inliers": int(inl.sum()),
                               "median_rel_err": None if rel is None else round(rel, 4)}
        ok = a > 0 and inl.sum() >= MIN_INLIERS
        depths.append(metric_depth(mono, a, b) if ok else None)
        with Image.open(f.image_path) as im:
            colors.append(np.asarray(im.convert("RGB").resize((w, h), Image.BILINEAR)))

    xyz, rgb = dense_points(frames, depths, colors, stride=stride)
    write_points_ply(out / DENSE_FILE, xyz, rgb)
    aligned = sum(d is not None for d in depths)
    errs = [v["median_rel_err"] for v in align.values() if v["median_rel_err"] is not None]
    report = {"model": model_name, "frames": len(frames), "aligned": aligned,
              "median_rel_err": round(float(np.median(errs)), 4) if errs else None,
              "dense_points": int(len(xyz)), "sparse_points": int(len(points)), "dir": str(out)}
    (out / ALIGN_FILE).write_text(json.dumps({"report": report, "frames": align}, indent=1))
    return report
