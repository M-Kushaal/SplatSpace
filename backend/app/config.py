"""Settings from environment variables (prefix GS_), e.g. GS_DATABASE_URL, GS_STORAGE_BACKEND=s3."""

import os
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict

# Dev data lives outside the (OneDrive-synced) repo.
DEV_DATA_DIR = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "gs_backend"


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="GS_", env_file=".env", extra="ignore")

    database_url: str = f"sqlite:///{(DEV_DATA_DIR / 'dev.db').as_posix()}"
    secret_key: str = "dev-secret-change-me-in-production-0123456789"
    worker_token: str = "dev-worker-token"
    token_ttl_days: int = 30

    # "local": files stored on this machine and served by the API itself (dev / LAN testing).
    # "s3": Cloudflare R2 or any S3-compatible bucket via presigned URLs.
    storage_backend: str = "local"
    local_storage_dir: Path = DEV_DATA_DIR / "storage"
    public_base_url: str = "http://127.0.0.1:8000"  # what phones/workers use to reach this API
    s3_bucket: str = ""
    s3_endpoint_url: str | None = None  # R2: https://<account_id>.r2.cloudflarestorage.com
    s3_access_key_id: str | None = None
    s3_secret_access_key: str | None = None
    s3_region: str = "auto"

    serve_viewer: bool = True
    viewer_dir: Path = Path(__file__).resolve().parents[2] / "viewer"

    url_ttl_seconds: int = 6 * 3600
    job_lease_seconds: int = 600
    max_attempts: int = 2
    max_files_per_capture: int = 600
    max_file_bytes: int = 40 * 1024 * 1024
