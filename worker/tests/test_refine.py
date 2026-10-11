import json
import sys
import types

import numpy as np
import pytest

from gs_pipeline.cameras import qvec_to_rotmat
from gs_pipeline.refine import effective_epochs, gravity_scale_similarity, main_epoch_mask, ransac_similarity, refine, robust_similarity, select_pairs, umeyama
from conftest import orbit_manifest


def test_umeyama_recovers_known_similarity():
    rng = np.random.default_rng(0)
    src = rng.normal(size=(40, 3))
    R, s, t = qvec_to_rotmat(rng.normal(size=4)), 6.7, np.array([0.3, -1.2, 2.0])
    dst = (s * (R @ src.T)).T + t
    s2, R2, t2 = umeyama(src, dst)
    assert np.isclose(s2, s) and np.allclose(R2, R) and np.allclose(t2, t)


def test_robust_similarity_ignores_drifted_cameras():
    rng = np.random.default_rng(1)
    src = rng.normal(size=(50, 3))
    R, s, t = qvec_to_rotmat(rng.normal(size=4)), 0.15, np.array([0.0, 0.8, -0.5])
    dst = (s * (R @ src.T)).T + t
    dst[:8] += rng.normal(scale=0.3, size=(8, 3))  # 16% badly drifted AR poses
    s2, R2, t2, res = robust_similarity(src, dst)
    assert abs(s2 - s) / s < 0.02 and np.allclose(R2, R, atol=0.02)
    assert np.median(res) < 0.01 and res[:8].min() > res[8:].max()


def test_select_pairs_has_time_and_view_neighbours_without_duplicates():
    a = np.linspace(0, 2 * np.pi, 30, endpoint=False)
    centers = np.c_[np.cos(a), np.zeros(30), np.sin(a)]
    order = np.random.default_rng(2).permutation(30)  # capture order != angular order
    names = [f"{i:02d}.jpg" for i in range(30)]
    pairs = select_pairs(names, centers[order], np.zeros(3), k_nearest=4, time_window=2)
    assert len(pairs) == len(set(pairs)) and all(a < b for a, b in pairs)
    pset = set(pairs)
    assert ("00.jpg", "01.jpg") in pset and ("00.jpg", "02.jpg") in pset  # time neighbours
    capture_index = {k: int(np.where(order == k)[0][0]) for k in range(30)}  # angular slot -> capture index
    i, j = sorted((capture_index[0], capture_index[1]))  # angular neighbours, wherever they were captured
    assert (names[i], names[j]) in pset
    assert len(pairs) < 30 * 29 // 2  # far fewer than exhaustive


def test_refine_falls_back_to_ar_poses_when_colmap_fails(tmp_path, write_manifest, monkeypatch):
    """Synthetic images can't be matched; refine must report fallback and point at the AR manifest."""
    fake = types.SimpleNamespace(
        ImageReaderOptions=lambda: types.SimpleNamespace(),
        FeatureExtractionOptions=lambda: types.SimpleNamespace(sift=types.SimpleNamespace()),
        CameraMode=types.SimpleNamespace(SINGLE=1), Device=types.SimpleNamespace(cpu=0),
        extract_features=lambda *a, **k: (_ for _ in ()).throw(RuntimeError("no features")),
    )
    monkeypatch.setitem(sys.modules, "pycolmap", fake)
    m = orbit_manifest()
    mp = write_manifest(m)
    (mp.parent / "images").mkdir()
    report = refine(mp, tmp_path / "refined")
    assert report["fallback"] is True and "no features" in report["error"]
    assert report["manifest"] == str(mp.resolve())
    assert json.loads((tmp_path / "refined" / "refine_report.json").read_text())["fallback"] is True


