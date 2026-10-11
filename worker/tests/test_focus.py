import numpy as np
import pytest

torch = pytest.importorskip("torch")

from gs_pipeline.cameras import intrinsics_matrix  # noqa: E402
from gs_pipeline.manifest import CameraFrame  # noqa: E402
from gs_pipeline.train import masked_mean, region_masks  # noqa: E402
from conftest import look_at_c2w_gl  # noqa: E402


def _frame(eye):
    return CameraFrame(image_path=None, K=intrinsics_matrix(100, 100, 50, 40), width=100, height=80,
                       c2w_gl=look_at_c2w_gl(eye, [0, 0, 0]), index=0)


def test_region_mask_is_a_centered_disc_and_full_when_inside():
    far, inside = _frame([0, 0, 2.0]), _frame([0, 0, 0.1])
    m_far, m_in = region_masks([far, inside], [far.K, inside.K], [(80, 100), (80, 100)],
                               np.zeros(3), 0.5, "cpu")
    assert m_far[40, 50] == 1 and m_far[0, 0] == 0 and m_far[40, 99] == 0
    expected_r = 1.15 * 100 * np.tan(np.arcsin(0.25))  # ~29.7 px
    assert m_far[40, 50 + int(expected_r) - 2] == 1 and m_far[40, 50 + int(expected_r) + 3] == 0
    assert m_in.min() == 1


def test_masked_mean_ignores_outside_pixels():
    x = torch.zeros(4, 4, 3)
    x[0, 0] = 9.0
    mask = torch.ones(4, 4)
    mask[0, 0] = 0
    assert masked_mean(x, mask) == 0 and masked_mean(x, None) > 0


def test_focus_strategy_only_densifies_inside_region():
    from gsplat import DefaultStrategy

    from gs_pipeline.train import focus_strategy_class

    # Gaussian 0 at the centre of the region, Gaussian 1 far outside; both have a high view-space gradient.
    params = torch.nn.ParameterDict({
        "means": torch.nn.Parameter(torch.tensor([[0.0, 0.0, 0.0], [5.0, 0.0, 0.0]])),
        "scales": torch.nn.Parameter(torch.log(torch.full((2, 3), 0.001))),  # small -> duplicate
        "quats": torch.nn.Parameter(torch.tensor([[1.0, 0, 0, 0]] * 2)),
        "opacities": torch.nn.Parameter(torch.zeros(2)),
        "sh0": torch.nn.Parameter(torch.zeros(2, 1, 3)),
        "shN": torch.nn.Parameter(torch.zeros(2, 15, 3)),
    })
    optimizers = {k: torch.optim.Adam([{"params": params[k], "lr": 1e-3, "name": k}]) for k in params}
    strategy = focus_strategy_class(DefaultStrategy)(region_center=torch.zeros(3), region_radius=1.0)
    state = strategy.initialize_state(scene_scale=1.0)
    state["grad2d"] = torch.tensor([1.0, 1.0])
    state["count"] = torch.tensor([1.0, 1.0])
    state["radii"] = None
    n_dupli, n_split = strategy._grow_gs(params, optimizers, state, step=1000)
    assert n_dupli + n_split == 1
    means = params["means"].detach()
    assert (means.norm(dim=-1) > 1.0).sum() == 1  # the outside Gaussian was not cloned
    assert len(means) == 3
