"""Pose refinement: COLMAP re-estimates poses + lens from the photos; AR supplies scale, gravity and pairs.

Why: on the first real captures ARCore poses were off by 4-13 cm and its focal length by 11%, giving
10-37 px epipolar error, while COLMAP on the same photos reaches < 1 px. AR is still used for
(1) choosing which photos to match, (2) the starting focal length, (3) metric scale + gravity-up via a
similarity fit, and (4) the fallback when COLMAP cannot register enough photos.

    python -m gs_pipeline refine <bundle>/manifest.json --out <refined_dir>
"""

import json
import shutil
import time
from pathlib import Path

import numpy as np

from .manifest import load_frames, read_manifest
from .scene import capture_mode, lookalike_pairs, select_pairs_scene, tiny_image
from .validate import scene_target

MIN_REGISTERED_FRACTION = 0.8
# Rooms: a single COLMAP model with most photos beats AR poses, which can contain a map jump (the second room's
# AR map jumped 16 m after a shake). Unplaced photos are simply left out of training.
MIN_REGISTERED_FRACTION_SCENE = 0.6


def relaxed_mapping_options(num_threads: int):
    """Looser registration for a second mapping pass on the same matches (no re-matching): photos with fewer,
    weaker matches - typical of night shots and blank walls - still get placed."""
    import pycolmap

    o = pycolmap.IncrementalPipelineOptions()
    o.num_threads = num_threads
    o.min_num_matches = 10
    o.mapper.abs_pose_min_num_inliers = 15
    o.mapper.abs_pose_min_inlier_ratio = 0.15
    o.mapper.init_min_num_inliers = 50
    if hasattr(o, "structure_less_registration_fallback"):
        o.structure_less_registration_fallback = True
    return o
MAX_REPROJ_PX = 1.5
CARRY_OVER = ("capture_id", "device", "object_bounds", "capture_mode")
FRAME_CARRY_OVER = ("target_id", "blur_score", "timestamp", "tracking_epoch", "anchor_id")  # per photo: which spot/sector, how sharp


# ---------------------------------------------------------------- pure helpers (unit-tested)

def select_pairs(names: list[str], centers: np.ndarray, target: np.ndarray, k_nearest: int = 12,
                 time_window: int = 2) -> list[tuple[str, str]]:
    """Image pairs worth matching: nearest viewing directions around the target + neighbours in time."""
    d = centers - target
    d /= np.linalg.norm(d, axis=1, keepdims=True)
    sim = d @ d.T
    pairs = set()
    n = len(names)
    for i in range(n):
        for j in np.argsort(-sim[i])[1:k_nearest + 1]:
            pairs.add((min(i, int(j)), max(i, int(j))))
        for j in range(i + 1, min(n, i + time_window + 1)):
            pairs.add((i, j))
    return [(names[i], names[j]) for i, j in sorted(pairs)]


def umeyama(src: np.ndarray, dst: np.ndarray) -> tuple[float, np.ndarray, np.ndarray]:
    """Similarity (s, R, t) minimising |s R src + t - dst|^2."""
    mu_s, mu_d = src.mean(0), dst.mean(0)
    a, b = src - mu_s, dst - mu_d
    U, D, Vt = np.linalg.svd(b.T @ a / len(src))
    S = np.eye(3)
    if np.linalg.det(U) * np.linalg.det(Vt) < 0:
        S[2, 2] = -1
    R = U @ S @ Vt
    s = float(np.trace(np.diag(D) @ S) / a.var(0).sum())
    return s, R, mu_d - s * R @ mu_s


def robust_similarity(src: np.ndarray, dst: np.ndarray, trim: float = 0.2):
    """Umeyama, then refit without the worst `trim` fraction. Returns (s, R, t, residuals_of_all)."""
    s, R, t = umeyama(src, dst)
    res = np.linalg.norm((s * (R @ src.T)).T + t - dst, axis=1)
    keep = res <= np.quantile(res, 1 - trim)
    if keep.sum() >= 3:
        s, R, t = umeyama(src[keep], dst[keep])
        res = np.linalg.norm((s * (R @ src.T)).T + t - dst, axis=1)
    return s, R, t, res


