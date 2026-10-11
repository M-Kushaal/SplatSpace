"""Room-quality upgrades: depth priors, hole report, needle filter, scene-profile training (dry run)."""

import argparse
import json

import numpy as np
import pytest

from conftest import look_at_c2w_gl
from gs_pipeline.cameras import intrinsics_matrix, project, viewmat_from_c2w_gl
from gs_pipeline.coverage import FACES, cubemap_sheet, cubemap_viewmats, face_K, hole_fraction, hole_report
from gs_pipeline.depth import (backproject, consistent_views, dense_points, depth_size, fit_disparity, metric_depth,
                               sparse_samples, voxel_merge)
from gs_pipeline.manifest import CameraFrame
from gs_pipeline.postprocess import needle_mask
from test_scene import STATIONS, _gaussians, scene_manifest, station_c2ws

K = intrinsics_matrix(60.0, 60.0, 40.0, 30.0)  # 80 x 60 px


def _wall_depth(c2w_gl, K, w, h, wall_z=-3.0):
    """Exact depth map of the plane z = wall_z seen by a camera (0 where it isn't hit)."""
    c2w_cv = np.linalg.inv(viewmat_from_c2w_gl(c2w_gl))
    rows, cols = np.mgrid[0:h, 0:w]
    rays = np.stack([(cols + 0.5 - K[0, 2]) / K[0, 0], (rows + 0.5 - K[1, 2]) / K[1, 1], np.ones((h, w))], -1)
    dirs = rays @ c2w_cv[:3, :3].T
    t = (wall_z - c2w_cv[2, 3]) / dirs[..., 2]
    return np.where(t > 0, t, 0.0)


def test_depth_size_keeps_aspect():
    assert depth_size(1920, 1080) == (518, 291) and depth_size(1080, 1920) == (291, 518)


def test_fit_disparity_recovers_scale_shift_despite_occluded_points():
    rng = np.random.default_rng(0)
    z = rng.uniform(0.8, 4.0, 400)
    mono = (1 / z - 0.05) / 2.5            # network output: disparity up to scale 2.5 and shift 0.05
    z_obs = z.copy()
    z_obs[:80] *= rng.uniform(1.5, 3.0, 80)  # 20% of sparse points hidden behind nearer surfaces
    a, b, inl = fit_disparity(mono, z_obs)
    assert a == pytest.approx(2.5, rel=1e-3) and b == pytest.approx(0.05, abs=1e-3)
    assert inl[80:].all() and inl[:80].mean() < 0.1
    assert np.allclose(metric_depth(mono, a, b), z, rtol=1e-3)


def test_fit_disparity_gives_up_without_data():
    a, _, inl = fit_disparity(np.array([0.3]), np.array([2.0]))
    assert a == 0 and not inl.any()
    assert metric_depth(np.array([-1.0, 0.5]), 1.0, 0.0)[0] == 0  # negative disparity = no depth


def test_backproject_and_project_round_trip_and_sparse_samples():
    c2w = look_at_c2w_gl([0.3, 1.2, 0.5], [0.0, 1.0, -3.0])
    d = _wall_depth(c2w, K, 80, 60)
    pts, px = backproject(d, K, c2w, stride=4)
    assert np.allclose(pts[:, 2], -3.0, atol=1e-6)            # all on the wall
    uv, z = project(pts, viewmat_from_c2w_gl(c2w), K)
    assert np.allclose(uv, px[:, ::-1] + 0.5, atol=1e-6) and np.allclose(z, d[px[:, 0], px[:, 1]])
    cols_rows, z2 = sparse_samples(np.vstack([pts, [[0, 1, 5.0]]]), viewmat_from_c2w_gl(c2w), K, 80, 60)
    assert len(z2) == len(pts)                                 # the point behind the camera is dropped
    assert np.array_equal(cols_rows, px[:, ::-1])


def _frames_on_wall(n=4):
    eyes = [[x, 1.2, 0.0] for x in np.linspace(-0.6, 0.6, n)]
    return [CameraFrame(image_path=None, K=K, width=80, height=60, c2w_gl=look_at_c2w_gl(e, [0.0, 1.2, -3.0]),
                        index=i) for i, e in enumerate(eyes)]


def test_dense_points_keep_only_multi_view_consistent_surfaces():
    frames = _frames_on_wall()
    depths = [_wall_depth(f.c2w_gl, K, 80, 60) for f in frames]
    depths[0] = depths[0] * 0.7  # a badly aligned frame: its points float in front of the wall
    colors = [np.full((60, 80, 3), 200, np.uint8) for _ in frames]
    xyz, rgb = dense_points(frames, depths, colors, stride=4, voxel=0.02)
    assert len(xyz) > 100 and np.allclose(xyz[:, 2], -3.0, atol=0.03) and (rgb == 200).all()
    vms = [viewmat_from_c2w_gl(f.c2w_gl) for f in frames]
    bad, _ = backproject(depths[0], K, frames[0].c2w_gl, 4)
    assert consistent_views(bad, depths, [K] * 4, vms, skip=0).max() == 0


