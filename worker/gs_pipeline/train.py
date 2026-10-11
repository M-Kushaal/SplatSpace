"""3D Gaussian Splatting trainer on gsplat. Manifest in -> splat.ply + metrics.json + previews out.

Follows gsplat's simple_trainer defaults (learning rates, DefaultStrategy or MCMCStrategy densification,
L1 + SSIM loss, SH degree ramp-up). Rooms (the scene profile) add a monocular depth loss, per-photo
exposure compensation, camera pose refinement and a min-views prune; see train_config.PROFILES.
`--dry-run` swaps the CUDA rasterizer for a tiny torch stand-in so the whole loop (data, init,
densification wiring, export) can be checked on a CPU before spending GPU time.
"""

import json
import time
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F

from .cameras import scale_intrinsics, viewmat_from_c2w_gl
from .manifest import load_frames, points_path
from .ply_io import read_points_ply, write_gaussians_ply

SH_C0 = 0.28209479177387814


def rgb_to_sh(rgb: torch.Tensor) -> torch.Tensor:
    return (rgb - 0.5) / SH_C0


def load_images(frames, max_side: int):
    """uint8 HxWx3 tensors + intrinsics rescaled to the loaded size."""
    from PIL import Image

    images, Ks = [], []
    for fr in frames:
        with Image.open(fr.image_path) as im:
            im = im.convert("RGB")
            if im.size != (fr.width, fr.height):
                raise ValueError(f"{fr.image_path.name}: image is {im.size}, manifest says {(fr.width, fr.height)}")
            s = min(1.0, max_side / max(im.size))
            if s < 1.0:
                im = im.resize((round(im.width * s), round(im.height * s)), Image.LANCZOS)
            Ks.append(scale_intrinsics(fr.K, im.width / fr.width, im.height / fr.height))
            images.append(torch.from_numpy(np.asarray(im).copy()))
    return images, Ks


def init_points(manifest_path, manifest, centers, scene_scale, args, rng, dense_ply=None):
    """Initial Gaussian centers + colors: points3d.ply (+ depth-prior points from dense_ply) if present,
    else random in the object bounds."""
    pp = points_path(manifest_path, manifest)
    if pp is not None and pp.exists():
        xyz, rgb = read_points_ply(pp)
        if rgb is None:
            rgb = np.full((len(xyz), 3), 128, np.uint8)
        src = f"points3d.ply ({len(xyz)} pts)"
        if dense_ply is not None and Path(dense_ply).exists():
            dxyz, drgb = read_points_ply(dense_ply)
            drgb = drgb if drgb is not None else np.full((len(dxyz), 3), 128, np.uint8)
            xyz, rgb = np.concatenate([xyz, dxyz]), np.concatenate([rgb, drgb])
            src += f" + depth prior ({len(dxyz)} pts)"
        if len(xyz) > args.max_init_points:
            keep = rng.choice(len(xyz), args.max_init_points, replace=False)
            xyz, rgb = xyz[keep], rgb[keep]
            src += f", subsampled to {len(xyz)}"
        return xyz.astype(np.float32), rgb.astype(np.float32) / 255.0, src
    if "object_bounds" in manifest:
        b = manifest["object_bounds"]
        center = np.asarray(b["center"], float) + [0.0, 0.5 * b.get("height", 0.0), 0.0]
        extent = 1.5 * float(np.hypot(b["radius"], 0.5 * b.get("height", 0.0)))
    else:
        center, extent = centers.mean(0), scene_scale
    xyz = center + (rng.random((args.init_random, 3)) * 2 - 1) * extent
    return xyz.astype(np.float32), rng.random((args.init_random, 3)).astype(np.float32), f"random ({args.init_random} pts)"


def knn_scale_log(xyz: np.ndarray) -> np.ndarray:
    """log of RMS distance to the 3 nearest neighbours, as the initial isotropic scale."""
    from scipy.spatial import cKDTree

    d, _ = cKDTree(xyz).query(xyz, k=4)
    dist = np.sqrt((d[:, 1:] ** 2).mean(1)).clip(1e-7)
    return np.repeat(np.log(dist)[:, None], 3, axis=1).astype(np.float32)