def ransac_similarity(src: np.ndarray, dst: np.ndarray, thresh: float = 0.5, iters: int = 400, seed: int = 0):
    """Similarity fitted to the largest group of cameras whose AR and COLMAP positions agree (within thresh m).
    A trimmed fit tolerates slow drift, but not a map jump: after a shake ARCore moved 40% of the second room's
    photos 16 m away, and every least-squares fit was dragged between the two groups.
    Returns (s, R, t, residuals_of_all, inlier_mask)."""
    n = len(src)
    if n < 6:
        s, R, t, res = robust_similarity(src, dst)
        return s, R, t, res, np.ones(n, bool)
    rng = np.random.default_rng(seed)
    best = np.zeros(n, bool)
    for _ in range(iters):
        idx = rng.choice(n, 3, replace=False)
        try:
            s, R, t = umeyama(src[idx], dst[idx])
        except np.linalg.LinAlgError:
            continue
        if not np.isfinite(s) or s <= 0:
            continue
        inl = np.linalg.norm((s * (R @ src.T)).T + t - dst, axis=1) < thresh
        if inl.sum() > best.sum():
            best = inl
    if best.sum() < 3:
        s, R, t, res = robust_similarity(src, dst)
        return s, R, t, res, np.ones(n, bool)
    s, R, t, _ = robust_similarity(src[best], dst[best], trim=0.1)
    res = np.linalg.norm((s * (R @ src.T)).T + t - dst, axis=1)
    return s, R, t, res, best


MIN_AR_CONSISTENT = 0.6  # below this, AR positions are too broken to align to (use gravity + scale instead)


def gravity_scale_similarity(R_cw: np.ndarray, centers: np.ndarray, ar_c2w_gl: np.ndarray, times: np.ndarray,
                             max_dt: float = 3.0):
    """COLMAP -> metric, gravity-up, using only what AR gets right even when its positions jump: the direction
    of gravity in every photo (ARCore never loses "down") and short hops between photos taken seconds apart.
    The second room (16 m map jump + ~1.5 m drift, only 30% of AR positions mutually consistent) came out
    tilted ~17 deg and twice too big when fitted to AR positions.
    R_cw: (n,3,3) COLMAP world->camera rotations; centers: (n,3) COLMAP camera centres; ar_c2w_gl: (n,4,4).
    Returns (s, R, t) with x_metric = s R x_colmap + t (cameras centred on the origin)."""
    from .postprocess import upright_rotation

    ups = []
    for Rc, A in zip(R_cw, ar_c2w_gl):
        up_cam_cv = (A[:3, :3].T @ np.array([0.0, 1.0, 0.0])) * np.array([1.0, -1.0, -1.0])  # GL -> CV camera axes
        ups.append(Rc.T @ up_cam_cv)
    ups = np.array(ups)
    g = np.median(ups, axis=0)
    g /= np.linalg.norm(g)
    R = upright_rotation(g)
    order = np.argsort(times)
    ratios = []
    for a, b in zip(order[:-1], order[1:]):
        if times[b] - times[a] > max_dt:
            continue
        d_ar = np.linalg.norm(ar_c2w_gl[a][:3, 3] - ar_c2w_gl[b][:3, 3])
        d_col = np.linalg.norm(centers[a] - centers[b])
        if d_ar > 0.05 and d_col > 1e-9:
            ratios.append(d_ar / d_col)
    if len(ratios) < 5:
        raise RuntimeError("too few short AR hops to recover the metric scale")
    sc = float(np.median(ratios))
    t = -sc * R @ np.median(centers, axis=0)
    return sc, R, t


