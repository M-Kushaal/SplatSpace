"""Generate the Kaggle GPU test notebooks in ../gpu_tests from the real gs_pipeline sources.

The notebooks embed every gs_pipeline file via %%writefile, so what runs on the GPU is
exactly the code in this repo. Re-run this script after changing gs_pipeline:

    python worker/build_gpu_notebooks.py
"""

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent
PKG = ROOT / "gs_pipeline"
OUT_DIR = ROOT.parent / "gpu_tests"
GSPLAT_VERSION = "1.5.3"


def md(text: str) -> dict:
    return {"cell_type": "markdown", "metadata": {}, "source": text.strip().splitlines(keepends=True)}


def code(text: str) -> dict:
    return {"cell_type": "code", "metadata": {}, "execution_count": None, "outputs": [],
            "source": text.strip().splitlines(keepends=True)}


def notebook(cells: list[dict]) -> dict:
    return {
        "cells": cells,
        "metadata": {
            "kernelspec": {"display_name": "Python 3", "language": "python", "name": "python3"},
            "language_info": {"name": "python"},
            "kaggle": {"accelerator": "nvidiaTeslaT4", "isInternetEnabled": True, "isGpuEnabled": True},
        },
        "nbformat": 4,
        "nbformat_minor": 5,
    }


def source_cells(dest: str = "/kaggle/working/gs_pipeline") -> list[dict]:
    files = sorted(p for p in PKG.iterdir() if p.suffix in {".py", ".json"})
    cells = [code(f"import os\nos.makedirs('{dest}', exist_ok=True)")]
    for p in files:
        cells.append(code(f"%%writefile {dest}/{p.name}\n{p.read_text(encoding='utf-8')}"))
    return cells


ENV_CHECK = """
import sys, shutil, subprocess, torch
print("python", sys.version.split()[0], "| torch", torch.__version__, "| CUDA", torch.version.cuda)
assert torch.cuda.is_available(), "No GPU: Settings -> Accelerator -> GPU T4 x2"
name, cap = torch.cuda.get_device_name(0), torch.cuda.get_device_capability(0)
print("GPU:", name, "compute capability", cap)
if cap < (7, 0):
    print("WARNING: this GPU is too old for current PyTorch/gsplat. Switch Accelerator to 'GPU T4 x2'.")
nvcc = shutil.which("nvcc") or ("/usr/local/cuda/bin/nvcc" if shutil.os.path.exists("/usr/local/cuda/bin/nvcc") else None)
print("nvcc:", nvcc)
if nvcc:
    print(subprocess.run([nvcc, "--version"], capture_output=True, text=True).stdout.strip().splitlines()[-1])
"""

INSTALL = f"""
# gsplat ships as source and compiles its CUDA kernels on first use (~3-8 min, once per session).
!pip install -q --no-deps gsplat=={GSPLAT_VERSION}
!pip install -q jaxtyping rich ninja remotezip jsonschema
"""

COMPILE_CHECK = """
import os, time, torch
maj, mnr = torch.cuda.get_device_capability(0)
os.environ["TORCH_CUDA_ARCH_LIST"] = f"{maj}.{mnr}"          # compile only for this GPU (faster)
os.environ["PATH"] = "/usr/local/cuda/bin:" + os.environ["PATH"]
t = time.time()
import gsplat
from gsplat import rasterization
N = 2000
means = torch.randn(N, 3, device="cuda") * 0.5 + torch.tensor([0, 0, 4.0], device="cuda")
quats = torch.nn.functional.normalize(torch.randn(N, 4, device="cuda"), dim=-1)
scales = torch.full((N, 3), 0.03, device="cuda")
opac = torch.full((N,), 0.8, device="cuda")
colors = torch.rand(N, 3, device="cuda")
K = torch.tensor([[100.0, 0, 64], [0, 100.0, 64], [0, 0, 1]], device="cuda")
img, alpha, meta = rasterization(means, quats, scales, opac, colors, torch.eye(4, device="cuda")[None], K[None], 128, 128)
torch.cuda.synchronize()
print(f"gsplat {gsplat.__version__} CUDA OK: rendered {tuple(img.shape)}, mean alpha {alpha.mean():.3f}, "
      f"compile+render {time.time() - t:.0f}s")
"""

