import io
import json

import numpy as np
import pytest
from fastapi.testclient import TestClient
from PIL import Image

from app.config import Settings
from app.main import create_app

WORKER = {"Authorization": "Bearer test-worker-token"}


@pytest.fixture
def settings(tmp_path):
    return Settings(database_url=f"sqlite:///{(tmp_path / 'test.db').as_posix()}", secret_key="test-secret-0123456789-0123456789-abcdef",
                    worker_token="test-worker-token", storage_backend="local",
                    local_storage_dir=tmp_path / "storage", public_base_url="http://testserver")


@pytest.fixture
def client(settings):
    with TestClient(create_app(settings)) as c:
        yield c


def auth_headers(client, email="a@example.com", password="password123"):
    r = client.post("/auth/register", json={"email": email, "password": password})
    assert r.status_code == 201, r.text
    return {"Authorization": f"Bearer {r.json()['access_token']}"}


def look_at_c2w_gl(eye, target, up=(0.0, 1.0, 0.0)):
    eye, target, up = (np.asarray(v, float) for v in (eye, target, up))
    back = (eye - target) / np.linalg.norm(eye - target)
    right = np.cross(up, back)
    right /= np.linalg.norm(right)
    c2w = np.eye(4)
    c2w[:3, 0], c2w[:3, 1], c2w[:3, 2], c2w[:3, 3] = right, np.cross(back, right), back, eye
    return c2w


def make_bundle(n=24, w=64, h=48, flip=False) -> dict[str, bytes]:
    """A small valid capture bundle (orbit around the origin) with real JPEGs."""
    rng = np.random.default_rng(0)
    files, frames = {}, []
    for k in range(n):
        a = 2 * np.pi * k / n
        c2w = look_at_c2w_gl([2 * np.cos(a), 0.3 + 0.5 * (k % 2), 2 * np.sin(a)], [0, 0, 0])
        if flip:  # OpenCV-convention mistake -> validator must reject
            c2w = c2w @ np.diag([1.0, -1.0, -1.0, 1.0])
        buf = io.BytesIO()
        Image.fromarray(rng.integers(0, 255, (h, w, 3), dtype=np.uint8)).save(buf, "JPEG")
        files[f"images/{k:04d}.jpg"] = buf.getvalue()
        frames.append({"file_path": f"images/{k:04d}.jpg", "transform_matrix": c2w.tolist()})
    manifest = {"schema_version": "1.0", "source": "synthetic", "camera_model": "PINHOLE",
                "fl_x": 60.0, "fl_y": 60.0, "cx": w / 2, "cy": h / 2, "w": w, "h": h,
                "object_bounds": {"center": [0, 0, 0], "radius": 0.5}, "frames": frames}
    files["manifest.json"] = json.dumps(manifest).encode()
    return files


def create_and_upload(client, headers, bundle, skip=()):
    r = client.post("/captures", headers=headers, json={
        "title": "test", "files": [{"path": p, "size": len(b)} for p, b in bundle.items()]})
    assert r.status_code == 201, r.text
    created = r.json()
    for t in created["uploads"]:
        if t["path"] not in skip:
            assert client.put(t["url"], content=bundle[t["path"]]).status_code == 200
    return created["capture_id"]


def queued_capture(client, headers=None):
    headers = headers or auth_headers(client)
    cid = create_and_upload(client, headers, make_bundle())
    r = client.post(f"/captures/{cid}/complete", headers=headers)
    assert r.status_code == 200, r.text
    return cid, headers
