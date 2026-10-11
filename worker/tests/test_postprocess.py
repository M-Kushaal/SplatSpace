import numpy as np

from gs_pipeline.postprocess import (quat_multiply, rotate_shN, rotmat_to_quat_wxyz, sh_basis, transform_gaussians,
                                     upright_rotation)
from gs_pipeline.cameras import qvec_to_rotmat


def _random_rotation(seed):
    return qvec_to_rotmat(np.random.default_rng(seed).normal(size=4))


def test_sh_rotation_preserves_view_dependent_color():
    R = _random_rotation(1)
    shN = np.random.default_rng(2).normal(size=(5, 15, 3))
    d = np.random.default_rng(3).normal(size=(40, 3))
    d /= np.linalg.norm(d, axis=1, keepdims=True)
    before = np.einsum("mk,nkc->nmc", sh_basis(d), shN)            # color seen along d, old frame
    after = np.einsum("mk,nkc->nmc", sh_basis(d @ R.T), rotate_shN(shN, R))  # same ray, rotated frame
    assert np.allclose(before, after, atol=1e-8)


def test_quaternion_helpers_match_rotation_matrices():
    R1, R2 = _random_rotation(4), _random_rotation(5)
    q = quat_multiply(rotmat_to_quat_wxyz(R1), rotmat_to_quat_wxyz(R2)[None])[0]
    assert np.allclose(qvec_to_rotmat(q), R1 @ R2)


def test_upright_rotation_maps_up_to_y():
    for up in ([0.3, -0.2, 0.93], [0, -1, 0], [0, 1, 0], [1, 0, 0]):
        R = upright_rotation(np.array(up, float))
        assert np.allclose(R @ (np.array(up) / np.linalg.norm(up)), [0, 1, 0]) and np.isclose(np.linalg.det(R), 1)


def test_transform_gaussians_rotates_covariance():
    rng = np.random.default_rng(6)
    g = {"means": rng.normal(size=(3, 3)), "quats_wxyz": rng.normal(size=(3, 4)), "shN": rng.normal(size=(3, 15, 3))}
    R, t = _random_rotation(7), np.array([1.0, 2.0, 3.0])
    out = transform_gaussians(g, R, t)
    assert np.allclose(out["means"], g["means"] @ R.T + t)
    for q0, q1 in zip(g["quats_wxyz"], out["quats_wxyz"]):
        assert np.allclose(qvec_to_rotmat(q1), R @ qvec_to_rotmat(q0))


def test_floater_mask_removes_isolated_faint_splats_only():
    from gs_pipeline.postprocess import floater_mask

    rng = np.random.default_rng(0)
    dense = rng.uniform(0, 0.1, size=(5000, 3))                       # object surface: dense
    haze = rng.uniform(-0.5, 0.6, size=(60, 3)) * [1, 1, 1] + [0, 0.8, 0]  # sparse cloud far above
    means = np.vstack([dense, haze])
    opacity = np.r_[np.full(5000, 0.3), np.full(60, 0.04)]
    g = {"means": means, "opacities_logit": np.log(opacity / (1 - opacity))}
    keep = floater_mask(g)
    assert keep[:5000].all()                      # never touches the object
    assert (~keep[5000:]).sum() >= 55             # removes (almost) all haze
    opaque = dict(g, opacities_logit=np.full(len(means), 3.0))
    assert floater_mask(opaque).all()             # isolated but opaque = real detail, kept