def rebuild_scene_extension(refined: dict, points: np.ndarray | None) -> None:
    """The app's spot centres and floor height are in AR coordinates; after a gravity+scale alignment they no
    longer apply. Re-derive them from the refined cameras (per-photo target_id -> spot) and the sparse points."""
    sc = (refined.get("extensions") or {}).get("scene")
    if not sc:
        return
    from .scene import floor_from_points

    cams = np.array([np.asarray(f["transform_matrix"])[:3, 3] for f in refined["frames"]])
    floor = floor_from_points(points, float(np.median(cams[:, 1]))) if points is not None else None
    if floor is None:
        floor = float(np.median(cams[:, 1]) - 1.4)
    sc["floor_y"] = floor
    for k, st in enumerate(sc.get("stations", [])):
        mine = [i for i, f in enumerate(refined["frames"]) if 0 <= int(f.get("target_id", -1)) < 9000
                and int(f["target_id"]) // 100 == k]
        if mine:
            c = np.median(cams[mine], axis=0)
            st["center"] = [float(c[0]), floor, float(c[2])]
        else:
            st.pop("center", None)  # no placed photo of this spot: no reliable position


MERGE_EPOCH_JUMP_M = 1.0


def effective_epochs(manifest: dict) -> list[int]:
    """Per-frame tracking epochs, with needless splits merged back. The app starts a new epoch on every pose event
    it can't rule out; an early version also did so for ARCore's normal 15-70 cm map corrections, splitting one
    capture into 26 epochs. Consecutive photos (in time) whose AR positions differ by less than
    MERGE_EPOCH_JUMP_M across an epoch change are in the same map; a real map restart (16 m) stays split."""
    frames = manifest.get("frames", [])
    raw = [int(f.get("tracking_epoch", 0)) for f in frames]
    if len(set(raw)) <= 1:
        return raw
    order = sorted(range(len(frames)), key=lambda i: float(frames[i].get("timestamp", i)))
    pos = [np.asarray(f["transform_matrix"], float)[:3, 3] if "transform_matrix" in f else None for f in frames]
    group = {raw[order[0]]: 0}
    for a, b in zip(order[:-1], order[1:]):
        if raw[b] in group:
            continue
        same_map = (raw[b] != raw[a] and pos[a] is not None and pos[b] is not None
                    and np.linalg.norm(pos[b] - pos[a]) < MERGE_EPOCH_JUMP_M)
        group[raw[b]] = group[raw[a]] if same_map else len(set(group.values()))
    return [group[e] for e in raw]


def main_epoch_mask(names: list[str], manifest: dict) -> np.ndarray:
    """Photos of the largest tracking epoch (the app starts a new epoch after a pose jump it couldn't rule out)."""
    eff = effective_epochs(manifest)
    epochs = {Path(f["file_path"]).name: eff[i] for i, f in enumerate(manifest.get("frames", []))}
    e = np.array([epochs.get(nm, 0) for nm in names])
    vals, counts = np.unique(e, return_counts=True)
    return e == vals[np.argmax(counts)]


def carry_frame_fields(refined: dict, manifest: dict) -> None:
    """Copy per-photo app fields (spot/sector target_id, blur score, time) onto the refined frames, by file name."""
    orig = {Path(f["file_path"]).name: f for f in manifest.get("frames", [])}
    for f in refined.get("frames", []):
        o = orig.get(Path(f["file_path"]).name)
        if o:
            for k in FRAME_CARRY_OVER:
                if k in o:
                    f[k] = o[k]


def realign_bundle(refined_manifest_path, raw_manifest_path) -> dict:
    """Re-do only the metric/gravity alignment of an already refined bundle with gravity_scale_similarity (a
    refined bundle is the COLMAP model under one similarity, so no COLMAP re-run is needed). For bundles refined
    before the gravity+scale fallback existed, whose AR positions were broken by a map jump."""
    from .cameras import c2w_gl_to_cv
    from .ply_io import read_points_ply, write_points_ply

    refined_manifest_path = Path(refined_manifest_path)
    refined = read_manifest(refined_manifest_path)
    raw = read_manifest(raw_manifest_path)
    by_name = {Path(f["file_path"]).name: (i, f) for i, f in enumerate(raw["frames"])}
    names = [Path(f["file_path"]).name for f in refined["frames"]]
    c2w = np.array([np.asarray(f["transform_matrix"], float) for f in refined["frames"]])
    R_cw = np.array([c2w_gl_to_cv(c)[:3, :3].T for c in c2w])
    ar = np.array([np.asarray(by_name[n][1]["transform_matrix"], float) for n in names])
    times = np.array([float(by_name[n][1].get("timestamp", by_name[n][0])) for n in names])
    s, R, t = gravity_scale_similarity(R_cw, c2w[:, :3, 3], ar, times)
    for f, c in zip(refined["frames"], c2w):
        out = np.eye(4)
        out[:3, :3] = R @ c[:3, :3]
        out[:3, 3] = s * R @ c[:3, 3] + t
        f["transform_matrix"] = out.tolist()
    pp = refined_manifest_path.parent / refined.get("points3d_path", "points3d.ply")
    pts = None
    if pp.exists():
        xyz, rgb = read_points_ply(pp)
        pts = (s * (R @ xyz.T)).T + t
        write_points_ply(pp, pts, rgb)
    carry_frame_fields(refined, raw)
    rep = refined.setdefault("extensions", {}).setdefault("refine", {})
    rep.update(alignment="gravity+scale (realigned)", scale_realign=round(s, 5))
    rebuild_scene_extension(refined, pts)
    refined_manifest_path.write_text(json.dumps(refined, indent=1))
    return {"scale": s, "frames": len(names), "manifest": str(refined_manifest_path)}


# ---------------------------------------------------------------- pipeline

def _camera_center(image) -> np.ndarray:
    m = np.eye(4)
    m[:3, :4] = image.cam_from_world().matrix()
    return np.linalg.inv(m)[:3, 3]


def refine(manifest_path, out_dir, num_threads: int = 2, max_image_size: int = 1600,
           k_nearest: int = 12) -> dict:
    """Write a refined bundle to out_dir. Returns the report; report["manifest"] is the manifest to
    train on (the refined one, or the original AR manifest if refinement fell back)."""
    import pycolmap

    manifest_path = Path(manifest_path).resolve()
    bundle = manifest_path.parent
    out_dir = Path(out_dir).resolve()
    shutil.rmtree(out_dir, ignore_errors=True)
    work = out_dir / "_colmap"
    work.mkdir(parents=True)
    frames, manifest = load_frames(manifest_path)
    report = {"total_images": len(frames), "fallback": False}
    t0 = time.time()

    # Images must share one folder for COLMAP; names are relative to it.
    image_dir = frames[0].image_path.parent
    if any(f.image_path.parent != image_dir for f in frames):
        raise ValueError("refine expects all images in one folder")
    names = [f.image_path.name for f in frames]
    scene = capture_mode(manifest) == "scene"
    report["capture_mode"] = capture_mode(manifest)

    try:
        db = work / "database.db"
        reader = pycolmap.ImageReaderOptions()
        reader.camera_model = "SIMPLE_RADIAL"
        f0 = float(np.median([f.K[0, 0] for f in frames]))
        w, h = frames[0].width, frames[0].height
        reader.camera_params = f"{f0},{w / 2},{h / 2},0"  # AR focal as the starting guess
        ext = pycolmap.FeatureExtractionOptions()
        ext.num_threads, ext.max_image_size, ext.use_gpu = num_threads, max_image_size, False
        ext.sift.max_num_features = 8000 if scene else 6000
        pycolmap.extract_features(db, image_dir, image_names=names, camera_mode=pycolmap.CameraMode.SINGLE,
                                  reader_options=reader, extraction_options=ext, device=pycolmap.Device.cpu)
        report["t_features_s"] = round(time.time() - t0, 1)

        t1 = time.time()
        if scene:  # no single target: pair cameras that look at the same part of the room
            epochs = effective_epochs(manifest)
            report["epochs_effective"] = len(set(epochs))
            pairs = select_pairs_scene(names, [f.c2w_gl for f in frames], epochs=epochs)
            # + photos that look alike, whatever AR says (AR can jump after a shake)
            tinies = np.stack([tiny_image(f.image_path) for f in frames])
            pairs = sorted(set(pairs) | set(lookalike_pairs(names, tinies)))
        else:
            centers = np.array([f.c2w_gl[:3, 3] for f in frames])
            target, _ = scene_target(manifest, manifest_path, [f.c2w_gl for f in frames])
            pairs = select_pairs(names, centers, target, k_nearest=k_nearest)
        (work / "pairs.txt").write_text("\n".join(f"{a} {b}" for a, b in pairs))
        mo = pycolmap.FeatureMatchingOptions()
        mo.num_threads, mo.use_gpu = num_threads * 2, False
        po = pycolmap.ImportedPairingOptions()
        po.match_list_path = str(work / "pairs.txt")
        pycolmap.match_image_pairs(db, matching_options=mo, pairing_options=po, device=pycolmap.Device.cpu)
        report["pairs"] = len(pairs)
        report["t_matching_s"] = round(time.time() - t1, 1)

        t2 = time.time()
        recs = pycolmap.incremental_mapping(db, image_dir, work / "sparse")
        report["t_mapping_s"] = round(time.time() - t2, 1)
        if not recs:
            raise RuntimeError("COLMAP produced no model")
        rec = max(recs.values(), key=lambda r: r.num_reg_images())
        min_fraction = MIN_REGISTERED_FRACTION_SCENE if scene else MIN_REGISTERED_FRACTION
        if rec.num_reg_images() < MIN_REGISTERED_FRACTION * len(frames):
            t2b = time.time()
            relaxed = pycolmap.incremental_mapping(db, image_dir, work / "sparse_relaxed",
                                                   options=relaxed_mapping_options(num_threads))
            report["t_mapping_relaxed_s"] = round(time.time() - t2b, 1)
            if relaxed:
                best = max(relaxed.values(), key=lambda r: r.num_reg_images())
                report["registered_first_pass"] = rec.num_reg_images()
                if best.num_reg_images() > rec.num_reg_images():
                    rec = best

        reg = rec.num_reg_images()
        reproj = float(rec.compute_mean_reprojection_error())
        cam = next(iter(rec.cameras.values()))
        report.update(registered=reg, reproj_px=round(reproj, 3), points=rec.num_points3D(),
                      focal_ar_px=round(f0, 1), focal_colmap_px=round(float(cam.params[0]), 1),
                      radial_k=round(float(cam.params[3]), 5))
        if reg < min_fraction * len(frames) or reproj > MAX_REPROJ_PX:
            raise RuntimeError(f"quality gate failed: {reg}/{len(frames)} registered, {reproj:.2f} px")

        # COLMAP world -> AR world (metres, gravity-up), fitted on camera centres.
        by_name = {f.image_path.name: f for f in frames}
        src = np.array([_camera_center(im) for im in rec.images.values()])
        dst = np.array([by_name[im.name].c2w_gl[:3, 3] for im in rec.images.values()])
        reg_names = [im.name for im in rec.images.values()]
        main = main_epoch_mask(reg_names, manifest)
        s, R, t, _, inl = ransac_similarity(src[main], dst[main])
        res = np.linalg.norm((s * (R @ src.T)).T + t - dst, axis=1)
        report["ar_consistent_fraction"] = round(float(inl.sum()) / max(1, len(inl)), 3)
        report["epochs"] = int(len(set(int(f.get("tracking_epoch", 0)) for f in manifest.get("frames", []))))
        report["alignment"] = "ar-positions"
        if report["ar_consistent_fraction"] < MIN_AR_CONSISTENT:
            times = {Path(f["file_path"]).name: float(f.get("timestamp", i)) for i, f in enumerate(manifest["frames"])}
            R_cw = np.array([im.cam_from_world().matrix()[:3, :3] for im in rec.images.values()])
            ar = np.array([by_name[n].c2w_gl for n in reg_names])
            s, R, t = gravity_scale_similarity(R_cw, src, ar, np.array([times[n] for n in reg_names]))
            report["alignment"] = "gravity+scale"
        rec.transform(pycolmap.Sim3d(np.c_[s * R, t]))
        report.update(ar_drift_cm={"median": round(100 * float(np.median(res)), 1),
                                   "p90": round(100 * float(np.percentile(res, 90)), 1),
                                   "max": round(100 * float(res.max()), 1)},
                      scale_colmap_to_m=round(s, 5))
        aligned = work / "aligned"
        aligned.mkdir()
        rec.write_binary(str(aligned))

        # Undistort to PINHOLE (the trainer's model), then reuse the COLMAP -> manifest converter.
        t3 = time.time()
        pycolmap.undistort_images(out_dir / "undistorted", aligned, image_dir)
        from .colmap_io import convert_colmap

        und = out_dir / "undistorted"
        convert_colmap(und, images_subdir="images", sparse_subdir="sparse")
        shutil.move(str(und / "images"), str(out_dir / "images"))
        shutil.move(str(und / "points3d.ply"), str(out_dir / "points3d.ply"))
        refined = read_manifest(und / "manifest.json")
        report["t_undistort_s"] = round(time.time() - t3, 1)
    except Exception as exc:  # noqa: BLE001 - any failure means: train on the AR poses instead
        report.update(fallback=True, error=f"{type(exc).__name__}: {exc}")
        report["manifest"] = str(manifest_path)
        report["t_total_s"] = round(time.time() - t0, 1)
        (out_dir / "refine_report.json").write_text(json.dumps(report, indent=2))
        return report

    for key in CARRY_OVER:
        if key in manifest:
            refined[key] = manifest[key]
    carry_frame_fields(refined, manifest)
    refined["source"] = "arcore+colmap" if manifest.get("source") in ("arcore", "arkit") else "colmap"
    report["t_total_s"] = round(time.time() - t0, 1)
    refined.setdefault("extensions", {})
    refined["extensions"].update(manifest.get("extensions", {}))
    refined["extensions"]["refine"] = report
    if report.get("alignment") == "gravity+scale":
        from .ply_io import read_points_ply

        pts = read_points_ply(out_dir / "points3d.ply")[0] if (out_dir / "points3d.ply").exists() else None
        rebuild_scene_extension(refined, pts)
    report["manifest"] = str(out_dir / "manifest.json")
    (out_dir / "manifest.json").write_text(json.dumps(refined, indent=1))
    (out_dir / "refine_report.json").write_text(json.dumps(report, indent=2))
    shutil.rmtree(und, ignore_errors=True)
    shutil.rmtree(work, ignore_errors=True)
    return report
