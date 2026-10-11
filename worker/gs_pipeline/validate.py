"""CPU-only checks that a capture bundle is trainable.

Errors = the bundle must not be sent to the GPU. Warnings = it will train, but
quality may suffer. The look-at check catches flipped axis conventions: if the
object ends up behind most cameras, the pose math is wrong.
"""

from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from .cameras import project, viewmat_from_c2w_gl
from .manifest import INTRINSIC_KEYS, frame_intrinsics, load_frames, points_path, read_manifest, schema_errors

MIN_FRAMES = 20


@dataclass
class ValidationReport:
    errors: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    stats: dict = field(default_factory=dict)

    @property
    def ok(self) -> bool:
        return not self.errors

    def summary(self) -> str:
        lines = [f"{'OK' if self.ok else 'FAILED'}: {len(self.errors)} error(s), {len(self.warnings)} warning(s)"]
        lines += [f"  stat  {k}: {v}" for k, v in self.stats.items()]
        lines += [f"  ERROR {e}" for e in self.errors]
        lines += [f"  WARN  {w}" for w in self.warnings]
        return "\n".join(lines)


def load_points(manifest_path, manifest: dict) -> np.ndarray | None:
    pts = points_path(manifest_path, manifest)
    if pts is None or not pts.exists():
        return None
    from .ply_io import read_points_ply

    return read_points_ply(pts)[0]


def object_bounds(manifest_path, manifest: dict):
    """Object cylinder refined from AR points (see bounds.py), or None if the capture has no bounds."""
    from .bounds import refine_bounds

    return refine_bounds(manifest, load_points(manifest_path, manifest))


def scene_target(manifest: dict, manifest_path, c2ws: list[np.ndarray]) -> tuple[np.ndarray, str]:
    """Point the cameras should be looking at: object middle, else median of points, else ray intersection."""
    b = object_bounds(manifest_path, manifest)
    if b is not None:
        return b.middle, f"object_bounds ({b.source})"
    xyz = load_points(manifest_path, manifest)
    if xyz is not None and len(xyz):
        return np.median(xyz, axis=0), "points3d median"
    # Least-squares point closest to all optical axes.
    A, b = np.zeros((3, 3)), np.zeros(3)
    for c2w in c2ws:
        d = -c2w[:3, 2] / np.linalg.norm(c2w[:3, 2])
        P = np.eye(3) - np.outer(d, d)
        A += P
        b += P @ c2w[:3, 3]
    return np.linalg.lstsq(A, b, rcond=None)[0], "optical-axes intersection"


