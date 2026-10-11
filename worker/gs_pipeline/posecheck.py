"""Measure how consistent a bundle's poses + intrinsics are with its own photos (CPU, needs OpenCV).

SIFT matches between photo pairs that look at the object from similar directions are filtered with a
pose-independent RANSAC, then scored by their Sampson (epipolar) distance under the manifest's poses.
Sharp splats need roughly <= 1-2 px; the first ARCore-only captures measured 10-37 px.
"""

import numpy as np

from .cameras import c2w_gl_to_cv
from .manifest import load_frames


def _sampson_px(Ki, Kj, ci, cj, p1, p2):
    A, B = c2w_gl_to_cv(ci), c2w_gl_to_cv(cj)
    rel = np.linalg.inv(B) @ A
    R, t = rel[:3, :3], rel[:3, 3]
    tx = np.array([[0, -t[2], t[1]], [t[2], 0, -t[0]], [-t[1], t[0], 0]])
    F = np.linalg.inv(Kj).T @ tx @ R @ np.linalg.inv(Ki)
    x1, x2 = np.c_[p1, np.ones(len(p1))], np.c_[p2, np.ones(len(p2))]
    Fx1, Ftx2 = x1 @ F.T, x2 @ F
    return np.sqrt(np.einsum("ij,ij->i", x2, Fx1) ** 2 /
                   (Fx1[:, 0] ** 2 + Fx1[:, 1] ** 2 + Ftx2[:, 0] ** 2 + Ftx2[:, 1] ** 2))


def posecheck(manifest_path, max_angle_deg: float = 25.0, max_pairs: int = 250, min_inliers: int = 30) -> dict:
    import cv2

    frames, _ = load_frames(manifest_path)
    sift = cv2.SIFT_create(4000)
    feats = [sift.detectAndCompute(cv2.imread(str(f.image_path), cv2.IMREAD_GRAYSCALE), None) for f in frames]
    bf = cv2.BFMatcher()
    dirs = np.array([f.c2w_gl[:3, 2] for f in frames])
    n = len(frames)
    pairs = [(i, j) for i in range(n) for j in range(i + 1, n)
             if dirs[i] @ dirs[j] > np.cos(np.radians(max_angle_deg))]
    if len(pairs) > max_pairs:
        pairs = [pairs[k] for k in np.linspace(0, len(pairs) - 1, max_pairs).astype(int)]

    errors, gaps = [], []
    for i, j in pairs:
        (k1, d1), (k2, d2) = feats[i], feats[j]
        if d1 is None or d2 is None:
            continue
        ms = [m for m, q in bf.knnMatch(d1, d2, k=2) if m.distance < 0.75 * q.distance]
        if len(ms) < min_inliers:
            continue
        p1 = np.float64([k1[m.queryIdx].pt for m in ms])
        p2 = np.float64([k2[m.trainIdx].pt for m in ms])
        _, mask = cv2.findFundamentalMat(p1, p2, cv2.FM_RANSAC, 2.0, 0.999)
        if mask is None or mask.sum() < min_inliers:
            continue
        keep = mask.ravel() == 1
        e = _sampson_px(frames[i].K, frames[j].K, frames[i].c2w_gl, frames[j].c2w_gl, p1[keep], p2[keep])
        errors.append(float(np.median(e)))
        gaps.append(j - i)

    errors, gaps = np.array(errors), np.array(gaps)
    out = {"pairs_considered": len(pairs), "pairs_measured": int(len(errors))}
    if len(errors):
        out.update(median_px=round(float(np.median(errors)), 2), p90_px=round(float(np.percentile(errors, 90)), 2),
                   max_px=round(float(errors.max()), 2))
        for lo, hi in ((1, 5), (5, 20), (20, 10 ** 6)):
            sel = (gaps >= lo) & (gaps < hi)
            if sel.any():
                out[f"median_px_gap_{lo}_{hi if hi < 10 ** 6 else 'plus'}"] = round(float(np.median(errors[sel])), 2)
    return out
