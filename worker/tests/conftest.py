import json
import os
from pathlib import Path

import numpy as np
import pytest

# Optional real dataset for convention tests: a mip-NeRF 360 scene folder with sparse/0 + images_8.
BONSAI_DIR = os.environ.get("BONSAI_DIR")


def look_at_c2w_gl(eye, target, up=(0.0, 1.0, 0.0)) -> np.ndarray:
    """OpenGL camera-to-world for a camera at `eye` looking at `target`."""
    eye, target, up = map(lambda v: np.asarray(v, float), (eye, target, up))
    back = eye - target
    back /= np.linalg.norm(back)
    right = np.cross(up, back)
    right /= np.linalg.norm(right)
    c2w = np.eye(4)
    c2w[:3, 0], c2w[:3, 1], c2w[:3, 2], c2w[:3, 3] = right, np.cross(back, right), back, eye
    return c2w


def orbit_manifest(n=36, radius=2.0, heights=(0.3, 1.0), target=(0, 0, 0), w=640, h=480) -> dict:
    frames = []
    for k in range(n):
        a = 2 * np.pi * k / n
        y = heights[k % len(heights)]
        eye = np.array(target, float) + [radius * np.cos(a), y, radius * np.sin(a)]
        frames.append({"file_path": f"images/{k:04d}.jpg", "transform_matrix": look_at_c2w_gl(eye, target).tolist()})
    return {"schema_version": "1.0", "source": "synthetic", "camera_model": "PINHOLE",
            "fl_x": 500.0, "fl_y": 500.0, "cx": w / 2, "cy": h / 2, "w": w, "h": h,
            "object_bounds": {"center": list(map(float, target)), "radius": 0.5}, "frames": frames}


@pytest.fixture
def write_manifest(tmp_path):
    def _write(manifest: dict) -> Path:
        p = tmp_path / "manifest.json"
        p.write_text(json.dumps(manifest))
        return p
    return _write