def test_voxel_merge_averages_per_voxel():
    xyz = np.array([[0.001, 0, 0], [0.009, 0, 0], [0.5, 0, 0]])
    rgb = np.array([[0, 0, 0], [100, 100, 100], [7, 7, 7]], np.uint8)
    m, c = voxel_merge(xyz, rgb, 0.02)
    assert len(m) == 2 and np.allclose(sorted(m[:, 0]), [0.005, 0.5]) and {50, 7} == set(c[:, 0])


def test_cubemap_faces_look_where_named():
    vms = cubemap_viewmats([1.0, 1.2, -2.0])
    for vm, (fwd, down) in zip(vms, FACES.values()):
        R = vm[:3, :3]
        assert np.allclose(R @ R.T, np.eye(3)) and np.isclose(np.linalg.det(R), 1.0)
        target = np.array([1.0, 1.2, -2.0]) + 2 * np.array(fwd, float)
        uv, z = project(target[None], vm, face_K(64))
        assert z[0] == pytest.approx(2.0) and np.allclose(uv[0], 32)  # straight ahead = image centre


def test_hole_report_counts_see_through_pixels(tmp_path):
    def render(vm, K, w, h):  # left half of every face is see-through
        alpha = np.ones((h, w))
        alpha[:, : w // 2] = 0.1
        return np.full((h, w, 3), 0.5), alpha

    m = scene_manifest(station_c2ws(STATIONS[:2]), STATIONS[:2])
    rep = hole_report(render, m, 0.0, tmp_path)
    assert rep["hole_fraction"] == pytest.approx(0.5) and rep["hole_fraction_per_station"] == [0.5, 0.5]
    assert (tmp_path / "holes_station1.png").exists()
    assert hole_fraction(np.ones((4, 4))) == 0
    assert cubemap_sheet([np.zeros((8, 8, 3), np.uint8)] * 6).shape == (16, 32, 3)


def test_needle_mask_drops_sticks_but_keeps_wall_discs():
    g = _gaussians(np.zeros((4, 3)))
    g["scales_log"] = np.log(np.array([
        [0.40, 0.01, 0.01],   # needle: long and thin -> drop
        [0.40, 0.30, 0.002],  # big flat disc on a blank wall -> keep
        [0.05, 0.002, 0.002], # short thin stick -> keep (not long enough to matter)
        [0.01, 0.01, 0.01],   # ordinary
    ], np.float32))
    assert needle_mask(g).tolist() == [False, True, True, True]


torch = pytest.importorskip("torch")


def test_se3_exp_identity_and_rotation():
    from gs_pipeline.train import se3_exp

    assert torch.allclose(se3_exp(torch.zeros(6)), torch.eye(4))
    T = se3_exp(torch.tensor([0, 0, np.pi / 2, 1.0, 2.0, 3.0]))
    assert torch.allclose(T[:3, :3] @ torch.tensor([1.0, 0, 0]), torch.tensor([0.0, 1.0, 0]), atol=1e-6)
    assert torch.allclose(T[:3, 3], torch.tensor([1.0, 2.0, 3.0]))
    d = torch.zeros(6, requires_grad=True)
    se3_exp(d).sum().backward()
    assert torch.isfinite(d.grad).all()


def test_depth_loss_weight_decays():
    from gs_pipeline.train import depth_loss_weight

    assert depth_loss_weight(0, 100, 0.2, 0.01) == pytest.approx(0.2)
    assert depth_loss_weight(99, 100, 0.2, 0.01) == pytest.approx(0.01)
    assert depth_loss_weight(5, 100, 0.0, 0.01) == 0


def test_visible_in_view_ignores_occluded_and_offscreen():
    from gs_pipeline.train import visible_in_view

    means = torch.tensor([[0.0, 0.0, 2.0], [0.0, 0.0, 5.0], [10.0, 0.0, 2.0], [0.0, 0.0, -1.0]])
    depth = torch.full((60, 80), 2.0)  # surface 2 m in front
    radii = torch.tensor([[3, 3], [3, 3], [3, 3], [0, 0]])
    vis = visible_in_view(means, torch.eye(4), torch.tensor(K, dtype=torch.float32), radii, depth)
    assert vis.tolist() == [True, False, False, False]  # on surface / hidden / off-screen / behind


def test_scene_profile_turns_on_the_upgrades():
    from gs_pipeline.train_config import add_train_args, resolve_profile

    p = add_train_args(argparse.ArgumentParser())
    a = p.parse_args(["m.json"])
    resolve_profile(a, {"capture_mode": "scene"})
    assert (a.strategy, a.depth, a.appearance, a.pose_opt, a.antialiased, a.min_views) == \
        ("mcmc", "auto", "on", "on", "on", 3)
    a = p.parse_args(["m.json", "--appearance", "off"])
    resolve_profile(a, {})
    assert (a.strategy, a.depth, a.appearance, a.min_views, a.max_init_points) == ("default", "off", "off", 0, 300_000)


def test_scene_training_dry_run_with_depth_priors(tmp_path, write_manifest):
    pytest.importorskip("gsplat")
    pytest.importorskip("scipy")
    from PIL import Image

    from gs_pipeline.depth import ALIGN_FILE, DENSE_FILE, depth_size
    from gs_pipeline.ply_io import write_points_ply
    from gs_pipeline.train import train
    from gs_pipeline.train_config import add_train_args

    stations = STATIONS[:2]
    m = scene_manifest(station_c2ws(stations, per_ring=8), stations, w=64, h=48)
    m.update(fl_x=40.0, fl_y=40.0, points3d_path="points3d.ply")  # ~77 degree view, like a phone
    mp = write_manifest(m)
    (mp.parent / "images").mkdir()
    rng = np.random.default_rng(0)
    for f in m["frames"]:
        Image.fromarray(rng.integers(0, 255, (48, 64, 3), dtype=np.uint8)).save(mp.parent / f["file_path"])
    write_points_ply(mp.parent / "points3d.ply", rng.uniform([-2, 0, -1.5], [2, 2.6, 1.5], (300, 3)), None)
    dd = tmp_path / "depth"
    dd.mkdir()
    w, h = depth_size(64, 48)
    align = {}
    for f in m["frames"]:
        key = f["file_path"].split("/")[-1][:-4]
        np.save(dd / f"{key}.npy", np.full((h, w), 0.4, np.float16))
        align[key] = {"a": 1.0, "b": 0.1, "points": 100, "inliers": 100, "median_rel_err": 0.01}
    align[key]["inliers"] = 3  # one badly aligned frame gets no prior
    (dd / ALIGN_FILE).write_text(json.dumps({"report": {}, "frames": align}))
    write_points_ply(dd / DENSE_FILE, rng.uniform([-2, 0, -1.5], [2, 2.6, 1.5], (200, 3)),
                     np.full((200, 3), 90, np.uint8))

    args = add_train_args(argparse.ArgumentParser()).parse_args(
        [str(mp), "--out", str(tmp_path / "out"), "--dry-run", "--steps", "12", "--log-every", "6",
         "--depth-dir", str(dd), "--test-every", "4"])
    metrics = train(args)
    assert metrics["strategy"] == "mcmc" and metrics["rasterize_mode"] == "antialiased"
    assert metrics["depth_priors"] == 23  # 32 frames - 8 held out - 1 unaligned
    assert "depth prior (200 pts)" in metrics["init"]
    prune = metrics["min_views_prune"]
    assert prune["step"] == 9 and prune["kept"] + prune["removed"] == 500  # inside training, at 80%
    assert prune["kept"] > 0 and prune["removed"] > 0
    assert metrics["psnr_test_color_matched_mean"] >= metrics["psnr_test_mean"] - 1e-6
    assert metrics["holes"]["hole_fraction_per_station"] and metrics["pose_opt"] is not None
    assert (tmp_path / "out" / "previews" / "holes_station0.png").exists()


def test_color_matched_psnr_forgives_exposure_but_not_content():
    from gs_pipeline.train import color_matched_psnr

    g = torch.Generator().manual_seed(0)
    gt = torch.rand(40, 50, 3, generator=g) * 0.6 + 0.2
    darker = (gt * 0.7 + 0.05).clamp(0, 1)  # same picture, different exposure
    other = torch.rand(40, 50, 3, generator=g)
    assert color_matched_psnr(darker, gt) > 40
    assert color_matched_psnr(other, gt) < 20  # the fit can only match the average colour


def test_spots_snap_to_their_camera_clusters_but_not_to_others():
    from gs_pipeline.coverage import snap_to_cameras

    rng = np.random.default_rng(0)
    ring = lambda c: np.array(c) + 0.4 * np.stack([np.cos(t := np.linspace(0, 6.2, 24)), np.sin(t)], 1)
    cams = np.vstack([ring([0, 0]), ring([2, 0]), rng.normal([5, 3], 0.2, (10, 2))])
    spots = np.array([[0.3, 0.2], [1.6, -0.3], [7.5, 3.0]])  # 2 drifted spots + one whose photos are missing
    snapped = snap_to_cameras(spots, cams)
    assert np.allclose(snapped[0], [0, 0], atol=0.1) and np.allclose(snapped[1], [2, 0], atol=0.1)
    assert np.allclose(snapped[2], [7.5, 3.0]), "would move > 1 m onto someone else's cluster: kept"


def test_refine_carries_spot_and_sharpness_per_photo():
    from gs_pipeline.refine import carry_frame_fields

    orig = {"frames": [{"file_path": "images/000001.jpg", "target_id": 214, "blur_score": 9.5, "tracking_state": "x"}]}
    refined = {"frames": [{"file_path": "images/000001.jpg"}, {"file_path": "images/000002.jpg"}]}
    carry_frame_fields(refined, orig)
    assert refined["frames"][0] == {"file_path": "images/000001.jpg", "target_id": 214, "blur_score": 9.5}
    assert refined["frames"][1] == {"file_path": "images/000002.jpg"}
