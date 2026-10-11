"""Read COLMAP binary models and convert them into a capture manifest.

Used for public reference datasets (GPU golden test) and later as the output
step of the GLOMAP/COLMAP fallback path for unposed footage.
"""

import json
import struct
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from . import SCHEMA_VERSION
from .cameras import colmap_pose_to_c2w_gl
from .ply_io import write_points_ply

# model_id -> (name, num_params)
CAMERA_MODELS = {
    0: ("SIMPLE_PINHOLE", 3), 1: ("PINHOLE", 4), 2: ("SIMPLE_RADIAL", 4), 3: ("RADIAL", 5),
    4: ("OPENCV", 8), 5: ("OPENCV_FISHEYE", 8), 6: ("FULL_OPENCV", 12),
}


@dataclass
class ColmapCamera:
    id: int
    model: str
    width: int
    height: int
    params: np.ndarray

    def pinhole(self) -> tuple[float, float, float, float]:
        """(fx, fy, cx, cy). Only distortion-free models are accepted."""
        p = self.params
        if self.model == "SIMPLE_PINHOLE":
            return p[0], p[0], p[1], p[2]
        if self.model == "PINHOLE":
            return p[0], p[1], p[2], p[3]
        raise ValueError(f"camera {self.id}: model {self.model} has distortion; undistort the images first")


@dataclass
class ColmapImage:
    id: int
    qvec: np.ndarray
    tvec: np.ndarray
    camera_id: int
    name: str
    xys: np.ndarray          # Nx2 keypoints, pixels of the original camera
    point3d_ids: np.ndarray  # N, -1 = untriangulated


def _read(fh, fmt):
    return struct.unpack("<" + fmt, fh.read(struct.calcsize("<" + fmt)))


def read_cameras_bin(path) -> dict[int, ColmapCamera]:
    cams = {}
    with open(path, "rb") as fh:
        (n,) = _read(fh, "Q")
        for _ in range(n):
            cam_id, model_id, w, h = _read(fh, "iiQQ")
            name, nparams = CAMERA_MODELS[model_id]
            cams[cam_id] = ColmapCamera(cam_id, name, w, h, np.array(_read(fh, "d" * nparams)))
    return cams


def read_images_bin(path) -> dict[int, ColmapImage]:
    images = {}
    with open(path, "rb") as fh:
        (n,) = _read(fh, "Q")
        for _ in range(n):
            img_id, *pose, cam_id = _read(fh, "idddddddi")
            name = b""
            while (c := fh.read(1)) != b"\x00":
                name += c
            (npts,) = _read(fh, "Q")
            pts = np.frombuffer(fh.read(24 * npts), dtype=[("x", "<f8"), ("y", "<f8"), ("id", "<i8")])
            images[img_id] = ColmapImage(
                img_id, np.array(pose[:4]), np.array(pose[4:]), cam_id, name.decode(),
                np.stack([pts["x"], pts["y"]], axis=1), pts["id"].copy(),
            )
    return images


def read_points3d_bin(path) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Returns (ids N, xyz Nx3, rgb Nx3 uint8)."""
    data = Path(path).read_bytes()
    (n,) = struct.unpack_from("<Q", data, 0)
    off = 8
    ids, xyz, rgb = np.empty(n, np.int64), np.empty((n, 3)), np.empty((n, 3), np.uint8)
    head = struct.Struct("<QdddBBBdQ")
    for i in range(n):
        pid, x, y, z, r, g, b, _err, track_len = head.unpack_from(data, off)
        ids[i], xyz[i], rgb[i] = pid, (x, y, z), (r, g, b)
        off += head.size + 8 * track_len
    return ids, xyz, rgb


def convert_colmap(scene_dir, images_subdir: str = "images", sparse_subdir: str = "sparse/0",
                   manifest_name: str = "manifest.json") -> Path:
    """Write <scene_dir>/manifest.json (+ points3d.ply) from a COLMAP model.

    Intrinsics are rescaled to the actual size of the images in `images_subdir`
    (e.g. mip-NeRF 360's `images_4` are 4x downscaled).
    """
    from PIL import Image

    scene_dir = Path(scene_dir)
    sparse = scene_dir / sparse_subdir
    cams = read_cameras_bin(sparse / "cameras.bin")
    images = read_images_bin(sparse / "images.bin")

    frames = []
    for img in sorted(images.values(), key=lambda im: im.name):
        rel = f"{images_subdir}/{img.name}"
        path = scene_dir / rel
        if not path.exists():
            continue
        cam = cams[img.camera_id]
        with Image.open(path) as im:
            w, h = im.size
        sx, sy = w / cam.width, h / cam.height
        fx, fy, cx, cy = cam.pinhole()
        frames.append({
            "file_path": rel,
            "transform_matrix": colmap_pose_to_c2w_gl(img.qvec, img.tvec).tolist(),
            "fl_x": fx * sx, "fl_y": fy * sy, "cx": cx * sx, "cy": cy * sy, "w": w, "h": h,
        })
    if not frames:
        raise FileNotFoundError(f"no images from the COLMAP model found under {scene_dir / images_subdir}")

    manifest = {
        "schema_version": SCHEMA_VERSION,
        "capture_id": scene_dir.name,
        "source": "colmap",
        "camera_model": "PINHOLE",
        "frames": frames,
    }
    points_file = sparse / "points3D.bin"
    if points_file.exists():
        _, xyz, rgb = read_points3d_bin(points_file)
        write_points_ply(scene_dir / "points3d.ply", xyz, rgb)
        manifest["points3d_path"] = "points3d.ply"

    out = scene_dir / manifest_name
    out.write_text(json.dumps(manifest, indent=1))
    return out
