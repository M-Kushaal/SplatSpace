"""CLI: python -m gs_pipeline {convert-colmap,validate,plot,train} ..."""

import argparse
import sys


def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="gs_pipeline")
    sub = p.add_subparsers(dest="cmd", required=True)

    c = sub.add_parser("convert-colmap", help="COLMAP model -> manifest.json")
    c.add_argument("scene_dir")
    c.add_argument("--images", default="images", help="image subfolder, e.g. images_4")
    c.add_argument("--sparse", default="sparse/0")

    v = sub.add_parser("validate", help="CPU sanity checks on a capture bundle")
    v.add_argument("manifest")
    v.add_argument("--no-files", action="store_true", help="skip image existence/size checks")

    pl = sub.add_parser("plot", help="camera frustum plot -> PNG")
    pl.add_argument("manifest")
    pl.add_argument("--out", default="cameras.png")

    rf = sub.add_parser("refine", help="COLMAP pose/lens refinement aligned to the AR world (CPU)")
    rf.add_argument("manifest")
    rf.add_argument("--out", required=True, help="output folder for the refined bundle")
    rf.add_argument("--threads", type=int, default=2)

    ra = sub.add_parser("realign", help="redo gravity/scale of a refined bundle whose AR positions jumped")
    ra.add_argument("refined_manifest")
    ra.add_argument("raw_manifest", help="the phone's original manifest.json (AR poses + timestamps)")

    pc = sub.add_parser("posecheck", help="epipolar consistency of poses vs photos, in px (CPU, OpenCV)")
    pc.add_argument("manifest")

    ov = sub.add_parser("overlay", help="draw AR points + object bounds onto photos (pose/orientation check)")
    ov.add_argument("manifest")
    ov.add_argument("--out", default="overlay.png")
    ov.add_argument("--frames", type=int, default=6)

    pp = sub.add_parser("postprocess", help="upright + crop a trained splat to the object (CPU)")
    pp.add_argument("splat_ply")
    pp.add_argument("manifest")
    pp.add_argument("--out", default="splat_object.ply")
    pp.add_argument("--radius", type=float, default=None, help="crop radius in scene units (default: auto)")
    pp.add_argument("--min-opacity", type=float, default=0.02)

    dp = sub.add_parser("depth", help="monocular depth priors for room captures (GPU recommended)")
    dp.add_argument("manifest")
    dp.add_argument("--out", default=None, help="output folder (default: <bundle>/depth)")
    dp.add_argument("--model", default=None, help="HF depth model (default: Depth-Anything-V2-Large)")

    hl = sub.add_parser("holes", help="hole report of an existing splat.ply from the capture spots (needs CUDA)")
    hl.add_argument("splat_ply", help="the trainer's splat.ply (not splat_object.ply)")
    hl.add_argument("manifest")
    hl.add_argument("--out", default="holes")

    t = sub.add_parser("train", help="train a Gaussian splat (needs CUDA)")
    from .train_config import add_train_args

    add_train_args(t)

    args = p.parse_args(argv)
    if args.cmd == "convert-colmap":
        from .colmap_io import convert_colmap

        print(convert_colmap(args.scene_dir, args.images, args.sparse))
    elif args.cmd == "validate":
        from .validate import validate_manifest

        rep = validate_manifest(args.manifest, check_files=not args.no_files)
        print(rep.summary())
        return 0 if rep.ok else 1
    elif args.cmd == "plot":
        from .visualize import plot_cameras

        print(plot_cameras(args.manifest, args.out))
    elif args.cmd == "refine":
        import json

        from .refine import refine

        report = refine(args.manifest, args.out, num_threads=args.threads)
        print(json.dumps(report, indent=2))
    elif args.cmd == "realign":
        import json

        from .refine import realign_bundle

        print(json.dumps(realign_bundle(args.refined_manifest, args.raw_manifest), indent=2))
    elif args.cmd == "posecheck":
        import json

        from .posecheck import posecheck

        print(json.dumps(posecheck(args.manifest), indent=2))
    elif args.cmd == "overlay":
        from .overlay import overlay

        print(overlay(args.manifest, args.out, args.frames))
    elif args.cmd == "postprocess":
        from .postprocess import postprocess

        print(postprocess(args.splat_ply, args.manifest, args.out, args.radius, args.min_opacity))
    elif args.cmd == "depth":
        import json

        from .depth import MODEL, compute_depth

        print(json.dumps(compute_depth(args.manifest, args.out, args.model or MODEL), indent=2))
    elif args.cmd == "holes":
        import json

        from .train import holes_for_splat

        print(json.dumps(holes_for_splat(args.splat_ply, args.manifest, args.out), indent=2))
    elif args.cmd == "train":
        from .train import train

        train(args)
    return 0


if __name__ == "__main__":
    sys.exit(main())