RESULTS = """
import json, glob, os
from IPython.display import Image, display
print(json.dumps(json.load(open("/kaggle/working/out/metrics.json")), indent=2))
for p in sorted(glob.glob("/kaggle/working/out/previews/*.png")):
    print(os.path.basename(p)); display(Image(p))
for p in sorted(glob.glob("/kaggle/working/out/*")):
    print(f"{os.path.getsize(p) / 1e6:8.1f} MB  {p}") if os.path.isfile(p) else None
print("\\nDownload out/splat_object.ply (object only) or out/splat.ply (whole scene) from the Output panel")
print("and drop it into https://superspl.at/editor")
"""


def setup_cells() -> list[dict]:
    cells = [md("## 1. Environment check"), code(ENV_CHECK),
             md("## 2. Install gsplat + compile CUDA kernels"), code(INSTALL), code(COMPILE_CHECK),
             md("## 3. Write our pipeline code (`gs_pipeline`, generated from the repo)")]
    return cells + source_cells()


def golden_run_bonsai() -> dict:
    cells = [md("""
# GPU test 01 — golden run (bonsai)

**Goal:** prove that the GPU side works once, end to end: *capture manifest → our trainer → viewable `splat.ply`*.
Uses the public mip-NeRF 360 **bonsai** scene (object-centric 360° orbit, like our app's captures),
converted into **our** manifest format. Only ~180 MB is downloaded (not the 12 GB zip).

**Kaggle settings (right sidebar):** Accelerator = **GPU T4 x2** · Internet = **On** (needs a phone-verified account).
Then **Run All**. Expected total time ≈ 20–30 min.

**Pass criteria**
1. Last cell prints `psnr_test_mean` ≳ **25** (good runs reach ~28–31).
2. `splat.ply` (Output panel → `out/`) opens in **https://superspl.at/editor** (drag & drop) and shows the bonsai.

**Report back:** the printed metrics JSON + whether the splat looks right (a screenshot helps).
If anything fails, send the error output of the first failing cell.

> Generated by `worker/build_gpu_notebooks.py` — don't edit by hand.
""")]
    cells += setup_cells()
    cells += [
        md("## 4. Download only the bonsai scene (sparse model + 4x-downscaled images)"),
        code("""
from remotezip import RemoteZip
import os, time
t = time.time()
with RemoteZip("http://storage.googleapis.com/gresearch/refraw360/360_v2.zip") as z:
    names = [i.filename for i in z.infolist()
             if i.filename.startswith(("bonsai/sparse/0/", "bonsai/images_4/")) and not i.filename.endswith("/")]
    for n in names:
        z.extract(n, "/tmp/data")
print(len(names), "files in", round(time.time() - t), "s")
"""),
        md("## 5. Convert to our manifest, validate (CPU checks), plot cameras"),
        code("""
%cd /kaggle/working
!python -m gs_pipeline convert-colmap /tmp/data/bonsai --images images_4
!python -m gs_pipeline validate /tmp/data/bonsai/manifest.json
!python -m gs_pipeline plot /tmp/data/bonsai/manifest.json --out /kaggle/working/cameras.png
from IPython.display import Image, display
display(Image("/kaggle/working/cameras.png"))
"""),
        md("## 6. Train (7k steps — the single GPU-heavy step, ~10–15 min on a T4)"),
        code("""
%cd /kaggle/working
!python -m gs_pipeline train /tmp/data/bonsai/manifest.json --out /kaggle/working/out --steps 7000
"""),
        md("## 7. Results — metrics, previews (left = photo, right = render), output files"),
        code(RESULTS),
    ]
    return notebook(cells)