def _gaussian_kernel(size=11, sigma=1.5, device="cpu"):
    x = torch.arange(size, device=device, dtype=torch.float32) - size // 2
    g = torch.exp(-x ** 2 / (2 * sigma ** 2))
    g = g / g.sum()
    return (g[:, None] * g[None, :]).expand(3, 1, size, size).contiguous()


def ssim(a: torch.Tensor, b: torch.Tensor, kernel: torch.Tensor, mask: torch.Tensor | None = None) -> torch.Tensor:
    """Mean SSIM of two (1,3,H,W) images in [0,1], optionally only over mask (H,W)."""
    pad = kernel.shape[-1] // 2
    conv = lambda x: F.conv2d(x, kernel, padding=pad, groups=3)  # noqa: E731
    mu_a, mu_b = conv(a), conv(b)
    s_aa, s_bb, s_ab = conv(a * a) - mu_a ** 2, conv(b * b) - mu_b ** 2, conv(a * b) - mu_a * mu_b
    c1, c2 = 0.01 ** 2, 0.03 ** 2
    smap = ((2 * mu_a * mu_b + c1) * (2 * s_ab + c2)) / ((mu_a ** 2 + mu_b ** 2 + c1) * (s_aa + s_bb + c2))
    if mask is None:
        return smap.mean()
    return (smap * mask).sum() / (mask.sum() * 3).clamp(min=1)


def masked_mean(x: torch.Tensor, mask: torch.Tensor | None) -> torch.Tensor:
    """Mean of an (H,W,3) tensor over mask (H,W); plain mean without a mask."""
    if mask is None:
        return x.mean()
    return (x * mask[..., None]).sum() / (mask.sum() * 3).clamp(min=1)


def region_masks(frames, Ks, sizes, center: np.ndarray, radius: float, device) -> list[torch.Tensor]:
    """Per image: pixels covered by the region sphere's silhouette (slightly enlarged, conservative)."""
    masks = []
    for fr, K, (h, w) in zip(frames, Ks, sizes):
        vm = viewmat_from_c2w_gl(fr.c2w_gl)
        pc = vm[:3, :3] @ center + vm[:3, 3]
        d = float(np.linalg.norm(pc))
        if d <= radius * 1.05 or pc[2] <= 0:
            masks.append(torch.ones(h, w, device=device))
            continue
        u, v = K[0, 0] * pc[0] / pc[2] + K[0, 2], K[1, 1] * pc[1] / pc[2] + K[1, 2]
        r_px = 1.15 * max(K[0, 0], K[1, 1]) * np.tan(np.arcsin(min(radius / d, 0.999))) * d / pc[2]
        yy, xx = torch.meshgrid(torch.arange(h, device=device), torch.arange(w, device=device), indexing="ij")
        masks.append((((xx + 0.5 - u) ** 2 + (yy + 0.5 - v) ** 2) <= r_px ** 2).float())
    return masks


def fake_rasterization(means, quats, scales, opacities, colors, viewmats, Ks, width, height, sh_degree=None,
                       packed=False, render_mode="RGB", **_):
    """CPU stand-in for gsplat.rasterization with matching output shapes (dry runs only)."""
    C, N = viewmats.shape[0], means.shape[0]
    cam = torch.einsum("cij,nj->cni", viewmats[:, :3, :3], means) + viewmats[:, None, :3, 3]
    z = cam[..., 2:].clamp(min=1e-3)
    means2d = cam[..., :2] / z * torch.stack([Ks[:, 0, 0], Ks[:, 1, 1]], -1)[:, None] + Ks[:, None, :2, 2]
    radii = torch.where(cam[..., 2:] > 0, 3, 0).expand(C, N, 2).int()
    color = (colors[:, 0] * SH_C0 + 0.5).mean(0) * opacities.mean() * scales.mean() * quats.norm(dim=-1).mean()
    render = color.expand(C, height, width, 3) + 1e-6 * means2d.tanh().sum()  # bounded: z ~ 0 projects to ~1e5 px
    if render_mode == "RGB+ED":
        far = cam[..., 2].amax(1).clamp(min=0.1)  # a wall behind everything: nothing is occluded
        render = torch.cat([render, far[:, None, None, None].expand(C, height, width, 1)], -1)
    info = {"means2d": means2d, "radii": radii, "width": width, "height": height, "n_cameras": C,
            "gaussian_ids": None}
    return render, torch.ones(C, height, width, 1), info


