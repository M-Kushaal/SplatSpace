"""GPU worker: claim a job from the backend, download the capture bundle, validate, train, upload artifacts.

    python -m gs_worker --api http://127.0.0.1:8000 --token dev-worker-token [--once] [--dry-run]

Training runs as a subprocess (`python -m gs_pipeline train`), so a CUDA crash or OOM fails the
job instead of killing the worker. A heartbeat thread keeps the job lease alive meanwhile.
"""

import json
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
from collections import deque
from pathlib import Path

import httpx

from gs_pipeline.validate import validate_manifest

ARTIFACT_KINDS = {"splat.ply": "splat_ply", "splat_object.ply": "splat_object_ply", "metrics.json": "metrics",
                  "train.log": "log", "refine_report.json": "refine_report"}
HEARTBEAT_SECONDS = 30


class JobFailed(Exception):
    def __init__(self, message: str, retryable: bool):
        super().__init__(message)
        self.retryable = retryable


class Worker:
    def __init__(self, api: str, token: str, client: httpx.Client | None = None, worker_id: str | None = None,
                 work_dir: Path | None = None, dry_run: bool = False, steps: int | None = None,
                 refine: bool = True):
        self.api = api.rstrip("/")
        self.client = client or httpx.Client(timeout=httpx.Timeout(60.0, read=600.0))
        self.headers = {"Authorization": f"Bearer {token}"}
        self.worker_id = worker_id or f"{socket.gethostname()}-{int(time.time())}"
        self.work_dir = Path(work_dir or tempfile.gettempdir()) / "gs_worker"
        self.dry_run, self.steps, self.do_refine = dry_run, steps, refine

    # ---- API helpers

    def _post(self, path: str, body: dict, attempts: int = 3) -> httpx.Response:
        """POST with a short retry on connection errors (e.g. the server closing an idle keep-alive
        connection just as we reuse it, which surfaces as ReadError / WinError 10053)."""
        for attempt in range(attempts):
            try:
                r = self.client.post(f"{self.api}{path}", json=body, headers=self.headers)
                break
            except httpx.TransportError:
                if attempt == attempts - 1:
                    raise
                time.sleep(1 + attempt)
        r.raise_for_status()
        return r

    def heartbeat(self, job_id: str, stage: str, message: str = "") -> None:
        self._post(f"/worker/jobs/{job_id}/heartbeat", {"worker_id": self.worker_id, "stage": stage,
                                                         "message": message[-2000:]})

    # ---- main loop

    def run(self, once: bool = False, poll_seconds: float = 10.0) -> int:
        """Process jobs; with once=True, handle at most one job and return. Returns jobs processed."""
        done = 0
        while True:
            try:
                job = self._post("/worker/jobs/claim", {"worker_id": self.worker_id}).json()
            except httpx.HTTPError as exc:  # server restarting / offline: keep the worker alive
                print(f"[worker] cannot reach {self.api}: {exc}; retrying in {poll_seconds:.0f}s", flush=True)
                if once:
                    return done
                time.sleep(poll_seconds)
                continue
            if job:
                self.process(job)
                done += 1
            if once:
                return done
            if not job:
                time.sleep(poll_seconds)

    def process(self, job: dict) -> None:
        job_id = job["job_id"]
        job_dir = self.work_dir / job_id
        shutil.rmtree(job_dir, ignore_errors=True)
        print(f"[worker] job {job_id} (capture {job['capture_id']}, attempt {job['attempt']})", flush=True)
        try:
            bundle = job_dir / "bundle"
            self.heartbeat(job_id, "downloading", f"{len(job['inputs'])} files")
            self.download(job["inputs"], bundle)

            self.heartbeat(job_id, "validating")
            report = validate_manifest(bundle / "manifest.json")
            if not report.ok:
                raise JobFailed("validation failed:\n" + report.summary(), retryable=False)

            out = job_dir / "out"
            manifest, refine_report = bundle / "manifest.json", None
            if self.do_refine:
                manifest, refine_report = self.refine(job_id, manifest, job_dir / "refined")
            self.train(job_id, manifest, out, job.get("params") or {})
            if refine_report is not None:
                (out / "refine_report.json").write_text(json.dumps(refine_report, indent=2))

            self.heartbeat(job_id, "uploading")
            artifacts = self.upload_outputs(job_id, out)
            metrics = json.loads((out / "metrics.json").read_text())
            if refine_report is not None:
                metrics["refine"] = {k: v for k, v in refine_report.items() if k != "manifest"}
            self._post(f"/worker/jobs/{job_id}/complete",
                       {"worker_id": self.worker_id, "metrics": metrics, "artifacts": artifacts})
            print(f"[worker] job {job_id} done: {metrics.get('psnr_test_mean')} PSNR", flush=True)
        except JobFailed as exc:
            self._fail(job_id, str(exc), exc.retryable)
        except Exception as exc:  # noqa: BLE001 - report anything unexpected and keep the worker alive
            self._fail(job_id, f"{type(exc).__name__}: {exc}", retryable=True)
        finally:
            shutil.rmtree(job_dir, ignore_errors=True)

    def _fail(self, job_id: str, error: str, retryable: bool) -> None:
        print(f"[worker] job {job_id} failed (retryable={retryable}): {error[-500:]}", flush=True)
        try:
            self._post(f"/worker/jobs/{job_id}/fail", {"worker_id": self.worker_id, "error": error,
                                                       "retryable": retryable})
        except httpx.HTTPError as exc:
            print(f"[worker] could not report failure: {exc}", flush=True)

    # ---- steps

    def download(self, inputs: list[dict], dest: Path) -> None:
        for f in inputs:
            path = dest / f["path"]
            path.parent.mkdir(parents=True, exist_ok=True)
            with self.client.stream("GET", f["url"]) as r:
                r.raise_for_status()
                with open(path, "wb") as fh:
                    for chunk in r.iter_bytes(1 << 20):
                        fh.write(chunk)
            if path.stat().st_size != f["size"]:
                raise JobFailed(f"{f['path']}: downloaded {path.stat().st_size} bytes, expected {f['size']}", True)

    def refine(self, job_id: str, manifest: Path, out: Path) -> tuple[Path, dict]:
        """COLMAP pose refinement in a subprocess (pycolmap can segfault). Never fails the job: on any
        problem the AR poses are used and the report says so."""
        self.heartbeat(job_id, "refining poses")
        cmd = [sys.executable, "-u", "-m", "gs_pipeline", "refine", str(manifest), "--out", str(out)]
        proc = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
        report_file = out / "refine_report.json"
        if report_file.exists():
            report = json.loads(report_file.read_text())
        else:
            tail = (proc.stdout + proc.stderr).strip().splitlines()[-5:]
            report = {"fallback": True, "error": f"refine exited with code {proc.returncode}: {' | '.join(tail)}",
                      "manifest": str(manifest)}
        summary = {k: report[k] for k in ("registered", "reproj_px", "ar_drift_cm", "error") if k in report}
        print(f"[worker] refine: {'fallback to AR poses' if report.get('fallback') else 'ok'} {summary}", flush=True)
        return Path(report["manifest"]), report

    def train(self, job_id: str, manifest: Path, out: Path, params: dict) -> None:
        cmd = [sys.executable, "-u", "-m", "gs_pipeline", "train", str(manifest), "--out", str(out)]
        steps = self.steps or params.get("steps")
        if steps:
            cmd += ["--steps", str(steps)]
        for key in ("max_side", "sh_degree"):
            if key in params:
                cmd += [f"--{key.replace('_', '-')}", str(params[key])]
        if self.dry_run:
            cmd.append("--dry-run")

        out.mkdir(parents=True, exist_ok=True)
        tail: deque[str] = deque(maxlen=60)
        stop = threading.Event()

        def beat():
            while not stop.wait(HEARTBEAT_SECONDS):
                try:
                    self.heartbeat(job_id, "training", tail[-1] if tail else "")
                except httpx.HTTPError:
                    pass  # a missed beat is fine; the lease covers several intervals

        self.heartbeat(job_id, "training", "starting")
        hb = threading.Thread(target=beat, daemon=True)
        hb.start()
        try:
            with open(out / "train.log", "w", encoding="utf-8") as log, subprocess.Popen(
                    cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                    errors="replace") as proc:
                for line in proc.stdout:
                    log.write(line)
                    tail.append(line.rstrip())
                    print(f"  | {line.rstrip()}", flush=True)
                rc = proc.wait()
        finally:
            stop.set()
            hb.join(timeout=5)
        if rc != 0:
            raise JobFailed(f"trainer exited with code {rc}:\n" + "\n".join(tail), retryable=True)

    def upload_outputs(self, job_id: str, out: Path) -> list[dict]:
        files = {name: kind for name, kind in ARTIFACT_KINDS.items() if (out / name).exists()}
        files |= {f"previews/{p.name}": "preview_png" for p in sorted((out / "previews").glob("*.png"))}
        if "splat.ply" not in files:
            raise JobFailed("trainer produced no splat.ply", retryable=True)
        urls = self._post(f"/worker/jobs/{job_id}/upload-urls", {"names": list(files)}).json()
        for name in files:
            with open(out / name, "rb") as fh:
                self.client.put(urls[name], content=fh.read()).raise_for_status()
        return [{"name": n, "kind": k} for n, k in files.items()]
