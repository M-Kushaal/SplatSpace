"""Full loop on CPU: phone-style upload -> validation -> queue -> worker (dry-run trainer) -> artifacts."""

import pytest

from conftest import queued_capture

pytest.importorskip("torch")
pytest.importorskip("gsplat")


def test_upload_to_splat_end_to_end(client, tmp_path):
    from gs_pipeline.ply_io import read_gaussians_ply
    from gs_worker.runner import Worker

    cid, h = queued_capture(client)
    worker = Worker("http://testserver", "test-worker-token", client=client, worker_id="e2e",
                    work_dir=tmp_path, dry_run=True, steps=30)
    assert worker.run(once=True) == 1

    cap = client.get(f"/captures/{cid}", headers=h).json()
    assert cap["status"] == "ready", cap["job"]["error"]
    arts = {a["name"]: a for a in cap["job"]["artifacts"]}
    assert {"splat.ply", "splat_object.ply", "metrics.json", "train.log"} <= set(arts)
    assert any(n.startswith("previews/") for n in arts)
    assert cap["job"]["metrics"]["dry_run"] is True
    # Random-noise test images can't be matched by COLMAP: refine must fall back to AR poses, not fail.
    assert cap["job"]["metrics"]["refine"]["fallback"] is True
    assert "refine_report.json" in arts

    ply = tmp_path / "splat.ply"
    ply.write_bytes(client.get(arts["splat.ply"]["url"]).content)
    assert len(read_gaussians_ply(ply)["means"]) > 0
