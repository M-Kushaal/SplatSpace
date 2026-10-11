"""Plot camera frustums + sparse points to a PNG, to eyeball pose sanity on CPU."""

from pathlib import Path

import numpy as np

from .manifest import load_frames, points_path
from .validate import scene_target


def _frustum_lines(c2w_gl, K, w, h, size):
    """World-space line segments of a camera frustum (OpenGL: looks down -z)."""
    corners_px = np.array([[0, 0], [w, 0], [w, h], [0, h]], float)
    dirs = np.stack([(corners_px[:, 0] - K[0, 2]) / K[0, 0],
                     -(corners_px[:, 1] - K[1, 2]) / K[1, 1],  # image y down -> camera y up
                     -np.ones(4)], axis=1) * size
    world = dirs @ c2w_gl[:3, :3].T + c2w_gl[:3, 3]
    o = c2w_gl[:3, 3]
    segs = [(o, c) for c in world] + [(world[i], world[(i + 1) % 4]) for i in range(4)]
    up_tip = (world[0] + world[1]) / 2 + c2w_gl[:3, 1] * size * 0.3  # marks the image top
    segs.append(((world[0] + world[1]) / 2, up_tip))
    return segs


def plot_cameras(manifest_path, out_png, max_points: int = 20000) -> Path:
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    frames, manifest = load_frames(manifest_path)
    c2ws = [f.c2w_gl for f in frames]
    target, _ = scene_target(manifest, manifest_path, c2ws)
    centers = np.array([c[:3, 3] for c in c2ws])
    size = 0.12 * np.median(np.linalg.norm(centers - target, axis=1))

    pts = None
    pp = points_path(manifest_path, manifest)
    if pp is not None and pp.exists():
        from .ply_io import read_points_ply

        xyz, rgb = read_points_ply(pp)
        keep = np.linalg.norm(xyz - target, axis=1) < 2.5 * np.median(np.linalg.norm(centers - target, axis=1))
        xyz, rgb = xyz[keep], (rgb[keep] if rgb is not None else None)
        idx = np.random.default_rng(0).choice(len(xyz), min(max_points, len(xyz)), replace=False)
        pts = (xyz[idx], rgb[idx] / 255.0 if rgb is not None else "gray")

    fig = plt.figure(figsize=(14, 7))
    for k, (elev, azim, title) in enumerate([(25, -60, "perspective"), (89, -90, "top-down")]):
        ax = fig.add_subplot(1, 2, k + 1, projection="3d")
        if pts is not None:
            ax.scatter(*pts[0].T, c=pts[1], s=0.3, alpha=0.6)
        for f in frames:
            for a, b in _frustum_lines(f.c2w_gl, f.K, f.width, f.height, size):
                ax.plot(*np.stack([a, b]).T, color="tab:red", lw=0.6)
        ax.scatter(*target, color="tab:blue", s=60, marker="*")
        span = np.ptp(np.vstack([centers, target[None]]), axis=0).max() / 2 * 1.1
        mid = (centers.max(0) + centers.min(0)) / 2
        ax.set_xlim(mid[0] - span, mid[0] + span)
        ax.set_ylim(mid[1] - span, mid[1] + span)
        ax.set_zlim(mid[2] - span, mid[2] + span)
        ax.view_init(elev=elev, azim=azim)
        ax.set_title(f"{title}: {len(frames)} cameras (red), target (blue star)")
        ax.set_xlabel("x"), ax.set_ylabel("y"), ax.set_zlabel("z")
    fig.tight_layout()
    out_png = Path(out_png)
    fig.savefig(out_png, dpi=110)
    plt.close(fig)
    return out_png