def focus_strategy_class(base):
    """DefaultStrategy that only clones/splits Gaussians inside the object region, and/or stops
    growing once `cap_max` Gaussians exist (rooms on a 16 GB T4).

    Background Gaussians are kept (they must explain the room seen behind the object; removing them
    made the trainer fake the background with floaters around the object) but stay coarse.
    """
    from dataclasses import dataclass

    @dataclass
    class FocusStrategy(base):
        region_center: torch.Tensor | None = None
        region_radius: float = 0.0
        cap_max: int = 0

        def _grow_gs(self, params, optimizers, state, step):
            if self.cap_max and len(params["means"]) >= self.cap_max:
                return 0, 0  # (n_dupli, n_split): pruning and opacity resets still run
            if self.region_center is not None:
                with torch.no_grad():
                    outside = (params["means"] - self.region_center).norm(dim=-1) > self.region_radius
                    state["grad2d"][outside] = 0.0
            return super()._grow_gs(params, optimizers, state, step)

    return FocusStrategy


def dry_mcmc_class(base):
    """MCMCStrategy whose relocation / noise steps (CUDA kernels) are skipped, for CPU dry runs."""

    class DryMCMC(base):
        def step_post_backward(self, params, optimizers, state, step, info, lr):
            pass

    return DryMCMC


def se3_exp(delta: torch.Tensor) -> torch.Tensor:
    """(6,) axis-angle + translation -> 4x4 rigid transform (differentiable, safe at zero)."""
    w, t = delta[:3], delta[3:]
    theta = torch.sqrt((w * w).sum() + 1e-12)
    zero = torch.zeros((), dtype=delta.dtype, device=delta.device)
    Kx = torch.stack([torch.stack([zero, -w[2], w[1]]), torch.stack([w[2], zero, -w[0]]),
                      torch.stack([-w[1], w[0], zero])])
    R = torch.eye(3, dtype=delta.dtype, device=delta.device) + torch.sin(theta) / theta * Kx \
        + (1 - torch.cos(theta)) / theta ** 2 * (Kx @ Kx)
    bottom = torch.tensor([[0.0, 0.0, 0.0, 1.0]], dtype=delta.dtype, device=delta.device)
    return torch.cat([torch.cat([R, t[:, None]], 1), bottom], 0)


def depth_loss_weight(step: int, steps: int, start: float, final: float) -> float:
    """Exponential decay start -> final: strong early (shape), weak late (photo detail wins)."""
    if start <= 0:
        return 0.0
    return float(start * (max(final, 1e-12) / start) ** (step / max(steps - 1, 1)))


def visible_in_view(means: torch.Tensor, viewmat: torch.Tensor, K: torch.Tensor, radii: torch.Tensor,
                    depth: torch.Tensor, slack: float = 0.05) -> torch.Tensor:
    """Bool (N,): Gaussians drawn in this view (radii > 0) whose centre is not hidden behind the rendered
    surface (centre depth <= rendered depth x (1 + slack))."""
    h, w = depth.shape
    cam = means @ viewmat[:3, :3].T + viewmat[:3, 3]
    z = cam[:, 2]
    u = K[0, 0] * cam[:, 0] / z.clamp(min=1e-6) + K[0, 2]
    v = K[1, 1] * cam[:, 1] / z.clamp(min=1e-6) + K[1, 2]
    drawn = (radii.reshape(len(means), -1) > 0).any(-1)
    inside = drawn & (z > 0) & (u >= 0) & (u < w) & (v >= 0) & (v < h)
    ui, vi = u.clamp(0, w - 1).long(), v.clamp(0, h - 1).long()
    return inside & (z <= depth[vi, ui] * (1 + slack))


