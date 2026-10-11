"""HTTP routes: auth, captures (phone app), worker queue (GPU workers), signed local file transfer."""

import json
import shutil
import tempfile
from datetime import timedelta
from pathlib import Path

from fastapi import APIRouter, Depends, HTTPException, Request, Response
from fastapi.responses import FileResponse
from sqlalchemy import or_, select, update
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import Session

from . import schemas as S
from .config import Settings
from .db import utcnow
from .deps import (create_token, current_user, get_db, get_settings, get_storage, hash_password, require_worker,
                   verify_password)
from .models import Artifact, Capture, Job, User
from .storage import LocalStorage, artifact_key, capture_key

auth = APIRouter(prefix="/auth", tags=["auth"])
captures = APIRouter(prefix="/captures", tags=["captures"])
worker = APIRouter(prefix="/worker", tags=["worker"], dependencies=[Depends(require_worker)])
files = APIRouter(tags=["files"])


# ---------------------------------------------------------------- auth

@auth.post("/register", response_model=S.TokenOut, status_code=201)
def register(body: S.Credentials, db: Session = Depends(get_db), settings: Settings = Depends(get_settings)):
    user = User(email=body.email.lower(), password_hash=hash_password(body.password))
    db.add(user)
    try:
        db.commit()
    except IntegrityError:
        raise HTTPException(409, "email already registered")
    return S.TokenOut(access_token=create_token(user.id, settings))


@auth.post("/login", response_model=S.TokenOut)
def login(body: S.Credentials, db: Session = Depends(get_db), settings: Settings = Depends(get_settings)):
    user = db.scalar(select(User).where(User.email == body.email.lower()))
    if user is None or not verify_password(user.password_hash, body.password):
        raise HTTPException(401, "wrong email or password")
    return S.TokenOut(access_token=create_token(user.id, settings))


# ---------------------------------------------------------------- captures

def _own_capture(db: Session, user: User, capture_id: str) -> Capture:
    cap = db.get(Capture, capture_id)
    if cap is None or cap.user_id != user.id:
        raise HTTPException(404, "capture not found")
    return cap


def _job_out(job: Job, storage) -> S.JobOut:
    return S.JobOut(
        id=job.id, pipeline=job.pipeline, status=job.status, stage=job.stage, message=job.message,
        attempts=job.attempts, metrics=job.metrics, error=job.error, created_at=job.created_at,
        finished_at=job.finished_at,
        artifacts=[S.ArtifactOut(kind=a.kind, name=a.name, bytes=a.bytes, url=storage.download_url(a.key))
                   for a in job.artifacts],
    )


def _capture_out(cap: Capture, storage) -> S.CaptureOut:
    return S.CaptureOut(id=cap.id, title=cap.title, status=cap.status, frame_count=cap.frame_count,
                        created_at=cap.created_at, validation=cap.validation,
                        job=_job_out(cap.jobs[-1], storage) if cap.jobs else None)


@captures.post("", response_model=S.CaptureCreated, status_code=201)
def create_capture(body: S.CaptureCreate, user: User = Depends(current_user), db: Session = Depends(get_db),
                   storage=Depends(get_storage), settings: Settings = Depends(get_settings)):
    paths = [f.path for f in body.files]
    if "manifest.json" not in paths:
        raise HTTPException(422, "files must include manifest.json")
    if len(set(paths)) != len(paths):
        raise HTTPException(422, "duplicate file paths")
    if len(paths) > settings.max_files_per_capture:
        raise HTTPException(422, f"too many files (max {settings.max_files_per_capture})")
    too_big = [f.path for f in body.files if f.size > settings.max_file_bytes]
    if too_big:
        raise HTTPException(422, f"files over {settings.max_file_bytes} bytes: {too_big[:5]}")
    cap = Capture(user_id=user.id, title=body.title, files=[f.model_dump() for f in body.files])
    db.add(cap)
    db.commit()
    return S.CaptureCreated(capture_id=cap.id, uploads=[
        S.UploadTarget(path=p, url=storage.upload_url(capture_key(cap.id, p))) for p in paths])


