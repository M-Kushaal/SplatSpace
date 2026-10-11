"""Object storage behind signed URLs. Clients (phone, worker) never get storage credentials:
they PUT/GET the URLs the API hands out.
"""

import hashlib
import hmac
import time
from pathlib import Path
from urllib.parse import quote, urlencode

from .config import Settings


def capture_key(capture_id: str, path: str) -> str:
    return f"captures/{capture_id}/input/{path}"


def artifact_key(capture_id: str, job_id: str, name: str) -> str:
    return f"captures/{capture_id}/jobs/{job_id}/{name}"


class LocalStorage:
    """Files on local disk; URLs point at this API's /files route, signed with HMAC."""

    def __init__(self, root: Path, base_url: str, secret: str, ttl: int):
        self.root, self.base_url, self.secret, self.ttl = Path(root), base_url.rstrip("/"), secret.encode(), ttl
        self.root.mkdir(parents=True, exist_ok=True)

    def sign(self, method: str, key: str, exp: int) -> str:
        return hmac.new(self.secret, f"{method}\n{key}\n{exp}".encode(), hashlib.sha256).hexdigest()

    def verify(self, method: str, key: str, exp: int, sig: str) -> bool:
        return exp >= time.time() and hmac.compare_digest(self.sign(method, key, exp), sig)

    def _url(self, method: str, key: str) -> str:
        exp = int(time.time()) + self.ttl
        return f"{self.base_url}/files/{quote(key)}?{urlencode({'exp': exp, 'sig': self.sign(method, key, exp)})}"

    def upload_url(self, key: str) -> str:
        return self._url("PUT", key)

    def download_url(self, key: str) -> str:
        return self._url("GET", key)

    def path(self, key: str) -> Path:
        p = (self.root / key).resolve()
        if self.root.resolve() not in p.parents:
            raise ValueError("key escapes storage root")
        return p

    def size(self, key: str) -> int | None:
        p = self.path(key)
        return p.stat().st_size if p.is_file() else None

    def read_bytes(self, key: str) -> bytes:
        return self.path(key).read_bytes()


class S3Storage:
    """Cloudflare R2 / S3 via presigned URLs."""

    def __init__(self, settings: Settings):
        import boto3

        self.bucket, self.ttl = settings.s3_bucket, settings.url_ttl_seconds
        self.client = boto3.client(
            "s3", endpoint_url=settings.s3_endpoint_url, region_name=settings.s3_region,
            aws_access_key_id=settings.s3_access_key_id, aws_secret_access_key=settings.s3_secret_access_key,
        )

    def upload_url(self, key: str) -> str:
        return self.client.generate_presigned_url("put_object", Params={"Bucket": self.bucket, "Key": key},
                                                  ExpiresIn=self.ttl)

    def download_url(self, key: str) -> str:
        return self.client.generate_presigned_url("get_object", Params={"Bucket": self.bucket, "Key": key},
                                                  ExpiresIn=self.ttl)

    def size(self, key: str) -> int | None:
        from botocore.exceptions import ClientError

        try:
            return self.client.head_object(Bucket=self.bucket, Key=key)["ContentLength"]
        except ClientError:
            return None

    def read_bytes(self, key: str) -> bytes:
        return self.client.get_object(Bucket=self.bucket, Key=key)["Body"].read()


def make_storage(settings: Settings):
    if settings.storage_backend == "s3":
        return S3Storage(settings)
    if settings.storage_backend == "local":
        return LocalStorage(settings.local_storage_dir, settings.public_base_url, settings.secret_key,
                            settings.url_ttl_seconds)
    raise ValueError(f"unknown storage backend {settings.storage_backend!r}")