def validate_manifest(manifest_path, check_files: bool = True) -> ValidationReport:
    manifest_path = Path(manifest_path)
    rep = ValidationReport()
    try:
        manifest = read_manifest(manifest_path)
    except Exception as exc:  # noqa: BLE001 - any parse failure is a validation error
        rep.errors.append(f"cannot read manifest: {exc}")
        return rep

    rep.errors += [f"schema: {e}" for e in schema_errors(manifest)]
    if rep.errors:
        return rep

    frames_raw = manifest["frames"]
    for i, f in enumerate(frames_raw):
        missing = [k for k in INTRINSIC_KEYS if k not in frame_intrinsics(manifest, f)]
        if missing:
            rep.errors.append(f"frame {i}: missing intrinsics {missing}")
    if rep.errors:
        return rep

    frames, _ = load_frames(manifest_path)
    rep.stats["frames"] = len(frames)
    if len(frames) < MIN_FRAMES:
        rep.warnings.append(f"only {len(frames)} frames; >= {MIN_FRAMES} recommended")

    paths = [f["file_path"] for f in frames_raw]
    dupes = {p for p in paths if paths.count(p) > 1}
    if dupes:
        rep.errors.append(f"duplicate file_path entries: {sorted(dupes)[:5]}")

    if any(abs(manifest.get(k, 0.0)) > 1e-6 for k in ("k1", "k2", "p1", "p2")):
        rep.warnings.append("lens distortion coefficients present; trainer treats images as undistorted pinhole")

    sizes = set()
    for fr in frames:
        tag = f"frame {fr.index} ({fr.image_path.name})"
        M = fr.c2w_gl
        if not np.all(np.isfinite(M)):
            rep.errors.append(f"{tag}: non-finite values in transform_matrix")
            continue
        if not np.allclose(M[3], [0, 0, 0, 1], atol=1e-6):
            rep.errors.append(f"{tag}: last row of transform_matrix must be [0,0,0,1]")
        R = M[:3, :3]
        if np.abs(R.T @ R - np.eye(3)).max() > 1e-3 or abs(np.linalg.det(R) - 1) > 1e-3:
            rep.errors.append(f"{tag}: rotation is not orthonormal with det +1 (scaled or mirrored pose)")
        fx, fy, cx, cy = fr.K[0, 0], fr.K[1, 1], fr.K[0, 2], fr.K[1, 2]
        if not (0 < cx < fr.width and 0 < cy < fr.height):
            rep.errors.append(f"{tag}: principal point ({cx:.1f},{cy:.1f}) outside {fr.width}x{fr.height}")
        if not (0.25 < fx / fr.width < 5 and 0.9 < fx / fy < 1.1):
            rep.warnings.append(f"{tag}: unusual focal length fx={fx:.1f} fy={fy:.1f} for width {fr.width}")
        sizes.add((fr.width, fr.height))
        if check_files:
            if not fr.image_path.exists():
                rep.errors.append(f"{tag}: image file missing: {fr.image_path}")
            elif len(sizes) <= 3:  # spot-check the declared size against the real file
                from PIL import Image

                with Image.open(fr.image_path) as im:
                    if im.size != (fr.width, fr.height):
                        rep.errors.append(f"{tag}: image is {im.size[0]}x{im.size[1]} but manifest says "
                                          f"{fr.width}x{fr.height} (rescale intrinsics)")
    rep.stats["resolutions"] = sorted(sizes)
    if rep.errors:
        return rep

    c2ws = [fr.c2w_gl for fr in frames]
    from .scene import capture_mode

    rep.stats["capture mode"] = capture_mode(manifest)
    if capture_mode(manifest) == "scene":
        _validate_scene(rep, manifest, c2ws)
        return rep
    target, how = scene_target(manifest, manifest_path, c2ws)
    centers = np.array([c[:3, 3] for c in c2ws])
    dists = np.linalg.norm(centers - target, axis=1)
    rep.stats["look-at target"] = f"{np.round(target, 3).tolist()} ({how})"
    rep.stats["camera distance to target (min/median/max)"] = (
        f"{dists.min():.3f} / {np.median(dists):.3f} / {dists.max():.3f}")

    in_view = 0
    for fr in frames:
        uv, depth = project(target[None], viewmat_from_c2w_gl(fr.c2w_gl), fr.K)
        if depth[0] > 0 and 0 <= uv[0, 0] < fr.width and 0 <= uv[0, 1] < fr.height:
            in_view += 1
    frac = in_view / len(frames)
    rep.stats["frames seeing target"] = f"{in_view}/{len(frames)}"
    if frac < 0.5:
        rep.errors.append(f"target is visible in only {frac:.0%} of frames; poses are probably in the wrong "
                          "convention (expected OpenGL camera-to-world: camera looks down -z, y up)")
    elif frac < 0.9:
        rep.warnings.append(f"target visible in only {frac:.0%} of frames")

    # Angular coverage around the target (azimuth in the plane orthogonal to the mean up vector).
    up = np.mean([c[:3, 1] for c in c2ws], axis=0)
    up /= np.linalg.norm(up)
    rel = centers - target
    rel_flat = rel - np.outer(rel @ up, up)
    ref = rel_flat[np.argmax(np.linalg.norm(rel_flat, axis=1))]
    ref /= np.linalg.norm(ref)
    az = np.degrees(np.arctan2(rel_flat @ np.cross(up, ref), rel_flat @ ref)) % 360
    covered = len(np.unique((az // 30).astype(int) % 12))
    rep.stats["azimuth coverage (30deg bins)"] = f"{covered}/12"
    if covered < 9:
        rep.warnings.append(f"azimuth coverage only {covered}/12 bins of 30deg; orbit the object fully")
    return rep


def _validate_scene(rep: ValidationReport, manifest: dict, c2ws: list[np.ndarray]) -> None:
    """Room captures: no single target, but the lens must have moved (parallax) and looked all around."""
    from .scene import forward_gl, parallax_check, scene_info

    centers = np.array([c[:3, 3] for c in c2ws])
    errors, warnings, spread = parallax_check(centers)
    rep.errors += errors
    rep.warnings += warnings
    rep.stats["camera spread (m)"] = round(spread, 3)
    stations = scene_info(manifest).get("stations") or []
    rep.stats["stations"] = len(stations)
    if stations and len(stations) < 3:
        rep.warnings.append(f"only {len(stations)} station(s); 4-6 spots around the room give the best result")
    fwd = np.array([forward_gl(c) for c in c2ws])
    az = np.degrees(np.arctan2(fwd[:, 0], -fwd[:, 2])) % 360
    covered = len(np.unique((az // 30).astype(int) % 12))
    rep.stats["view directions (30deg bins)"] = f"{covered}/12"
    if covered < 9:
        rep.warnings.append(f"photos look in only {covered}/12 directions; turn all the way around")