@captures.post("/{capture_id}/complete", response_model=S.CaptureOut)
def complete_capture(capture_id: str, user: User = Depends(current_user), db: Session = Depends(get_db),
                     storage=Depends(get_storage)):
    """All files uploaded: verify them, run the bundle validator, then queue training."""
    from gs_pipeline.validate import validate_manifest

    cap = _own_capture(db, user, capture_id)
    if cap.status not in ("created", "invalid"):
        raise HTTPException(409, f"capture is already {cap.status}")

    declared = {f["path"]: f["size"] for f in cap.files}
    missing = [p for p, size in declared.items() if storage.size(capture_key(cap.id, p)) != size]
    if missing:
        raise HTTPException(409, {"message": "files missing or wrong size", "paths": missing[:20],
                                  "count": len(missing)})

    tmp = Path(tempfile.mkdtemp(prefix="gs_validate_"))
    try:
        manifest = json.loads(storage.read_bytes(capture_key(cap.id, "manifest.json")))
        (tmp / "manifest.json").write_text(json.dumps(manifest))
        points = manifest.get("points3d_path")
        if points and points in declared:
            (tmp / points).parent.mkdir(parents=True, exist_ok=True)
            (tmp / points).write_bytes(storage.read_bytes(capture_key(cap.id, points)))
        report = validate_manifest(tmp / "manifest.json", check_files=False)
    except (ValueError, KeyError, TypeError) as exc:
        raise HTTPException(422, f"unreadable manifest.json: {exc}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    referenced = [f.get("file_path") for f in manifest.get("frames", [])]
    if manifest.get("points3d_path"):
        referenced.append(manifest["points3d_path"])
    not_uploaded = [p for p in referenced if p not in declared]
    if not_uploaded:
        report.errors.append(f"manifest references {len(not_uploaded)} files that were not uploaded, "
                             f"e.g. {not_uploaded[:3]}")

    cap.validation = {"ok": report.ok, "errors": report.errors, "warnings": report.warnings,
                      "stats": {k: str(v) for k, v in report.stats.items()}}
    cap.frame_count = len(manifest.get("frames", []))
    if not report.ok:
        cap.status = "invalid"
        db.commit()
        raise HTTPException(422, {"message": "capture failed validation", "validation": cap.validation})
    cap.status = "queued"
    db.add(Job(capture_id=cap.id))
    db.commit()
    db.refresh(cap)
    return _capture_out(cap, storage)


@captures.get("", response_model=list[S.CaptureOut])
def list_captures(user: User = Depends(current_user), db: Session = Depends(get_db), storage=Depends(get_storage)):
    caps = db.scalars(select(Capture).where(Capture.user_id == user.id).order_by(Capture.created_at.desc()))
    return [_capture_out(c, storage) for c in caps]


@captures.get("/{capture_id}", response_model=S.CaptureOut)
def get_capture(capture_id: str, user: User = Depends(current_user), db: Session = Depends(get_db),
                storage=Depends(get_storage)):
    return _capture_out(_own_capture(db, user, capture_id), storage)


@captures.post("/{capture_id}/retrain", response_model=S.CaptureOut)
def retrain(capture_id: str, body: S.RetrainIn, user: User = Depends(current_user), db: Session = Depends(get_db),
            storage=Depends(get_storage)):
    cap = _own_capture(db, user, capture_id)
    if cap.status not in ("ready", "failed"):
        raise HTTPException(409, f"cannot retrain while capture is {cap.status}")
    cap.status = "queued"
    db.add(Job(capture_id=cap.id, params=body.params))
    db.commit()
    db.refresh(cap)
    return _capture_out(cap, storage)


# ---------------------------------------------------------------- worker queue

def _running_job(db: Session, job_id: str, worker_id: str) -> Job:
    job = db.get(Job, job_id)
    if job is None or job.status != "running" or job.worker_id != worker_id:
        raise HTTPException(409, "job is not running on this worker (lease lost?)")
    return job


def _finish(job: Job, status: str, capture_status: str) -> None:
    job.status, job.finished_at, job.lease_expires_at = status, utcnow(), None
    job.capture.status = capture_status


@worker.post("/jobs/claim", response_model=S.ClaimOut | None)
def claim_job(body: S.ClaimIn, db: Session = Depends(get_db), storage=Depends(get_storage),
              settings: Settings = Depends(get_settings)):
    """Hand the oldest queued job (or one whose worker stopped heart-beating) to this worker."""
    now = utcnow()
    for _ in range(10):  # retry if another worker wins the race for the same row
        job = db.scalar(select(Job).where(or_(
            Job.status == "queued", (Job.status == "running") & (Job.lease_expires_at < now),
        )).order_by(Job.created_at).limit(1))
        if job is None:
            return None
        if job.attempts >= settings.max_attempts:  # stale lease, out of retries
            job.error = (job.error or "") + "\nlease expired on final attempt"
            _finish(job, "failed", "failed")
            db.commit()
            continue
        won = db.execute(
            update(Job).where(Job.id == job.id, Job.status == job.status, Job.attempts == job.attempts).values(
                status="running", stage="claimed", attempts=job.attempts + 1, worker_id=body.worker_id,
                lease_expires_at=now + timedelta(seconds=settings.job_lease_seconds), started_at=now,
            )).rowcount
        db.commit()
        if not won:
            continue
        db.refresh(job)
        job.capture.status = "training"
        db.commit()
        return S.ClaimOut(
            job_id=job.id, capture_id=job.capture_id, pipeline=job.pipeline, params=job.params,
            attempt=job.attempts,
            inputs=[S.InputFile(path=f["path"], size=f["size"],
                                url=storage.download_url(capture_key(job.capture_id, f["path"])))
                    for f in job.capture.files])
    return None


@worker.post("/jobs/{job_id}/heartbeat")
def heartbeat(job_id: str, body: S.HeartbeatIn, db: Session = Depends(get_db),
              settings: Settings = Depends(get_settings)):
    job = _running_job(db, job_id, body.worker_id)
    job.stage, job.message = body.stage, body.message
    job.lease_expires_at = utcnow() + timedelta(seconds=settings.job_lease_seconds)
    db.commit()
    return {"ok": True}


@worker.post("/jobs/{job_id}/upload-urls")
def upload_urls(job_id: str, body: S.UploadUrlsIn, db: Session = Depends(get_db), storage=Depends(get_storage)):
    job = db.get(Job, job_id)
    if job is None or job.status != "running":
        raise HTTPException(409, "job is not running")
    return {n: storage.upload_url(artifact_key(job.capture_id, job.id, n)) for n in body.names}


@worker.post("/jobs/{job_id}/complete")
def complete_job(job_id: str, body: S.CompleteIn, db: Session = Depends(get_db), storage=Depends(get_storage)):
    job = _running_job(db, job_id, body.worker_id)
    for a in body.artifacts:
        key = artifact_key(job.capture_id, job.id, a.name)
        size = storage.size(key)
        if size is None:
            raise HTTPException(409, f"artifact {a.name} was not uploaded")
        db.add(Artifact(job_id=job.id, kind=a.kind, name=a.name, key=key, bytes=size))
    job.metrics, job.stage, job.message = body.metrics, "done", ""
    _finish(job, "done", "ready")
    db.commit()
    return {"ok": True}


@worker.post("/jobs/{job_id}/fail")
def fail_job(job_id: str, body: S.FailIn, db: Session = Depends(get_db), settings: Settings = Depends(get_settings)):
    job = _running_job(db, job_id, body.worker_id)
    job.error = body.error[-20_000:]
    if body.retryable and job.attempts < settings.max_attempts:
        job.status, job.stage, job.worker_id, job.lease_expires_at = "queued", "queued (retry)", None, None
        job.capture.status = "queued"
    else:
        job.stage = "failed"
        _finish(job, "failed", "failed")
    db.commit()
    return {"ok": True, "status": job.status}


# ---------------------------------------------------------------- signed file transfer (LocalStorage only)

def _local(storage) -> LocalStorage:
    if not isinstance(storage, LocalStorage):
        raise HTTPException(404, "local file serving disabled")
    return storage


@files.put("/files/{key:path}")
async def put_file(key: str, exp: int, sig: str, request: Request, storage=Depends(get_storage)):
    st = _local(storage)
    if not st.verify("PUT", key, exp, sig):
        raise HTTPException(403, "bad or expired signature")
    dest = st.path(key)
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(dest.suffix + ".part")
    with open(tmp, "wb") as fh:
        async for chunk in request.stream():
            fh.write(chunk)
    tmp.replace(dest)  # atomic: a half-uploaded file never looks complete
    return Response(status_code=200)


@files.get("/files/{key:path}")
def get_file(key: str, exp: int, sig: str, storage=Depends(get_storage)):
    st = _local(storage)
    if not st.verify("GET", key, exp, sig):
        raise HTTPException(403, "bad or expired signature")
    path = st.path(key)
    if not path.is_file():
        raise HTTPException(404, "not found")
    return FileResponse(path)
