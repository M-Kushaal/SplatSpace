"""Camera conventions — the single place where coordinate frames are converted.

Manifest poses: camera-to-world, OpenGL / nerfstudio convention
    (x right, y up, camera looks down -z).
gsplat + COLMAP: OpenCV convention
    (x right, y down, camera looks down +z), world-to-camera "viewmat".

Pixel coordinates follow COLMAP/gsplat: origin at the top-left corner of the
top-left pixel, so pixel centers are at +0.5 and scaling an image by s scales
fx, fy, cx, cy all by s.
"""

import numpy as np

# Right-multiplying a c2w by this flips the camera's own y and z axes.
GL_TO_CV = np.diag([1.0, -1.0, -1.0, 1.0])


def c2w_gl_to_cv(c2w_gl: np.ndarray) -> np.ndarray:
    return c2w_gl @ GL_TO_CV


def c2w_cv_to_gl(c2w_cv: np.ndarray) -> np.ndarray:
    return c2w_cv @ GL_TO_CV


def viewmat_from_c2w_gl(c2w_gl: np.ndarray) -> np.ndarray:
    """World-to-camera (OpenCV) matrix, as gsplat's `viewmats` expects."""
    return np.linalg.inv(c2w_gl_to_cv(c2w_gl))


def intrinsics_matrix(fl_x: float, fl_y: float, cx: float, cy: float) -> np.ndarray:
    return np.array([[fl_x, 0.0, cx], [0.0, fl_y, cy], [0.0, 0.0, 1.0]])


def scale_intrinsics(K: np.ndarray, sx: float, sy: float) -> np.ndarray:
    K = K.copy()
    K[0] *= sx
    K[1] *= sy
    K[2] = [0.0, 0.0, 1.0]
    return K


def qvec_to_rotmat(q) -> np.ndarray:
    """Unit quaternion (w, x, y, z) -> 3x3 rotation matrix."""
    w, x, y, z = np.asarray(q, dtype=np.float64) / np.linalg.norm(q)
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y)],
        [2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x)],
        [2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y)],
    ])


def colmap_pose_to_c2w_gl(qvec, tvec) -> np.ndarray:
    """COLMAP image pose (world-to-camera, OpenCV) -> manifest c2w (OpenGL)."""
    w2c = np.eye(4)
    w2c[:3, :3] = qvec_to_rotmat(qvec)
    w2c[:3, 3] = tvec
    return c2w_cv_to_gl(np.linalg.inv(w2c))


def project(points_world: np.ndarray, viewmat: np.ndarray, K: np.ndarray):
    """Project Nx3 world points. Returns (uv Nx2, depth N); depth>0 = in front."""
    pts = np.asarray(points_world, dtype=np.float64)
    cam = pts @ viewmat[:3, :3].T + viewmat[:3, 3]
    depth = cam[:, 2]
    with np.errstate(divide="ignore", invalid="ignore"):
        uv = (cam[:, :2] / depth[:, None]) * [K[0, 0], K[1, 1]] + [K[0, 2], K[1, 2]]
    return uv, depth


def camera_center(c2w: np.ndarray) -> np.ndarray:
    return c2w[:3, 3]


def camera_forward_gl(c2w_gl: np.ndarray) -> np.ndarray:
    """World-space viewing direction of an OpenGL-convention camera."""
    return -c2w_gl[:3, 2]
