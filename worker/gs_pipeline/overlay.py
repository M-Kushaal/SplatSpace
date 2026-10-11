"""Draw the AR feature points and the object-bounds cylinder onto captured photos via the manifest poses.

If image orientation or pose conventions are wrong (e.g. a portrait/landscape or mirror mix-up on a
new phone), the drawn geometry visibly misses the object, so this catches it on CPU, before any GPU run.
"""

from pathlib import Path

import numpy as np

from .cameras import project, viewmat_from_c2w_gl
from .manifest import load_frames, points_path
from .ply_io import read_points_ply
from .bounds import refine_bounds, user_bounds


def _cylinder_rings(center, radius, height, n=48):
    """Bottom + top circles of the object bounds (center = footprint center, y up)."""
    t = np.linspace(0, 2 * np.pi, n, endpoint=False)
    ring = np.stack([radius * np.cos(t), np.zeros(n), radius * np.sin(t)], axis=1)
    base = np.asarray(center, float) + ring
    return [base, base + [0.0, height, 0.0]]


def overlay(manifest_path, out_png, num_frames: int = 6, max_points: int = 4000) -> Path:
    from PIL import Image, ImageDraw

    frames, manifest = load_frames(manifest_path)
    pts = None
    pp = points_path(manifest_path, manifest)
    if pp is not None and pp.exists():
        pts, _ = read_points_ply(pp)
        if len(pts) > max_points:
            pts = pts[np.random.default_rng(0).choice(len(pts), max_points, replace=False)]
    rings = []  # (points, color): user-tapped cylinder in cyan, point-refined cylinder in green
    user = user_bounds(manifest)
    if user is not None:
        rings += [(r, (40, 220, 255)) for r in _cylinder_rings(user.base, user.radius, user.height)]
        all_pts = read_points_ply(pp)[0] if pp is not None and pp.exists() else None
        refined = refine_bounds(manifest, all_pts)
        if refined is not None and refined.source != user.source:
            rings += [(r, (60, 255, 60)) for r in _cylinder_rings(refined.base, refined.radius, refined.height)]

    picks = np.linspace(0, len(frames) - 1, min(num_frames, len(frames))).round().astype(int)
    tiles = []
    for i in picks:
        fr = frames[i]
        im = Image.open(fr.image_path).convert("RGB")
        draw = ImageDraw.Draw(im)
        vm = viewmat_from_c2w_gl(fr.c2w_gl)
        if pts is not None:
            uv, depth = project(pts, vm, fr.K)
            for (u, v), d in zip(uv, depth):
                if d > 0 and 0 <= u < fr.width and 0 <= v < fr.height:
                    draw.ellipse([u - 2, v - 2, u + 2, v + 2], fill=(255, 40, 40))
        for ring, color in rings:
            uv, depth = project(ring, vm, fr.K)
            if (depth > 0).all():
                draw.line([tuple(p) for p in uv] + [tuple(uv[0])], fill=color, width=3)
        draw.text((10, 10), f"frame {i}: {fr.image_path.name}", fill=(255, 255, 0))
        im.thumbnail((640, 640))
        tiles.append(im)

    cols = 3
    w, h = max(t.width for t in tiles), max(t.height for t in tiles)
    sheet = Image.new("RGB", (cols * w, ((len(tiles) + cols - 1) // cols) * h), (30, 30, 30))
    for k, t in enumerate(tiles):
        sheet.paste(t, ((k % cols) * w, (k // cols) * h))
    out_png = Path(out_png)
    sheet.save(out_png)
    return out_png
