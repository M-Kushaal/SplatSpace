"""Load capture manifests into per-frame camera records."""

import json
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .cameras import intrinsics_matrix

SCHEMA_PATH = Path(__file__).with_name("capture_manifest.schema.json")
INTRINSIC_KEYS = ("fl_x", "fl_y", "cx", "cy", "w", "h")


@dataclass
class CameraFrame:
    image_path: Path
    K: np.ndarray        # 3x3, pixels of the saved image
    width: int
    height: int
    c2w_gl: np.ndarray   # 4x4 camera-to-world, OpenGL convention
    index: int


def load_schema() -> dict:
    return json.loads(SCHEMA_PATH.read_text())


def schema_errors(manifest: dict) -> list[str]:
    import jsonschema

    validator = jsonschema.Draft202012Validator(load_schema())
    return [
        f"{'/'.join(map(str, e.absolute_path)) or '<root>'}: {e.message}"
        for e in sorted(validator.iter_errors(manifest), key=lambda e: list(e.absolute_path))
    ]


def read_manifest(path) -> dict:
    return json.loads(Path(path).read_text())


def frame_intrinsics(manifest: dict, frame: dict) -> dict:
    """Per-frame intrinsics with top-level fallbacks; missing keys are omitted."""
    out = {}
    for k in INTRINSIC_KEYS:
        v = frame.get(k, manifest.get(k))
        if v is not None:
            out[k] = v
    return out


def load_frames(manifest_path) -> tuple[list[CameraFrame], dict]:
    manifest_path = Path(manifest_path)
    manifest = read_manifest(manifest_path)
    frames = []
    for i, f in enumerate(manifest["frames"]):
        intr = frame_intrinsics(manifest, f)
        missing = [k for k in INTRINSIC_KEYS if k not in intr]
        if missing:
            raise ValueError(f"frame {i} ({f['file_path']}) missing intrinsics: {missing}")
        frames.append(CameraFrame(
            image_path=manifest_path.parent / f["file_path"],
            K=intrinsics_matrix(intr["fl_x"], intr["fl_y"], intr["cx"], intr["cy"]),
            width=int(intr["w"]),
            height=int(intr["h"]),
            c2w_gl=np.asarray(f["transform_matrix"], dtype=np.float64),
            index=i,
        ))
    return frames, manifest


def points_path(manifest_path, manifest: dict) -> Path | None:
    rel = manifest.get("points3d_path")
    return Path(manifest_path).parent / rel if rel else None
