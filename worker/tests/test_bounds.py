import numpy as np

from gs_pipeline.bounds import refine_bounds
from gs_pipeline.ply_io import read_points_ply, write_points_ply


def _scene(rng, floor_y=0.49, obj_xz=(0.0, -0.31), obj_r=0.07, obj_h=0.28):
    floor = np.c_[rng.uniform(-1.5, 1.5, 6000), floor_y + rng.normal(0, 0.005, 6000), rng.uniform(-1.8, 1.2, 6000)]
    t, h = rng.uniform(0, 2 * np.pi, 3000), rng.uniform(0, obj_h, 3000)
    obj = np.c_[obj_xz[0] + obj_r * np.cos(t), floor_y + h, obj_xz[1] + obj_r * np.sin(t)]
    return np.vstack([floor, obj])


def test_refine_recovers_object_when_user_cylinder_is_off():
    """Mirrors the first OnePlus capture: tapped 11 cm off to the side, on a plane 13 cm too low."""
    xyz = _scene(np.random.default_rng(0))
    manifest = {"object_bounds": {"center": [-0.03, 0.36, -0.42], "radius": 0.067, "height": 0.227}}
    b = refine_bounds(manifest, xyz)
    assert b.source.startswith("refined")
    assert abs(b.base[1] - 0.49) < 0.02
    assert np.hypot(b.base[0] - 0.0, b.base[2] + 0.31) < 0.03
    assert 0.2 < b.height < 0.32 and 0.05 < b.radius < 0.12


def test_refine_falls_back_to_user_cylinder_without_points():
    manifest = {"object_bounds": {"center": [0, 0, 0], "radius": 0.1, "height": 0.2}}
    assert refine_bounds(manifest, None).source == "user cylinder"
    assert refine_bounds(manifest, np.zeros((5, 3))).source == "user cylinder"
    assert refine_bounds({}, None) is None


def test_non_finite_points_are_dropped_on_read(tmp_path):
    xyz = np.array([[0, 0, 0], [np.nan, 1, 2], [1, np.inf, 0], [1, 2, 3]], np.float32)
    write_points_ply(tmp_path / "p.ply", xyz)
    out, _ = read_points_ply(tmp_path / "p.ply")
    assert out.tolist() == [[0, 0, 0], [1, 2, 3]]
