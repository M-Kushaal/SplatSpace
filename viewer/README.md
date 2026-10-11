# SplatSpace viewer — modular Gaussian splat viewer

A self-contained web viewer for 3D Gaussian splats (phone + desktop). No build step, no internet needed:
everything it uses is vendored and pinned in `vendor/`.

- Rendering: [Spark](https://sparkjs.dev) 2.3.1 (MIT, World Labs) on [three.js](https://threejs.org) 0.180.0 (MIT).
- Formats: `.ply` (incl. SH3 view-dependent colour and SuperSplat-compressed), `.spz`, `.splat`, `.ksplat`, `.sog`.
- Font: Inter (SIL OFL, `fonts/`).

## Open it
- **Through the backend** (normal use): `http://<server>:8000/viewer/` — the backend mounts this folder at `/viewer`
  (`backend/app/main.py`, setting `GS_SERVE_VIEWER`). The phone app's **View 3D** button opens
  `/viewer/?url=<signed splat URL>&metrics=…&refine=…&name=…`.
- **Standalone**: serve this folder with any static server (ES modules need http, not `file://`), e.g.
  `python -m http.server 8080` here, then open `http://localhost:8080/` and drop a file in.

## URL parameters
| Param | Meaning |
|---|---|
| `url` | splat file to load (same origin, or a server that allows CORS) |
| `metrics` | optional training `metrics.json` (PSNR, time, steps…) shown under **Details** |
| `refine` | optional `refine_report.json` (photos placed, pose accuracy, AR drift) |
| `name` | title shown in the top bar |
| `up=-y` | file is stored upside down (e.g. raw COLMAP/INRIA output); also fixable with **Flip** |
| `mode=scene` / `mode=object` | force walk-through (room) or orbit (object) mode; default: `capture_mode` in `metrics` |
| `debug` | prints live state (splats, fps, bounds, camera) — used by the automated screenshot test |

You can also drop a splat together with a `metrics.json` / `refine_report.json`.

## What it does
- Smooth orbit with inertia, zoom to cursor, pan, **double-tap / double-click to glide to a spot**, Reset / Frame,
  turntable, keyboard (R, F, Space, arrows, +/−, U, I, O, ?).
- Auto-framing on the **body** of the splat (2nd–98th percentile of splat centres), so stray floaters don't
  shrink the object on screen; eased intro camera move, splats appear instantly.
- Details: real-world size (W × D × H), splat count, SH degree, file size, training + pose quality, live FPS/GPU.
- Adaptive resolution keeps it smooth on phones; Quality: Auto / Sharp / Battery. Background: Dark / Studio / Black.
- Rendering quality: full SH3 view-dependent colour, **extended-precision splat data** (`extSplats` + `accumExtSplats`),
  on-screen splat size capped (`maxPixelRadius` 320, no close-up spikes), 0.3 px low-pass (matches gsplat's training
  default), re-sort after any 1 mm / ~2.6° camera change, near plane scaled to the object, no tone mapping.
- Renders on demand: smooth adaptive resolution while moving, then a full-resolution frame and **idle** when still
  (saves battery). Brightness control for dark or bright captures.
- **Rooms (scene captures)** open in first-person **walk mode** at eye height (1.2 m; held keys walk at 1.2 m/s), at the spot the pipeline suggests
  (`metrics.splat_object.view`): drag to look around, pinch / wheel / W S to walk, two fingers / A D to step sideways,
  Q E down/up, double-tap to glide to a spot. **Dollhouse** shows the whole room from above (orbit mode).
- Floater/haze cleanup happens in the pipeline (`gs_pipeline/postprocess.py: floater_mask`), not the viewer.

## Swapping it out
The viewer is a leaf module: nothing imports it. To replace it, swap this folder for another viewer that accepts
the same `?url=` parameter (optionally `metrics`, `refine`, `name`). Nothing else in the repo needs to change.

## Tests
`node --test tests` — pure modules (framing math, PLY header parsing, formatting, URL params).
Set `GS_SAMPLE_PLY=<path to a trained .ply>` to also check a real file.