def phone_capture() -> dict:
    cells = [md("""
# GPU test 02 — real phone capture

**Goal:** train a splat from a capture made by our Android app (poses from ARCore, no COLMAP).

**Before running:** upload the exported capture `.zip` as a **private Kaggle Dataset**
(Kaggle → Datasets → New Dataset → drop the zip → Private → Create), then in this notebook use
*Add Input* → your dataset. The notebook finds the capture under `/kaggle/input` automatically.

**Kaggle settings:** Accelerator = **GPU T4 x2** · Internet = **On**. Then **Run All** (~15–20 min).

**Report back:** the metrics JSON, the overlay image, and whether `out/splat_object.ply` looks right in
https://superspl.at/editor.

> Generated by `worker/build_gpu_notebooks.py` — don't edit by hand.
""")]
    cells += setup_cells()
    cells += [
        md("## 4. Find and unpack the capture"),
        code("""
import glob, os, zipfile
zips = sorted(glob.glob("/kaggle/input/**/*.zip", recursive=True))
manifests = sorted(glob.glob("/kaggle/input/**/manifest.json", recursive=True))
if zips:
    print("using", zips[0])
    zipfile.ZipFile(zips[0]).extractall("/tmp/capture")
    CAPTURE = "/tmp/capture"
elif manifests:  # Kaggle usually auto-extracts uploaded zips
    CAPTURE = os.path.dirname(manifests[0])
else:
    raise FileNotFoundError("No capture found: add your capture dataset via 'Add Input'")
print("capture folder:", CAPTURE, "|", len(glob.glob(CAPTURE + "/images/*.jpg")), "images")
"""),
        md("## 4b. Pose refinement (skipped if the bundle was already refined on the PC)"),
        code("""
import json, subprocess, sys
source = json.load(open(CAPTURE + "/manifest.json")).get("source", "")
if "colmap" in source:
    print("already refined:", source)
else:
    !pip install -q pycolmap opencv-python-headless
    !python -m gs_pipeline refine {CAPTURE}/manifest.json --out /tmp/refined --threads 4
    report = json.load(open("/tmp/refined/refine_report.json"))
    CAPTURE = str(__import__("pathlib").Path(report["manifest"]).parent)
    print("training on:", CAPTURE, "| fallback to AR poses:", report["fallback"])
"""),
        md("## 5. CPU checks: validate, camera plot, overlay (red = AR points, cyan = tapped cylinder, green = refined)"),
        code("""
%cd /kaggle/working
!python -m gs_pipeline validate {CAPTURE}/manifest.json
!python -m gs_pipeline plot {CAPTURE}/manifest.json --out /kaggle/working/cameras.png
!python -m gs_pipeline overlay {CAPTURE}/manifest.json --out /kaggle/working/overlay.png --frames 9
from IPython.display import Image, display
display(Image("/kaggle/working/cameras.png")); display(Image("/kaggle/working/overlay.png"))
"""),
        md("## 6. Train (object-focused, 15k steps, full resolution)"),
        code("""
%cd /kaggle/working
!python -m gs_pipeline train {CAPTURE}/manifest.json --out /kaggle/working/out --steps 15000 --max-side 1920
"""),
        md("## 7. Results"),
        code(RESULTS),
    ]
    return notebook(cells)


