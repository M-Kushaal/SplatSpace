import argparse
import os
import sys

from .runner import Worker


def main(argv=None) -> int:
    p = argparse.ArgumentParser(prog="gs_worker")
    p.add_argument("--api", default=os.environ.get("GS_API_URL", "http://127.0.0.1:8000"))
    p.add_argument("--token", default=os.environ.get("GS_WORKER_TOKEN", "dev-worker-token"))
    p.add_argument("--once", action="store_true", help="process at most one job, then exit")
    p.add_argument("--dry-run", action="store_true", help="CPU fake rasterizer (pipeline test, no GPU)")
    p.add_argument("--steps", type=int, default=None, help="override training steps")
    p.add_argument("--poll", type=float, default=10.0)
    p.add_argument("--no-refine", action="store_true", help="train directly on the AR poses")
    args = p.parse_args(argv)
    n = Worker(args.api, args.token, dry_run=args.dry_run, steps=args.steps, refine=not args.no_refine).run(once=args.once, poll_seconds=args.poll)
    print(f"[worker] processed {n} job(s)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
