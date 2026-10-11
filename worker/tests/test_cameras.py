from pathlib import Path

import numpy as np
import pytest

from gs_pipeline.cameras import (c2w_cv_to_gl, c2w_gl_to_cv, colmap_pose_to_c2w_gl, intrinsics_matrix, project,
                                 qvec_to_rotmat, scale_intrinsics, viewmat_from_c2w_gl)
from conftest import BONSAI_DIR, look_at_c2w_gl


def test_gl_cv_round_trip():
    c2w = look_at_c2w_gl([1, 2, 3], [0, 0, 0])
    assert np.allclose(c2w_cv_to_gl(c2w_gl_to_cv(c2w)), c2w)


def test_camera_looking_at_target_projects_to_principal_point():
    K = intrinsics_matrix(500, 500, 320, 240)
    c2w = look_at_c2w_gl([0, 0, 5], [0, 0, 0])
    uv, depth = project(np.array([[0, 0, 0], [0, 1, 0], [1, 0, 0]]), viewmat_from_c2w_gl(c2w), K)
    assert np.allclose(uv[0], [320, 240]) and np.isclose(depth[0], 5)
    assert uv[1, 1] < 240  # world +y (up) appears above the center: image y points down
    assert uv[2, 0] > 320  # world +x appears to the right for a camera on +z looking at origin


def test_colmap_pose_conversion_matches_colmap_projection():
    rng = np.random.default_rng(0)
    q = rng.normal(size=4)
    t = rng.normal(size=3)
    K = intrinsics_matrix(800, 810, 400, 300)
    pts = rng.normal(size=(50, 3)) + [0, 0, 10]
    cam = pts @ qvec_to_rotmat(q).T + t  # COLMAP: x_cam = R p + t
    expected = cam[:, :2] / cam[:, 2:] * [800, 810] + [400, 300]
    uv, _ = project(pts, viewmat_from_c2w_gl(colmap_pose_to_c2w_gl(q, t)), K)
    assert np.allclose(uv, expected)


def test_scale_intrinsics():
    K = scale_intrinsics(intrinsics_matrix(1000, 1000, 500, 400), 0.25, 0.25)
    assert np.allclose(K, intrinsics_matrix(250, 250, 125, 100))


@pytest.mark.skipif(not BONSAI_DIR, reason="set BONSAI_DIR to a mip-NeRF 360 scene to run")
def test_real_colmap_keypoints_reproject_through_manifest():
    """End-to-end convention check on real data: COLMAP 3D points, projected via our manifest
    (converted pose + rescaled intrinsics), must land on COLMAP's own 2D keypoints."""
    from gs_pipeline.colmap_io import convert_colmap, read_cameras_bin, read_images_bin, read_points3d_bin
    from gs_pipeline.manifest import load_frames

    scene = Path(BONSAI_DIR)
    frames, _ = load_frames(convert_colmap(scene, "images_8"))
    cam = next(iter(read_cameras_bin(scene / "sparse/0/cameras.bin").values()))
    images = {im.name: im for im in read_images_bin(scene / "sparse/0/images.bin").values()}
    ids, xyz, _ = read_points3d_bin(scene / "sparse/0/points3D.bin")
    lookup = dict(zip(ids.tolist(), range(len(ids))))

    errors = []
    for fr in frames[::25]:
        im = images[fr.image_path.name]
        valid = im.point3d_ids >= 0
        pts = xyz[[lookup[i] for i in im.point3d_ids[valid]]]
        uv, depth = project(pts, viewmat_from_c2w_gl(fr.c2w_gl), fr.K)
        assert (depth > 0).all()
        expected = im.xys[valid] * [fr.width / cam.width, fr.height / cam.height]
        errors.append(np.linalg.norm(uv - expected, axis=1))
    err = np.concatenate(errors)
    assert np.median(err) < 0.5, f"median reprojection error {np.median(err):.3f}px"