def color_matched_psnr(img: torch.Tensor, gt: torch.Tensor) -> float:
    """PSNR after the best affine colour map (3x3 + bias, least squares) from render to photo. With per-photo
    exposure compensation the held-out photos' own exposure is unknown, so this separates "wrong colours"
    (exposure / white balance) from "wrong content" (geometry, holes, blur)."""
    x = torch.cat([img.reshape(-1, 3), torch.ones_like(img.reshape(-1, 3)[:, :1])], 1).double()
    A = torch.linalg.lstsq(x, gt.reshape(-1, 3).double()).solution
    fit = (x @ A).float().reshape(img.shape).clamp(0, 1)
    return float(-10 * torch.log10(F.mse_loss(fit, gt)))


def train(args) -> dict:
    import gsplat
    from gsplat import DefaultStrategy, MCMCStrategy

    rasterize = fake_rasterization if args.dry_run else gsplat.rasterization
    device = "cpu" if args.dry_run else "cuda"
    if not args.dry_run and not torch.cuda.is_available():
        raise RuntimeError("CUDA GPU required (use --dry-run for a CPU smoke test)")
    torch.manual_seed(args.seed)
    rng = np.random.default_rng(args.seed)
    t0 = time.time()

    out = Path(args.out)
    (out / "previews").mkdir(parents=True, exist_ok=True)
    frames, manifest = load_frames(args.manifest)
    from .train_config import resolve_profile

    profile = resolve_profile(args, manifest)
    images, Ks = load_images(frames, args.max_side)
    n = len(frames)
    test_ids = list(range(0, n, args.test_every)) if args.test_every > 0 and n >= 2 * args.test_every else []
    train_ids = [i for i in range(n) if i not in set(test_ids)]
    images = [im.to(device) for im in images]
    viewmats = torch.tensor(np.stack([viewmat_from_c2w_gl(f.c2w_gl) for f in frames]), dtype=torch.float32,
                            device=device)
    Ks_t = torch.tensor(np.stack(Ks), dtype=torch.float32, device=device)
    centers = np.stack([f.c2w_gl[:3, 3] for f in frames])
    scene_scale = float(np.linalg.norm(centers - centers.mean(0), axis=1).max() * 1.1)

    # Monocular depth priors (scenes): aligned inverse depth per training photo + dense init points.
    from . import depth as depth_mod

    depth_dir = Path(args.depth_dir) if args.depth_dir else Path(args.manifest).parent / "depth"
    use_depth = args.depth != "off" and depth_mod.has_depth(depth_dir)
    if args.depth != "off" and not use_depth:
        print(f"[train] WARNING: no depth priors in {depth_dir} (run `gs_pipeline depth` first); "
              "blank walls and ceilings may get holes", flush=True)
    priors = [None] * n
    if use_depth:
        train_set = set(train_ids)
        for i, d in enumerate(depth_mod.load_aligned(depth_dir, frames)):
            if d is not None and i in train_set:
                inv = np.where(d > 0, 1.0 / np.maximum(d, 1e-6), 0.0)
                priors[i] = torch.tensor(inv, dtype=torch.float32, device=device)
    n_priors = sum(p is not None for p in priors)

    mcmc = args.strategy == "mcmc"
    xyz, rgb, init_src = init_points(args.manifest, manifest, centers, scene_scale, args, rng,
                                     dense_ply=depth_dir / depth_mod.DENSE_FILE if use_depth else None)

    # Object-focused training: only a sphere around the (point-refined) object is reconstructed and
    # supervised. On capture #2 the whole-scene run spent ~94% of its Gaussians (and time) on the room.
    region = None
    if args.focus == "auto" and not mcmc:
        from .validate import object_bounds

        b = object_bounds(args.manifest, manifest)
        if b is not None:
            region = (b.middle.astype(np.float32), float(args.focus_scale * b.sphere_radius))
    masks = [None] * n  # object-region masks, used only to report object PSNR
    if region is not None:
        r_center, r_radius = region
        inside = int((np.linalg.norm(xyz - r_center, axis=1) < r_radius).sum())
        init_src += f", densify only inside r={r_radius:.3f} m ({inside} of {len(xyz)} init pts inside)"
        # Only the held-out views need masks (object PSNR); full-res masks for every frame cost ~8 MB each.
        for i, m in zip(test_ids, region_masks([frames[i] for i in test_ids], [Ks[i] for i in test_ids],
                                               [images[i].shape[:2] for i in test_ids], r_center, r_radius, device)):
            masks[i] = m
    N = len(xyz)
    deg = args.sh_degree
    # MCMC starts from smaller, more opaque Gaussians (gsplat simple_trainer's MCMC settings).
    scales0 = knn_scale_log(xyz) + (np.float32(np.log(0.1)) if mcmc else np.float32(0.0))
    params = torch.nn.ParameterDict({
        "means": torch.nn.Parameter(torch.from_numpy(xyz)),
        "scales": torch.nn.Parameter(torch.from_numpy(scales0)),
        "quats": torch.nn.Parameter(torch.rand(N, 4)),
        "opacities": torch.nn.Parameter(torch.logit(torch.full((N,), 0.5 if mcmc else 0.1))),
        "sh0": torch.nn.Parameter(rgb_to_sh(torch.from_numpy(rgb))[:, None, :]),
        "shN": torch.nn.Parameter(torch.zeros(N, (deg + 1) ** 2 - 1, 3)),
    }).to(device)
    lrs = {"means": 1.6e-4 * scene_scale, "scales": 5e-3, "quats": 1e-3, "opacities": 5e-2,
           "sh0": 2.5e-3, "shN": 2.5e-3 / 20}
    optimizers = {k: torch.optim.Adam([{"params": params[k], "lr": lr, "name": k}], eps=1e-15)
                  for k, lr in lrs.items()}
    means_sched = torch.optim.lr_scheduler.ExponentialLR(optimizers["means"], gamma=0.01 ** (1.0 / max(args.steps, 1)))

    # Per-photo colour transform (3x3 + bias, identity at start): phones re-expose as you turn, which
    # otherwise shows up as blotchy walls. Used only in the loss; held-out views and the PLY never see it.
    extra_opts = []
    color_M = color_b = pose_delta = None
    if args.appearance == "on":
        color_M = torch.nn.Parameter(torch.eye(3, device=device).repeat(n, 1, 1))
        color_b = torch.nn.Parameter(torch.zeros(n, 3, device=device))
        extra_opts.append(torch.optim.Adam([color_M, color_b], lr=1e-3))
    # Pose refinement: a small rigid correction per training photo (held-out photos stay fixed).
    if args.pose_opt == "on":
        pose_delta = torch.nn.Parameter(torch.zeros(n, 6, device=device))
        extra_opts.append(torch.optim.Adam([pose_delta], lr=1e-5, weight_decay=1e-6))

    refine_stop = min(25_000 if mcmc else 15_000, int(args.steps * 0.85))
    if mcmc:
        cls = dry_mcmc_class(MCMCStrategy) if args.dry_run else MCMCStrategy
        strategy = cls(cap_max=args.cap_max or 1_000_000, refine_stop_iter=refine_stop)
        state = strategy.initialize_state()
    else:
        if region is not None or args.cap_max:
            strategy = focus_strategy_class(DefaultStrategy)(
                refine_stop_iter=refine_stop, cap_max=args.cap_max,
                region_center=None if region is None else torch.tensor(region[0], device=device),
                region_radius=0.0 if region is None else region[1])
        else:
            strategy = DefaultStrategy(refine_stop_iter=refine_stop)
        state = strategy.initialize_state(scene_scale=scene_scale)
    strategy.check_sanity(params, optimizers)
    kernel = _gaussian_kernel(device=device)
    raster_mode = "antialiased" if args.antialiased == "on" else "classic"
    train_mode = "RGB+ED" if n_priors else "RGB"

    print(f"[train] {n} frames ({len(train_ids)} train / {len(test_ids)} test), "
          f"{images[0].shape[1]}x{images[0].shape[0]} px, init {init_src}, scene_scale {scene_scale:.3f}, "
          f"device {device}, gsplat {gsplat.__version__}, profile {profile} ({args.steps} steps, "
          f"{args.strategy}, depth priors {n_priors}/{len(train_ids)}, appearance {args.appearance}, "
          f"pose-opt {args.pose_opt}, {raster_mode})", flush=True)

    def view_of(i, train_pose=False):
        if train_pose and pose_delta is not None:
            return se3_exp(pose_delta[i]) @ viewmats[i]
        return viewmats[i]

    def raster(vm, K, w, h, sh_deg, mode="RGB"):
        colors = torch.cat([params["sh0"], params["shN"]], 1)
        img, alpha, info = rasterize(
            means=params["means"], quats=params["quats"], scales=torch.exp(params["scales"]),
            opacities=torch.sigmoid(params["opacities"]), colors=colors, viewmats=vm[None], Ks=K[None],
            width=w, height=h, sh_degree=sh_deg, packed=False, render_mode=mode, rasterize_mode=raster_mode)
        return img[0], alpha[0, ..., 0], info

    def render(i, sh_deg, mode="RGB", train_pose=False):
        h, w = images[i].shape[:2]
        return raster(view_of(i, train_pose), Ks_t[i], w, h, sh_deg, mode)

    train_t = torch.tensor(train_ids, device=device)
    # The min-views prune runs inside training (not after it): the first room pruned 13% of the Gaussians
    # after the last step and lost held-out PSNR to the gaps; pruned this early, the rest adapt.
    prune_step = int(args.steps * 0.8) if args.min_views > 0 else -1
    prune = None
    perm, t_train = [], time.time()
    for step in range(args.steps):
        if not perm:
            perm = list(rng.permutation(train_ids))
        i = int(perm.pop())
        gt = images[i].float() / 255.0
        out_img, _, info = render(i, min(step // 1000, deg), train_mode, train_pose=True)
        strategy.step_pre_backward(params, optimizers, state, step, info)
        img = out_img[..., :3]
        if color_M is not None:
            img = img @ color_M[i].T + color_b[i]
        l1 = (img - gt).abs().mean()
        s = ssim(img.permute(2, 0, 1)[None], gt.permute(2, 0, 1)[None], kernel)
        loss = (1 - args.ssim_lambda) * l1 + args.ssim_lambda * (1 - s)
        if priors[i] is not None:
            inv = 1.0 / out_img[..., 3].clamp(min=1e-2)
            inv = F.interpolate(inv[None, None], size=priors[i].shape, mode="area")[0, 0]
            valid = priors[i] > 0
            lam = depth_loss_weight(step, args.steps, args.depth_lambda, args.depth_lambda_final)
            loss = loss + lam * (inv - priors[i])[valid].abs().mean()
        if mcmc:
            loss = loss + args.opacity_reg * torch.sigmoid(params["opacities"]).mean() \
                + args.scale_reg * torch.exp(params["scales"]).mean()
        if color_M is not None:
            loss = loss + 1e-3 * ((color_M[i] - torch.eye(3, device=device)) ** 2).sum() + 1e-3 * (color_b[i] ** 2).sum()
        loss.backward()
        for opt in list(optimizers.values()) + extra_opts:
            opt.step()
            opt.zero_grad(set_to_none=True)
        if color_M is not None:
            # Anchor: the mean per-photo transform stays identity, so the splat's colours match the average
            # photo. Unanchored, colour drifted into the transforms and held-out views (rendered without one)
            # lost ~3 dB on the first room.
            with torch.no_grad():
                color_M -= color_M[train_t].mean(0) - torch.eye(3, device=device)
                color_b -= color_b[train_t].mean(0)
        means_sched.step()
        if mcmc:
            strategy.step_post_backward(params, optimizers, state, step, info,
                                        lr=optimizers["means"].param_groups[0]["lr"])
        else:
            strategy.step_post_backward(params, optimizers, state, step, info, packed=False)
        if step == prune_step:
            with torch.no_grad():
                seen = torch.zeros(len(params["means"]), dtype=torch.int32, device=device)
                for j in train_ids:
                    o, _, inf = render(j, min(step // 1000, deg), "RGB+ED", train_pose=True)
                    seen += visible_in_view(params["means"], view_of(j, True), Ks_t[j], inf["radii"][0], o[..., 3]).int()
                drop = seen < args.min_views
            from gsplat.strategy.ops import remove

            n_before = len(drop)
            # MCMC's only state ("binoms") is not per-Gaussian; the default strategy's is
            remove(params, optimizers, {} if mcmc else state, drop)
            prune = {"min_views": args.min_views, "step": step, "removed": int(drop.sum()), "kept": n_before - int(drop.sum())}
            print(f"[train] min-views prune at step {step}: removed {prune['removed']} of {n_before} Gaussians", flush=True)
        if len(params["means"]) == 0:
            raise RuntimeError(f"all Gaussians were pruned at step {step}; poses or scene scale are likely wrong")
        if step % args.log_every == 0 or step == args.steps - 1:
            mem = torch.cuda.max_memory_allocated() / 1e9 if device == "cuda" else 0.0
            print(f"[train] step {step:6d}  loss {loss.item():.4f}  gaussians {len(params['means']):8d}  "
                  f"{time.time() - t_train:7.1f}s  peak mem {mem:.1f} GB", flush=True)
    train_seconds = time.time() - t_train

    # Evaluate on held-out views (or a couple of train views) and save side-by-side previews.
    psnrs, psnrs_obj, psnrs_cm = [], [], []
    preview_ids = test_ids or [0, n // 2]
    with torch.no_grad():
        for k, i in enumerate(preview_ids):
            img, _, _ = render(i, deg)
            img = img.clamp(0, 1)
            gt = images[i].float() / 255.0
            if test_ids:
                psnrs.append(float(-10 * torch.log10(F.mse_loss(img, gt))))
                psnrs_cm.append(color_matched_psnr(img, gt))
                if masks[i] is not None:
                    psnrs_obj.append(float(-10 * torch.log10(masked_mean((img - gt) ** 2, masks[i]))))
            if k < 4:
                from PIL import Image

                pair = torch.cat([gt, img], 1).mul(255).byte().cpu().numpy()
                Image.fromarray(pair).save(out / "previews" / f"view_{i:04d}_gt_vs_render.png")

    # Rooms: how much of the view from each capture spot sees through the splat (the quality KPI).
    holes = None
    from .scene import capture_mode, floor_height

    if capture_mode(manifest) == "scene":
        from .coverage import hole_report
        from .validate import load_points

        def render_np(vm, K, w, h):
            with torch.no_grad():
                img, alpha, _ = raster(torch.tensor(vm, dtype=torch.float32, device=device),
                                       torch.tensor(K, dtype=torch.float32, device=device), w, h, deg)
            return img.clamp(0, 1).cpu().numpy(), alpha.cpu().numpy()

        floor_y = floor_height(manifest, load_points(args.manifest, manifest), centers)
        holes = hole_report(render_np, manifest, floor_y, out / "previews", camera_centers=centers)
        print(f"[train] holes seen from the capture spots: {holes}", flush=True)

    ply_path = out / "splat.ply"
    with torch.no_grad():
        quats = F.normalize(params["quats"], dim=-1)
        write_gaussians_ply(ply_path, params["means"].cpu().numpy(), params["scales"].cpu().numpy(),
                            quats.cpu().numpy(), params["opacities"].cpu().numpy(),
                            params["sh0"][:, 0].cpu().numpy(), params["shN"].cpu().numpy())

    from .postprocess import postprocess

    object_stats = postprocess(ply_path, args.manifest, out / "splat_object.ply")

    pose_shift = None
    if pose_delta is not None and train_ids:
        d = pose_delta.detach()[train_ids]
        pose_shift = {"rot_deg_max": round(float(d[:, :3].norm(dim=-1).max()) * 180 / np.pi, 3),
                      "trans_cm_max": round(float(d[:, 3:].norm(dim=-1).max()) * 100, 2)}
    metrics = {
        "capture_mode": profile,
        "psnr_test_mean": float(np.mean(psnrs)) if psnrs else None,
        "psnr_test": psnrs,
        "psnr_test_color_matched_mean": float(np.mean(psnrs_cm)) if psnrs_cm else None,
        "psnr_object_mean": float(np.mean(psnrs_obj)) if psnrs_obj else None,
        "psnr_object": psnrs_obj,
        "focus_region": None if region is None else {"center": [round(float(c), 4) for c in region[0]],
                                                     "radius": round(region[1], 4)},
        "num_gaussians": len(params["means"]),
        "steps": args.steps,
        "strategy": args.strategy,
        "depth_priors": n_priors,
        "appearance": args.appearance,
        "pose_opt": pose_shift,
        "rasterize_mode": raster_mode,
        "min_views_prune": prune,
        "holes": holes,
        "train_seconds": round(train_seconds, 1),
        "total_seconds": round(time.time() - t0, 1),
        "frames_train": len(train_ids),
        "frames_test": len(test_ids),
        "image_size": [int(images[0].shape[1]), int(images[0].shape[0])],
        "init": init_src,
        "splat_ply_mb": round(ply_path.stat().st_size / 1e6, 1),
        "splat_object": object_stats,
        "gsplat": gsplat.__version__,
        "torch": torch.__version__,
        "device": torch.cuda.get_device_name() if device == "cuda" else "cpu (dry run)",
        "dry_run": bool(args.dry_run),
    }
    (out / "metrics.json").write_text(json.dumps(metrics, indent=2))
    print("[train] done:", json.dumps(metrics, indent=2), flush=True)
    return metrics


def holes_for_splat(splat_ply, manifest_path, out_dir) -> dict:
    """Hole report for an existing splat (e.g. an older run, to compare): needs CUDA. Use the trainer's
    splat.ply (training coordinates), not splat_object.ply (shifted so the floor is at y = 0)."""
    import gsplat

    from .coverage import hole_report
    from .ply_io import read_gaussians_ply
    from .scene import floor_height
    from .validate import load_points

    frames, manifest = load_frames(manifest_path)
    centers = np.stack([f.c2w_gl[:3, 3] for f in frames])
    g = read_gaussians_ply(splat_ply)
    dev = "cuda"
    t = {k: torch.tensor(v, dtype=torch.float32, device=dev) for k, v in g.items()}
    colors = torch.cat([t["sh0"][:, None, :], t["shN"]], 1)
    deg = int(round(np.sqrt(colors.shape[1]))) - 1

    def render_np(vm, K, w, h):
        with torch.no_grad():
            img, alpha, _ = gsplat.rasterization(
                means=t["means"], quats=F.normalize(t["quats_wxyz"], dim=-1), scales=torch.exp(t["scales_log"]),
                opacities=torch.sigmoid(t["opacities_logit"]), colors=colors,
                viewmats=torch.tensor(vm, dtype=torch.float32, device=dev)[None],
                Ks=torch.tensor(K, dtype=torch.float32, device=dev)[None], width=w, height=h, sh_degree=deg)
        return img[0].clamp(0, 1).cpu().numpy(), alpha[0, ..., 0].cpu().numpy()

    Path(out_dir).mkdir(parents=True, exist_ok=True)
    floor_y = floor_height(manifest, load_points(manifest_path, manifest), centers)
    return hole_report(render_np, manifest, floor_y, out_dir, camera_centers=centers)
