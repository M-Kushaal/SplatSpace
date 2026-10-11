"""Upload a local capture-bundle folder exactly the way the phone app will, then poll its status.

    python scripts/upload_bundle.py <bundle_dir> [--api http://127.0.0.1:8000] [--email you@x.com] [--max-frames 40]

Uploads manifest.json, points3d.ply and the referenced images (optionally only the first N frames).
"""

import argparse
import json
import sys
import tempfile
import time
from pathlib import Path

import httpx


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("bundle_dir")
    p.add_argument("--api", default="http://127.0.0.1:8000")
    p.add_argument("--email", default="dev@example.com")
    p.add_argument("--password", default="devpassword123")
    p.add_argument("--max-frames", type=int, default=0)
    p.add_argument("--wait", type=int, default=0, help="seconds to poll for ready/failed (0 = don't wait)")
    a = p.parse_args()

    root = Path(a.bundle_dir)
    manifest = json.loads((root / "manifest.json").read_text())
    if a.max_frames:
        manifest["frames"] = manifest["frames"][::max(1, len(manifest["frames"]) // a.max_frames)][:a.max_frames]
    body = json.dumps(manifest).encode()
    files = {"manifest.json": body}
    for rel in [f["file_path"] for f in manifest["frames"]] + [manifest.get("points3d_path")]:
        if rel:
            files[rel] = (root / rel).read_bytes()

    c = httpx.Client(base_url=a.api, timeout=120)
    creds = {"email": a.email, "password": a.password}
    r = c.post("/auth/login", json=creds)
    if r.status_code == 401:
        r = c.post("/auth/register", json=creds)
    r.raise_for_status()
    h = {"Authorization": f"Bearer {r.json()['access_token']}"}

    r = c.post("/captures", headers=h, json={"title": root.name,
                                              "files": [{"path": k, "size": len(v)} for k, v in files.items()]})
    r.raise_for_status()
    cid = r.json()["capture_id"]
    t = time.time()
    for target in r.json()["uploads"]:
        httpx.put(target["url"], content=files[target["path"]], timeout=120).raise_for_status()
    print(f"uploaded {len(files)} files ({sum(map(len, files.values())) / 1e6:.1f} MB) in {time.time() - t:.1f}s")

    r = c.post(f"/captures/{cid}/complete", headers=h)
    print("complete:", r.status_code, json.dumps(r.json(), indent=1)[:1500])
    if r.status_code != 200:
        return 1
    deadline = time.time() + a.wait
    while time.time() < deadline:
        cap = c.get(f"/captures/{cid}", headers=h).json()
        print(f"  status={cap['status']} stage={cap['job']['stage']} {cap['job']['message'][:80]}")
        if cap["status"] in ("ready", "failed"):
            for art in cap["job"]["artifacts"]:
                print(f"  artifact {art['kind']:18s} {art['bytes'] / 1e6:7.2f} MB  {art['name']}")
            return 0 if cap["status"] == "ready" else 1
        time.sleep(3)
    print("capture_id:", cid)
    return 0


if __name__ == "__main__":
    sys.exit(main())