def test_ransac_similarity_survives_a_map_jump():
    """40% of the cameras jumped 16 m (the second room): the fit follows the larger consistent group."""
    rng = np.random.default_rng(1)
    src = rng.uniform(-2, 2, (100, 3))
    R = np.array([[0.0, 0, 1], [0, 1, 0], [-1, 0, 0]])
    dst = (0.3 * (R @ src.T)).T + [1, 2, 3] + rng.normal(0, 0.03, (100, 3))
    dst[60:] += [0, 0, -16.0]
    s, R2, t, res, inl = ransac_similarity(src, dst)
    assert abs(s - 0.3) < 0.01 and np.allclose(R2, R, atol=0.02)
    assert inl[:60].all() and not inl[60:].any()
    _, _, _, res_trim = robust_similarity(src, dst)
    assert np.median(res_trim[:60]) > 1.0, "the trimmed fit is dragged by the jumped group"


def test_main_epoch_and_epoch_aware_pairs():
    m = {"frames": [{"file_path": f"images/{i}.jpg", "tracking_epoch": 0 if i < 3 else 1} for i in range(8)]}
    assert main_epoch_mask([f"{i}.jpg" for i in range(8)], m).tolist() == [False] * 3 + [True] * 5
    from gs_pipeline.scene import select_pairs_scene
    from conftest import look_at_c2w_gl

    c2ws = [look_at_c2w_gl([0.1 * i, 1.4, 0], [0.1 * i, 1.4, -2]) for i in range(8)]
    names = [str(i) for i in range(8)]
    pairs = select_pairs_scene(names, c2ws, k_nearest=7, k_cross=0, time_window=0, epochs=[0] * 4 + [1] * 4)
    assert pairs and all((int(a) < 4) == (int(b) < 4) for a, b in pairs), "AR pairs never cross epochs"


def test_gravity_and_scale_survive_broken_ar_positions():
    """AR positions jump 16 m half-way, but AR tilt (gravity) and short hops stay right."""
    from conftest import look_at_c2w_gl
    from gs_pipeline.cameras import c2w_gl_to_cv

    rng = np.random.default_rng(3)
    n = 60
    true_c2w = [look_at_c2w_gl([np.cos(a), 1.4, np.sin(a)], [3 * np.cos(a), 1.0 + rng.uniform(-0.5, 0.5), 3 * np.sin(a)])
                for a in np.linspace(0, 6.0, n)]
    # COLMAP world = true world rotated arbitrarily (tilted), scaled 0.4, shifted
    Q, _ = np.linalg.qr(rng.normal(size=(3, 3)))
    Q *= np.sign(np.linalg.det(Q))
    R_cw, centers = [], []
    for c in true_c2w:
        cv = c2w_gl_to_cv(c)
        Rw = Q @ cv[:3, :3]
        R_cw.append(Rw.T)
        centers.append(0.4 * Q @ cv[:3, 3] + [5, -2, 1])
    ar = [c.copy() for c in true_c2w]
    for c in ar[30:]:
        c[:3, 3] += [16.0, 0, -3.0]  # the map jump
    s, R, t = gravity_scale_similarity(np.array(R_cw), np.array(centers), np.array(ar), np.arange(n) * 1.0)
    assert abs(s - 2.5) < 0.05
    up = R @ (Q @ np.array([0.0, 1.0, 0.0]))
    assert up[1] > 0.999, "gravity recovered"


def test_small_epoch_splits_merge_but_a_real_jump_stays_split():
    def frame(i, epoch, x):
        m = np.eye(4)
        m[0, 3] = x
        return {"file_path": f"images/{i}.jpg", "timestamp": float(i), "tracking_epoch": epoch, "transform_matrix": m.tolist()}

    # epochs 0 -> 1 -> 2 with 20-30 cm corrections, then a 16 m restart into epoch 3, then a small split again
    xs = [0.0, 0.1, 0.3, 0.4, 0.7, 0.8, 16.8, 16.9, 17.1]
    eps = [0, 0, 1, 1, 2, 2, 3, 4, 4]
    eff = effective_epochs({"frames": [frame(i, e, x) for i, (e, x) in enumerate(zip(eps, xs))]})
    assert eff == [0, 0, 0, 0, 0, 0, 1, 1, 1]
