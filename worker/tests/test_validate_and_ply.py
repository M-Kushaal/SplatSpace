import numpy as np

from gs_pipeline.cameras import c2w_gl_to_cv
from gs_pipeline.ply_io import read_gaussians_ply, read_points_ply, write_gaussians_ply, write_points_ply
from gs_pipeline.validate import validate_manifest
from conftest import orbit_manifest


def test_good_orbit_passes(write_manifest):
    rep = validate_manifest(write_manifest(orbit_manifest()), check_files=False)
    assert rep.ok, rep.summary()
    assert rep.stats["frames seeing target"] == "36/36"


def test_opencv_convention_poses_are_rejected(write_manifest):
    m = orbit_manifest()
    for f in m["frames"]:  # the classic mistake: writing OpenCV c2w where OpenGL is expected
        f["transform_matrix"] = c2w_gl_to_cv(np.array(f["transform_matrix"])).tolist()
    rep = validate_manifest(write_manifest(m), check_files=False)
    assert not rep.ok and "convention" in rep.summary()


def test_scaled_rotation_and_bad_schema_rejected(write_manifest):
    m = orbit_manifest()
    for row in m["frames"][0]["transform_matrix"][:3]:
        row[:3] = [2 * v for v in row[:3]]  # scaled rotation
    assert "orthonormal" in validate_manifest(write_manifest(m), check_files=False).summary()
    m = orbit_manifest()
    del m["frames"][0]["transform_matrix"]
    assert "schema" in validate_manifest(write_manifest(m), check_files=False).summary()


def test_missing_intrinsics_rejected(write_manifest):
    m = orbit_manifest()
    del m["fl_x"]
    assert "missing intrinsics" in validate_manifest(write_manifest(m), check_files=False).summary()


def test_points_ply_round_trip(tmp_path):
    xyz = np.random.default_rng(0).normal(size=(100, 3)).astype(np.float32)
    rgb = np.random.default_rng(1).integers(0, 255, size=(100, 3)).astype(np.uint8)
    write_points_ply(tmp_path / "p.ply", xyz, rgb)
    xyz2, rgb2 = read_points_ply(tmp_path / "p.ply")
    assert np.array_equal(xyz, xyz2) and np.array_equal(rgb, rgb2)


def test_gaussians_ply_round_trip(tmp_path):
    rng = np.random.default_rng(0)
    n = 50
    g = dict(means=rng.normal(size=(n, 3)), scales_log=rng.normal(size=(n, 3)), quats_wxyz=rng.normal(size=(n, 4)),
             opacities_logit=rng.normal(size=n), sh0=rng.normal(size=(n, 3)), shN=rng.normal(size=(n, 15, 3)))
    write_gaussians_ply(tmp_path / "s.ply", **g)
    back = read_gaussians_ply(tmp_path / "s.ply")
    for k, v in g.items():
        assert np.allclose(back[k], v, atol=1e-6), k
    header = (tmp_path / "s.ply").read_bytes()[:2000].decode("ascii", "ignore")
    assert "property float f_rest_44" in header and "property float rot_3" in header


def test_empty_gaussians_ply_round_trip(tmp_path):
    z = np.zeros((0, 3))
    write_gaussians_ply(tmp_path / "e.ply", means=z, scales_log=z, quats_wxyz=np.zeros((0, 4)),
                        opacities_logit=np.zeros(0), sh0=z, shN=np.zeros((0, 15, 3)))
    assert len(read_gaussians_ply(tmp_path / "e.ply")["means"]) == 0
