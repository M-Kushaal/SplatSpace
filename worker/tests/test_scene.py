import argparse

import numpy as np

from conftest import look_at_c2w_gl, orbit_manifest
from gs_pipeline.ply_io import read_gaussians_ply, write_gaussians_ply, write_points_ply
from gs_pipeline.postprocess import postprocess
from gs_pipeline.scene import capture_mode, crop_box, parallax_check, select_pairs_scene
from gs_pipeline.train_config import add_train_args, resolve_profile
from gs_pipeline.validate import validate_manifest

ROOM = np.array([4.0, 2.6, 3.0])  # x, y (height), z extent; floor at y = 0


def station_c2ws(stations, per_ring=24, arm=0.4, eye=1.4, pitches=(0.0, -35.0)):
    """Phone at arm's length while the user turns in place: lens on a circle around each station."""
    c2ws = []
    for sx, sz in stations:
        for pitch in pitches:
            for k in range(per_ring):
                a = 2 * np.pi * k / per_ring
                d = np.array([np.sin(a), 0.0, -np.cos(a)])
                lens = np.array([sx, eye, sz]) + arm * d
                look = d * np.cos(np.radians(pitch)) + [0, np.sin(np.radians(pitch)), 0]
                c2ws.append(look_at_c2w_gl(lens, lens + look))
    return c2ws


def scene_manifest(c2ws, stations=None, w=640, h=480) -> dict:
    m = {"schema_version": "1.1", "capture_mode": "scene", "source": "synthetic", "camera_model": "PINHOLE",
         "fl_x": 500.0, "fl_y": 500.0, "cx": w / 2, "cy": h / 2, "w": w, "h": h,
         "frames": [{"file_path": f"images/{i:04d}.jpg", "transform_matrix": c.tolist()} for i, c in enumerate(c2ws)]}
    if stations is not None:
        m["extensions"] = {"scene": {"floor_y": 0.0, "stations": [{"center": [x, 0.0, z]} for x, z in stations]}}
    return m


STATIONS = [(-1.0, -0.8), (1.0, -0.8), (1.0, 0.8), (-1.0, 0.8), (0.0, 0.0)]


def test_scene_capture_validates(write_manifest):
    rep = validate_manifest(write_manifest(scene_manifest(station_c2ws(STATIONS), STATIONS)), check_files=False)
    assert rep.ok, rep.summary()
    assert rep.stats["capture mode"] == "scene" and rep.stats["stations"] == 5
    assert not any("target" in w for w in rep.warnings)  # object-only checks are skipped


def test_turning_in_place_is_rejected(write_manifest):
    """Rotating about the lens (arm = 0) is a panorama: no parallax, so no depth."""
    rep = validate_manifest(write_manifest(scene_manifest(station_c2ws([(0.0, 0.0)], arm=0.0))), check_files=False)
    assert not rep.ok and "no depth" in rep.errors[0]
    errors, _, spread = parallax_check(np.array([c[:3, 3] for c in station_c2ws([(0.0, 0.0)], arm=0.4)]))
    assert not errors and 0.35 < spread < 0.45  # one arm's-length spin already moves the lens ~0.8 m


def test_object_captures_are_unchanged(write_manifest):
    m = orbit_manifest()
    assert capture_mode(m) == "object"
    rep = validate_manifest(write_manifest(m), check_files=False)
    assert rep.ok and "frames seeing target" in rep.stats


def test_scene_pairs_link_stations_that_see_the_same_wall():
    c2ws = station_c2ws(STATIONS, per_ring=12, pitches=(0.0,))
    names = [f"{i:03d}.jpg" for i in range(len(c2ws))]
    pairs = set(select_pairs_scene(names, c2ws, k_nearest=4, k_cross=6))
    assert all(a < b for a, b in pairs) and len(pairs) < len(names) * (len(names) - 1) // 2
    # Every station is matched against the others (else COLMAP builds one model per station);
    # neighbouring corners (0-1, 1-2, ...) share many views of the same walls.
    links = {}
    for a, b in pairs:
        si, sj = int(a[:3]) // 12, int(b[:3]) // 12
        if si != sj:
            links[(si, sj)] = links.get((si, sj), 0) + 1
    assert all(links.get((s, s + 1), 0) >= 10 for s in range(4))
    assert all(sum(1 for (x, y) in links if s in (x, y)) >= 3 for s in range(5))
    # Opposite directions never pair (except as time neighbours).
    fwd = [-c[:3, 2] for c in c2ws]
    for a, b in pairs:
        i, j = int(a[:3]), int(b[:3])
        assert j - i <= 4 or fwd[i] @ fwd[j] > np.cos(np.radians(70)) - 1e-6


