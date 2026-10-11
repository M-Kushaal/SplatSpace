"""Hole report for scene (room) splats: where can a visitor see through the room?

From every capture spot (extensions.scene.stations) a 90-degree cubemap is rendered at standing eye
height. Pixels whose accumulated opacity stays below HOLE_ALPHA see through the splat (in the viewer the
grid or background shows there). The hole fraction is the room-quality KPI, and the images (holes painted
magenta) show the user what to re-shoot.
"""

import numpy as np

from .cameras import intrinsics_matrix
from .scene import scene_info

HOLE_ALPHA = 0.5
EYE_HEIGHT_M = 1.2
FACE_PX = 256

# Cubemap faces as (forward, down) in world axes (y up). OpenCV camera: x right, y down, z forward.
FACES = {
    "front": ((0, 0, -1), (0, -1, 0)), "right": ((1, 0, 0), (0, -1, 0)),
    "back": ((0, 0, 1), (0, -1, 0)), "left": ((-1, 0, 0), (0, -1, 0)),
    "up": ((0, 1, 0), (0, 0, 1)), "down": ((0, -1, 0), (0, 0, -1)),
}


def face_viewmat(eye, forward, down) -> np.ndarray:
    """World-to-camera (OpenCV) for a camera at eye looking along forward with image-down = down."""
    z = np.asarray(forward, float)
    y = np.asarray(down, float)
    x = np.cross(y, z)
    R = np.stack([x, y, z])  # rows = camera axes in world coords
    vm = np.eye(4)
    vm[:3, :3] = R
    vm[:3, 3] = -R @ np.asarray(eye, float)
    return vm


def cubemap_viewmats(eye) -> list[np.ndarray]:
    return [face_viewmat(eye, f, d) for f, d in FACES.values()]


def face_K(size: int = FACE_PX) -> np.ndarray:
    return intrinsics_matrix(size / 2, size / 2, size / 2, size / 2)  # 90 degree field of view


def snap_to_cameras(spots_xz: np.ndarray, cameras_xz: np.ndarray, iters: int = 10, max_move: float = 1.0) -> np.ndarray:
    """Move recorded spot centres onto the camera clusters they belong to (k-means seeded at the spots).
    The app records spots in the AR world, which drifted up to ~2 m on the first room, while the cameras
    are COLMAP-refined; a drifted spot can sit inside furniture or a wall and see "holes" that aren't there.
    A spot that would move more than max_move keeps its recorded position: its own photos are missing (on the
    first room COLMAP dropped all of spot 4's) and the nearest cluster belongs to something else."""
    c = spots_xz.astype(float).copy()
    if len(cameras_xz) == 0 or len(c) == 0:
        return c
    for _ in range(iters):
        nearest = np.argmin(np.linalg.norm(cameras_xz[:, None] - c[None], axis=-1), axis=1)
        for k in range(len(c)):
            members = cameras_xz[nearest == k]
            if len(members):
                c[k] = np.median(members, axis=0)
    far = np.linalg.norm(c - spots_xz, axis=1) > max_move
    c[far] = spots_xz[far]
    return c


def station_eyes(manifest: dict, floor_y: float, camera_centers: np.ndarray | None = None) -> list[np.ndarray]:
    """Standing eye positions above each capture spot (training coordinates)."""
    spots = np.array([[s["center"][0], s["center"][2]] for s in scene_info(manifest).get("stations", [])
                      if "center" in s], float).reshape(-1, 2)
    if camera_centers is not None and len(spots):
        spots = snap_to_cameras(spots, np.asarray(camera_centers, float)[:, [0, 2]])
    return [np.array([x, floor_y + EYE_HEIGHT_M, z], float) for x, z in spots]


def hole_fraction(alpha: np.ndarray) -> float:
    return float((np.asarray(alpha) < HOLE_ALPHA).mean())


def paint_holes(rgb: np.ndarray, alpha: np.ndarray) -> np.ndarray:
    """uint8 image: the render, with see-through pixels painted magenta."""
    img = (np.clip(np.nan_to_num(rgb), 0, 1) * 255).astype(np.uint8)
    img[np.asarray(alpha) < HOLE_ALPHA] = (255, 0, 255)
    return img


def cubemap_sheet(faces: list[np.ndarray]) -> np.ndarray:
    """6 faces (front right back left / up down) -> one 4x2 image sheet."""
    s = faces[0].shape[0]
    sheet = np.zeros((2 * s, 4 * s, 3), np.uint8)
    for k, f in enumerate(faces):
        r, c = divmod(k, 4)
        sheet[r * s:(r + 1) * s, c * s:(c + 1) * s] = f
    return sheet


def hole_report(render_fn, manifest: dict, floor_y: float, out_dir, camera_centers=None) -> dict:
    """render_fn(viewmat 4x4, K 3x3, w, h) -> (rgb HxWx3 float, alpha HxW). Saves holes_station<k>.png."""
    from PIL import Image

    eyes = station_eyes(manifest, floor_y, camera_centers)
    per_station, alphas = [], []
    K = face_K()
    for k, eye in enumerate(eyes):
        faces = []
        station_alpha = []
        for vm in cubemap_viewmats(eye):
            rgb, alpha = render_fn(vm, K, FACE_PX, FACE_PX)
            station_alpha.append(alpha)
            faces.append(paint_holes(rgb, alpha))
        Image.fromarray(cubemap_sheet(faces)).save(f"{out_dir}/holes_station{k}.png")
        per_station.append(round(hole_fraction(np.stack(station_alpha)), 4))
        alphas += station_alpha
    return {"hole_fraction": round(hole_fraction(np.stack(alphas)), 4) if alphas else None,
            "hole_fraction_per_station": per_station, "eye_height_m": EYE_HEIGHT_M,
            "eyes": [[round(float(v), 3) for v in e] for e in eyes]}
