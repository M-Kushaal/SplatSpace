from datetime import timedelta

import pytest

from app.db import utcnow
from app.models import Job
from conftest import WORKER, auth_headers, create_and_upload, make_bundle, queued_capture


def test_auth_flow(client):
    auth_headers(client)
    assert client.post("/auth/register", json={"email": "a@example.com", "password": "password123"}).status_code == 409
    assert client.post("/auth/login", json={"email": "A@example.com", "password": "password123"}).status_code == 200
    assert client.post("/auth/login", json={"email": "a@example.com", "password": "wrongpass1"}).status_code == 401
    assert client.get("/captures").status_code == 401
    assert client.get("/captures", headers={"Authorization": "Bearer junk"}).status_code == 401


def test_capture_upload_complete_queues_job(client):
    cid, h = queued_capture(client)
    cap = client.get(f"/captures/{cid}", headers=h).json()
    assert cap["status"] == "queued" and cap["frame_count"] == 24
    assert cap["validation"]["ok"] and cap["job"]["status"] == "queued"
    assert [c["id"] for c in client.get("/captures", headers=h).json()] == [cid]


def test_complete_rejects_missing_upload(client):
    h = auth_headers(client)
    cid = create_and_upload(client, h, make_bundle(), skip={"images/0003.jpg"})
    r = client.post(f"/captures/{cid}/complete", headers=h)
    assert r.status_code == 409 and r.json()["detail"]["paths"] == ["images/0003.jpg"]


def test_validator_rejects_wrong_pose_convention(client):
    h = auth_headers(client)
    cid = create_and_upload(client, h, make_bundle(flip=True))
    r = client.post(f"/captures/{cid}/complete", headers=h)
    assert r.status_code == 422 and "convention" in str(r.json())
    assert client.get(f"/captures/{cid}", headers=h).json()["status"] == "invalid"


@pytest.mark.parametrize("bad", ["../etc/passwd", "/abs.jpg", "a\\b.jpg", "x y.jpg"])
def test_unsafe_paths_rejected(client, bad):
    h = auth_headers(client)
    r = client.post("/captures", headers=h, json={"files": [{"path": "manifest.json", "size": 1},
                                                            {"path": bad, "size": 1}]})
    assert r.status_code == 422


def test_captures_are_private(client):
    cid, _ = queued_capture(client)
    other = auth_headers(client, email="b@example.com")
    assert client.get(f"/captures/{cid}", headers=other).status_code == 404


def test_tampered_or_wrong_method_signature_rejected(client):
    h = auth_headers(client)
    r = client.post("/captures", headers=h, json={"files": [{"path": "manifest.json", "size": 2}]})
    url = r.json()["uploads"][0]["url"]
    assert client.put(url.replace("sig=", "sig=0"), content=b"{}").status_code == 403
    assert client.get(url).status_code == 403  # a PUT signature can't be used to read


def test_worker_claim_heartbeat_complete(client):
    cid, h = queued_capture(client)
    assert client.post("/worker/jobs/claim", json={"worker_id": "w1"}).status_code == 401
    job = client.post("/worker/jobs/claim", json={"worker_id": "w1"}, headers=WORKER).json()
    assert job["capture_id"] == cid and job["attempt"] == 1 and len(job["inputs"]) == 25
    manifest_in = next(i for i in job["inputs"] if i["path"] == "manifest.json")
    assert client.get(manifest_in["url"]).json()["schema_version"] == "1.0"
    assert client.post("/worker/jobs/claim", json={"worker_id": "w2"}, headers=WORKER).json() is None
    assert client.get(f"/captures/{cid}", headers=h).json()["status"] == "training"

    jid = job["job_id"]
    hb = {"worker_id": "w1", "stage": "training", "message": "step 100"}
    assert client.post(f"/worker/jobs/{jid}/heartbeat", json=hb, headers=WORKER).status_code == 200
    assert client.post(f"/worker/jobs/{jid}/heartbeat", json={**hb, "worker_id": "w2"},
                       headers=WORKER).status_code == 409
    urls = client.post(f"/worker/jobs/{jid}/upload-urls", json={"names": ["splat.ply"]}, headers=WORKER).json()
    complete = {"worker_id": "w1", "metrics": {"psnr_test_mean": 30.0},
                "artifacts": [{"name": "splat.ply", "kind": "splat_ply"}]}
    client.put(urls["splat.ply"], content=b"ply-bytes")
    assert client.post(f"/worker/jobs/{jid}/complete", json=complete, headers=WORKER).status_code == 200

    cap = client.get(f"/captures/{cid}", headers=h).json()
    assert cap["status"] == "ready" and cap["job"]["metrics"]["psnr_test_mean"] == 30.0
    art = cap["job"]["artifacts"][0]
    assert art["kind"] == "splat_ply" and art["bytes"] == 9 and client.get(art["url"]).content == b"ply-bytes"


def _expire_lease(client, job_id):
    db = client.app.state.session_factory()
    db.get(Job, job_id).lease_expires_at = utcnow() - timedelta(seconds=1)
    db.commit()
    db.close()


def test_dead_worker_lease_is_reclaimed_then_fails_after_max_attempts(client):
    cid, h = queued_capture(client)
    j1 = client.post("/worker/jobs/claim", json={"worker_id": "w1"}, headers=WORKER).json()
    _expire_lease(client, j1["job_id"])
    j2 = client.post("/worker/jobs/claim", json={"worker_id": "w2"}, headers=WORKER).json()
    assert j2["job_id"] == j1["job_id"] and j2["attempt"] == 2
    _expire_lease(client, j1["job_id"])
    assert client.post("/worker/jobs/claim", json={"worker_id": "w3"}, headers=WORKER).json() is None
    assert client.get(f"/captures/{cid}", headers=h).json()["status"] == "failed"


def test_fail_retries_then_gives_up(client):
    cid, h = queued_capture(client)
    for attempt, expected in ((1, "queued"), (2, "failed")):
        job = client.post("/worker/jobs/claim", json={"worker_id": "w"}, headers=WORKER).json()
        assert job["attempt"] == attempt
        r = client.post(f"/worker/jobs/{job['job_id']}/fail", json={"worker_id": "w", "error": "CUDA OOM"},
                        headers=WORKER)
        assert r.json()["status"] == expected
    cap = client.get(f"/captures/{cid}", headers=h).json()
    assert cap["status"] == "failed" and cap["job"]["error"] == "CUDA OOM"
    r = client.post(f"/captures/{cid}/retrain", headers=h, json={"params": {"steps": 3000}})
    assert r.status_code == 200 and r.json()["job"]["status"] == "queued"
    assert client.post(f"/captures/{cid}/retrain", headers=h, json={"params": {"evil": 1}}).status_code == 422


def test_s3_storage_presigns_and_reads():
    boto3 = pytest.importorskip("boto3")
    from moto import mock_aws

    from app.config import Settings
    from app.storage import S3Storage

    with mock_aws():
        boto3.client("s3", region_name="us-east-1").create_bucket(Bucket="caps")
        st = S3Storage(Settings(storage_backend="s3", s3_bucket="caps", s3_region="us-east-1",
                                s3_access_key_id="x", s3_secret_access_key="y"))
        assert "captures/c1/input/manifest.json" in st.upload_url("captures/c1/input/manifest.json")
        assert "Signature" in st.download_url("k")
        assert st.size("k") is None
        st.client.put_object(Bucket="caps", Key="k", Body=b"abc")
        assert st.size("k") == 3 and st.read_bytes("k") == b"abc"