def test_profiles_pick_defaults_from_capture_mode():
    p = add_train_args(argparse.ArgumentParser())
    a = p.parse_args(["m.json"])
    assert resolve_profile(a, {"capture_mode": "scene"}) == "scene" and (a.steps, a.max_side) == (30000, 1600)
    a = p.parse_args(["m.json", "--steps", "500"])
    assert resolve_profile(a, {}) == "object" and (a.steps, a.max_side, a.cap_max) == (500, 1920, 0)


def _gaussians(xyz, opacity=0.6):
    n = len(xyz)
    return {"means": xyz.astype(np.float32), "scales_log": np.full((n, 3), -4.0, np.float32),
            "quats_wxyz": np.tile(np.float32([1, 0, 0, 0]), (n, 1)),
            "opacities_logit": np.full(n, np.log(opacity / (1 - opacity)), np.float32),
            "sh0": np.zeros((n, 3), np.float32), "shN": np.zeros((n, 15, 3), np.float32)}


def test_scene_postprocess_keeps_the_room_and_puts_the_floor_at_zero(tmp_path, write_manifest):
    rng = np.random.default_rng(0)
    offset = np.array([5.0, -1.2, 2.0])  # the AR world origin is wherever the session started
    walls = rng.random((4000, 3)) * ROOM - [ROOM[0] / 2, 0, ROOM[2] / 2] + offset
    far = rng.normal(size=(40, 3)) * 30 + offset  # sky-box junk far outside the room
    c2ws = station_c2ws(STATIONS)
    for c in c2ws:
        c[:3, 3] += offset
    m = scene_manifest(c2ws, STATIONS)
    m["extensions"]["scene"]["floor_y"] = float(offset[1])
    m["points3d_path"] = "points3d.ply"
    mp = write_manifest(m)
    write_points_ply(mp.parent / "points3d.ply", walls, None)
    src = tmp_path / "splat.ply"
    write_gaussians_ply(src, **_gaussians(np.vstack([walls, far])))
    out = tmp_path / "splat_object.ply"
    stats = postprocess(src, mp, out)
    g = read_gaussians_ply(out)
    assert stats["out"] >= 3900 and np.abs(g["means"]).max() < 4  # room kept, far junk cropped
    assert abs(np.percentile(g["means"][:, 1], 1)) < 0.1             # floor at y = 0
    assert np.allclose(np.median(g["means"][:, [0, 2]], axis=0), 0, atol=0.2)
    assert 1.2 < stats["view"]["eye"][1] < 1.6                        # start view at eye height


def test_crop_box_without_points_uses_cameras():
    centers = np.array([[0, 1.4, 0], [1, 1.4, 1]], float)
    lo, hi = crop_box(None, centers)
    assert np.all(lo < centers.min(0)) and np.all(hi > centers.max(0))


def test_floor_is_the_lowest_dense_layer_not_stray_points():
    from gs_pipeline.scene import floor_from_points

    rng = np.random.default_rng(0)
    floor = np.c_[rng.uniform(-2, 2, 3000), rng.normal(0.0, 0.01, 3000), rng.uniform(-2, 2, 3000)]
    table = np.c_[rng.uniform(-0.5, 0.5, 800), rng.normal(0.75, 0.01, 800), rng.uniform(-0.5, 0.5, 800)]
    junk = np.c_[rng.uniform(-2, 2, 200), rng.uniform(-1.8, -0.2, 200), rng.uniform(-2, 2, 200)]  # below the floor
    y = floor_from_points(np.vstack([floor, table, junk]), camera_y=1.5)
    assert abs(y) < 0.05
