import re
from datetime import datetime

from pydantic import BaseModel, EmailStr, Field, field_validator

SAFE_PATH = re.compile(r"^(?!/)(?!.*\.\.)[A-Za-z0-9._/-]{1,200}$")
ALLOWED_PARAMS = {"steps": (100, 30_000), "max_side": (256, 4096), "sh_degree": (0, 3)}


def check_safe_path(p: str) -> str:
    if not SAFE_PATH.match(p):
        raise ValueError(f"unsafe path {p!r}: relative, [A-Za-z0-9._/-] only, no '..'")
    return p


class Credentials(BaseModel):
    email: EmailStr
    password: str = Field(min_length=8, max_length=200)


class TokenOut(BaseModel):
    access_token: str
    token_type: str = "bearer"


class FileIn(BaseModel):
    path: str
    size: int = Field(ge=0)

    _safe = field_validator("path")(check_safe_path)


class CaptureCreate(BaseModel):
    title: str = Field(default="", max_length=200)
    files: list[FileIn] = Field(min_length=1)


class UploadTarget(BaseModel):
    path: str
    url: str
    method: str = "PUT"


class CaptureCreated(BaseModel):
    capture_id: str
    uploads: list[UploadTarget]


class ArtifactOut(BaseModel):
    kind: str
    name: str
    bytes: int
    url: str


class JobOut(BaseModel):
    id: str
    pipeline: str
    status: str
    stage: str
    message: str
    attempts: int
    metrics: dict | None
    error: str | None
    created_at: datetime
    finished_at: datetime | None
    artifacts: list[ArtifactOut] = []


class CaptureOut(BaseModel):
    id: str
    title: str
    status: str
    frame_count: int
    created_at: datetime
    validation: dict | None = None
    job: JobOut | None = None


class RetrainIn(BaseModel):
    params: dict = {}

    @field_validator("params")
    @classmethod
    def _check(cls, v: dict) -> dict:
        for k, val in v.items():
            if k not in ALLOWED_PARAMS:
                raise ValueError(f"unknown param {k!r}; allowed: {sorted(ALLOWED_PARAMS)}")
            lo, hi = ALLOWED_PARAMS[k]
            if not isinstance(val, int) or not lo <= val <= hi:
                raise ValueError(f"{k} must be an int in [{lo}, {hi}]")
        return v


# ---- worker API ----

class ClaimIn(BaseModel):
    worker_id: str = Field(min_length=1, max_length=100)


class InputFile(BaseModel):
    path: str
    url: str
    size: int


class ClaimOut(BaseModel):
    job_id: str
    capture_id: str
    pipeline: str
    params: dict
    attempt: int
    inputs: list[InputFile]


class HeartbeatIn(BaseModel):
    worker_id: str
    stage: str = Field(max_length=50)
    message: str = Field(default="", max_length=2000)


class UploadUrlsIn(BaseModel):
    names: list[str] = Field(min_length=1, max_length=50)

    @field_validator("names")
    @classmethod
    def _safe(cls, v):
        return [check_safe_path(n) for n in v]


class ArtifactIn(BaseModel):
    name: str
    kind: str = Field(max_length=50)

    _safe = field_validator("name")(check_safe_path)


class CompleteIn(BaseModel):
    worker_id: str
    metrics: dict = {}
    artifacts: list[ArtifactIn] = Field(min_length=1)


class FailIn(BaseModel):
    worker_id: str
    error: str = Field(max_length=20_000)
    retryable: bool = True
