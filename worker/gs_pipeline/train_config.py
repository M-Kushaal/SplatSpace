"""Training CLI arguments (kept free of torch imports so the CLI loads on CPU)."""

# Defaults per capture type. Objects: 15k steps reached ~90% of 30k quality in a third of the time.
# Scenes (rooms) have far more surface to fit, so they get the full 30k schedule, slightly smaller
# images (more frames) and a Gaussian cap that keeps a T4 (16 GB) from running out of memory.
# The first room (plain 3DGS) had holes on the blank ceiling and needle "fur" wherever few photos looked,
# so scenes also get: MCMC densification with scale/opacity regularisation, monocular depth priors
# (dense init + depth loss, from `gs_pipeline depth`), per-photo exposure compensation, pose refinement,
# anti-aliased rendering and a prune of Gaussians seen by fewer than 3 photos.
PROFILES = {
    "object": {"steps": 15000, "max_side": 1920, "cap_max": 0, "max_init_points": 300_000, "strategy": "default",
               "depth": "off", "appearance": "off", "pose_opt": "off", "antialiased": "off", "min_views": 0},
    "scene": {"steps": 30000, "max_side": 1600, "cap_max": 3_000_000, "max_init_points": 1_000_000,
              "strategy": "mcmc", "depth": "auto", "appearance": "on", "pose_opt": "on", "antialiased": "on",
              "min_views": 3},
}


def resolve_profile(args, manifest: dict) -> str:
    """Fill unset steps / max_side / cap_max from the profile (auto = from the manifest's capture_mode)."""
    name = args.profile
    if name == "auto":
        name = "scene" if manifest.get("capture_mode") == "scene" else "object"
    for key, value in PROFILES[name].items():
        if getattr(args, key) is None:
            setattr(args, key, value)
    return name


def add_train_args(p):
    p.add_argument("manifest")
    p.add_argument("--out", default="out", help="output folder (splat.ply, metrics.json, previews/)")
    p.add_argument("--profile", choices=["auto", "object", "scene"], default="auto",
                   help="defaults for steps/max-side/cap-max; auto picks from the manifest's capture_mode")
    p.add_argument("--steps", type=int, default=None, help="default: from the profile (object 15k, scene 30k)")
    p.add_argument("--max-side", type=int, default=None,
                   help="downscale images so the long side <= this (default: object 1920, scene 1600)")
    p.add_argument("--cap-max", type=int, default=None, help="stop densifying above this many Gaussians (0 = no cap)")
    p.add_argument("--sh-degree", type=int, default=3)
    p.add_argument("--test-every", type=int, default=8, help="hold out every Nth frame for PSNR (0 = none)")
    p.add_argument("--init-random", type=int, default=100_000, help="random points if no points3d.ply")
    p.add_argument("--max-init-points", type=int, default=None, help="default: object 300k, scene 1M")
    p.add_argument("--strategy", choices=["default", "mcmc"], default=None,
                   help="densification: default = 3DGS clone/split, mcmc = 3DGS-MCMC (default: object default, scene mcmc)")
    p.add_argument("--opacity-reg", type=float, default=0.01, help="MCMC only: penalty on mean opacity")
    p.add_argument("--scale-reg", type=float, default=0.01, help="MCMC only: penalty on mean scale (stops needles)")
    p.add_argument("--depth", choices=["auto", "off"], default=None,
                   help="auto: use depth priors from --depth-dir if `gs_pipeline depth` was run (default: scene auto)")
    p.add_argument("--depth-dir", default=None, help="output of `gs_pipeline depth` (default: <bundle>/depth)")
    p.add_argument("--depth-lambda", type=float, default=0.2, help="depth loss weight at the start")
    p.add_argument("--depth-lambda-final", type=float, default=0.01, help="depth loss weight at the end")
    p.add_argument("--appearance", choices=["on", "off"], default=None,
                   help="learn a per-photo colour transform (phone auto-exposure / white balance)")
    p.add_argument("--pose-opt", choices=["on", "off"], default=None, help="refine training camera poses")
    p.add_argument("--antialiased", choices=["on", "off"], default=None, help="anti-aliased rasterisation")
    p.add_argument("--min-views", type=int, default=None,
                   help="after training drop Gaussians seen (unoccluded) by fewer training photos (0 = off)")
    p.add_argument("--ssim-lambda", type=float, default=0.2)
    p.add_argument("--log-every", type=int, default=500)
    p.add_argument("--seed", type=int, default=42)
    p.add_argument("--focus", choices=["auto", "off"], default="auto",
                   help="auto: only densify inside a sphere around the object (needs object_bounds); off: everywhere")
    p.add_argument("--focus-scale", type=float, default=2.0, help="focus sphere = this x object bounding sphere")
    p.add_argument("--dry-run", action="store_true", help="CPU smoke test with a fake rasterizer (no CUDA)")
    return p