def scene_capture() -> dict:
    nb = phone_capture()
    cells = nb["cells"]
    cells[0] = md("""
# GPU test 03 — scene (room) capture

**Goal:** train a whole-room splat from a *Scene capture* made by our Android app (4–6 spots, phone at
arm's length). Uses the `scene` training profile: 30k steps, images at 1600 px, up to 3M Gaussians,
plus monocular depth priors, MCMC densification, per-photo exposure and pose refinement against holes and
"fur" on blank walls and ceilings.

**Before running:** upload the capture `.zip` (ideally the **refined** one we made on the PC, so this
notebook skips the slow CPU pose refinement) as a **private Kaggle Dataset**, then *Add Input* → it.

**Kaggle settings:** Accelerator = **GPU T4 x2** · Internet = **On**. Then **Run All**
(~70–100 min; add ~20 min if the capture still needs pose refinement).

**Report back:** the metrics JSON (especially `holes.hole_fraction`), the preview images, and download
`out/splat_object.ply` (the room: floor at y = 0, cropped to the room) — open it in our viewer.
`previews/holes_station<k>.png` show the view from each capture spot as a cubemap
(front right back left / up down); magenta = see-through holes = places to re-shoot.

> Generated by `worker/build_gpu_notebooks.py` — don't edit by hand.
""")
    for i, c in enumerate(cells):
        src = "".join(c["source"])
        if "gs_pipeline overlay" in src:  # the overlay draws the object cylinder; rooms have none
            cells[i] = code("""
%cd /kaggle/working
!python -m gs_pipeline validate {CAPTURE}/manifest.json
!python -m gs_pipeline plot {CAPTURE}/manifest.json --out /kaggle/working/cameras.png
from IPython.display import Image, display
display(Image("/kaggle/working/cameras.png"))
""")
            cells[i - 1] = md("## 5. CPU checks: validate (must say capture mode: scene) + camera plot")
        elif "gs_pipeline train" in src:
            cells[i] = code("""
%cd /kaggle/working
!python -m gs_pipeline train {CAPTURE}/manifest.json --out /kaggle/working/out --profile scene --depth-dir /kaggle/working/depth
""")
            cells[i - 1] = md("""
## 6. Train (scene profile: 30k steps, 1600 px, MCMC with cap 3M, depth loss, per-photo exposure,
pose refinement, anti-aliased; Gaussians seen by < 3 photos are pruned at the end)
""")
            cells[i - 1:i - 1] = [md("""
## 5b. Depth priors (Depth-Anything-V2, ~3-5 min)
Blank ceilings and walls give COLMAP almost no points, so plain 3DGS leaves holes or "fur" there.
Each photo's predicted depth is aligned to the sparse points; the result seeds Gaussians on those
surfaces and supervises their shape. Check `aligned` (should be close to `frames`) and `median_rel_err`
(below ~0.1 is good).
"""), code("""
import transformers
from packaging.version import Version
if Version(transformers.__version__) < Version("4.45"):
    !pip install -q "transformers>=4.45"
%cd /kaggle/working
!python -m gs_pipeline depth {CAPTURE}/manifest.json --out /kaggle/working/depth
""")]
            break
    cells += [md("""
## 8. (Optional) Compare with an earlier run
Add the earlier run's notebook output as an input (*Add Input* → *Your Work* → that notebook version). This
scores its `splat.ply` with the same hole report, so `hole_fraction` can be compared like for like.
"""), code("""
import glob, json
old = [p for p in glob.glob("/kaggle/input/**/splat.ply", recursive=True)]
if not old:
    print("no earlier splat.ply attached - skipping")
else:
    print("earlier splat:", old[0])
    !python -m gs_pipeline holes {old[0]} {CAPTURE}/manifest.json --out /kaggle/working/holes_old
    new = json.load(open("/kaggle/working/out/metrics.json"))
    print("this run's hole_fraction:", new["holes"]["hole_fraction"], "| per spot:", new["holes"]["hole_fraction_per_station"])
""")]
    return nb


def main():
    OUT_DIR.mkdir(exist_ok=True)
    for name, nb in (("01_golden_run_bonsai.ipynb", golden_run_bonsai()), ("02_phone_capture.ipynb", phone_capture()),
                     ("03_scene_capture.ipynb", scene_capture())):
        path = OUT_DIR / name
        path.write_text(json.dumps(nb, indent=1), encoding="utf-8")
        print("wrote", path)


if __name__ == "__main__":
    main()
