// GS Viewer: modular Gaussian-splat viewer (Spark + three.js). Self-contained: open with ?url=<splat>
// [&metrics=<metrics.json>&refine=<refine_report.json>&name=<title>&up=-y&mode=scene|object&debug], or drop /
// pick a file. Scene (room) captures open in first-person walk mode; objects in orbit mode.
import * as THREE from "three";
import { SplatMesh } from "@sparkjsdev/spark";
import { Stage, makeGroundRing } from "./stage.js";
import { CameraRig, onDoubleTap } from "./camera.js";
import { robustBounds, orbitPosition, isScene, walkStart } from "./framing.js";
import { parsePlyHeader, fileKind } from "./plyinfo.js";
import * as fmt from "./format.js";
import { fetchBytes, remoteSize, sortFiles, readJsonFiles, fetchJson, nameFromUrl } from "./sources.js";

const $ = (id) => document.getElementById(id);
const params = fmt.parseParams(location.search);
const IS_TOUCH = matchMedia("(pointer: coarse)").matches;
const MOBILE_LIMIT = 300 * 1024 * 1024;

const ORIENTS = [
  new THREE.Quaternion(),
  new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), Math.PI),
  new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), Math.PI / 2),
  new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), -Math.PI / 2),
];

const state = {
  mesh: null, ground: null, localCenters: null, bounds: null, orient: 0,
  file: null, // { name, size, kind, header, loadSeconds }
  metrics: null, refine: null, status: "idle", fadeT0: 0, error: null,
  scene: false, // room capture: walk-through instead of orbit
};

// ---------------------------------------------------------------- settings (per device)
const settings = loadSettings();
function loadSettings() {
  const d = { background: "dark", quality: "auto", ground: true, sh: true, brightness: 1 };
  try {
    return { ...d, ...JSON.parse(localStorage.getItem("gsviewer.settings") || "{}") };
  } catch {
    return d;
  }
}
function saveSettings() {
  try {
    localStorage.setItem("gsviewer.settings", JSON.stringify(settings));
  } catch { /* private mode */ }
}
function orientKey() {
  return state.file ? `gsviewer.orient:${state.file.name}:${state.file.size}` : null;
}

// ---------------------------------------------------------------- UI helpers
function toast(text, color = "var(--accent)", ms = 2600) {
  const el = document.createElement("div");
  el.className = "toast";
  el.style.setProperty("--dot", color);
  el.textContent = text;
  $("toasts").appendChild(el);
  setTimeout(() => el.classList.add("out"), ms);
  setTimeout(() => el.remove(), ms + 450);
}

function showEmpty(title, text, { anyway = null } = {}) {
  $("loading").hidden = true;
  $("empty").hidden = false;
  $("empty-title").textContent = title;
  $("empty-text").innerHTML = text;
  $("btn-anyway").hidden = !anyway;
  $("btn-anyway").onclick = anyway;
}

function setLoading(pct, label, name) {
  $("empty").hidden = true;
  $("loading").hidden = false;
  const ring = $("ring-fill").parentElement;
  ring.classList.toggle("spin", pct < 0);
  $("ring-fill").style.strokeDashoffset = String(169.6 * (1 - Math.max(0, pct)));
  $("loading-pct").textContent = pct < 0 ? "" : `${Math.round(pct * 100)}%`;
  $("loading-label").textContent = label;
  if (name !== undefined) $("loading-name").textContent = name;
}

function setTitle(name, sub) {
  $("title").textContent = name || "GS Viewer";
  $("subtitle").textContent = sub || "Gaussian splat viewer";
  document.title = name ? `${name} · GS Viewer` : "GS Viewer";
}

function showHint() {
  const hint = $("hint");
  if (rig.walking)
    hint.textContent = IS_TOUCH
      ? "Drag to look around · Pinch to walk · Two fingers to step sideways · Double-tap to go there"
      : "Drag to look around · Scroll or W/S to walk · A/D sideways · Q/E up/down · Double-click to go there";
  else hint.textContent = IS_TOUCH
    ? "Drag to orbit · Pinch to zoom · Double-tap to focus"
    : "Drag to orbit · Scroll to zoom · Right-drag to pan · Double-click to focus";
  hint.hidden = false;
  hint.classList.remove("fade");
  setTimeout(() => hint.classList.add("fade"), 4200);
  setTimeout(() => (hint.hidden = true), 5200);
}

function togglePanel(id, force) {
  const el = $(id);
  const open = force ?? el.hidden;
  for (const other of ["panel", "settings"]) if (other !== id) $(other).hidden = true;
  el.hidden = !open;
  $("btn-info").classList.toggle("active", !$("panel").hidden);
  $("btn-settings").classList.toggle("active", !$("settings").hidden);
  if (id === "panel" && open) renderPanel();
  requestAnimationFrame(updateViewOffset);
}

/** On phones the panels are bottom sheets: shift the 3D view up so the object stays visible above them. */
function updateViewOffset() {
  const sheet = ["panel", "settings"].map($).find((el) => !el.hidden);
  const r = sheet?.getBoundingClientRect();
  const isSheet = r && r.left < innerWidth * 0.2 && r.top > innerHeight * 0.3;
  if (isSheet) stage.camera.setViewOffset(innerWidth, innerHeight, 0, (innerHeight - r.top) * 0.5, innerWidth, innerHeight);
  else stage.camera.clearViewOffset();
}
addEventListener("resize", () => requestAnimationFrame(updateViewOffset));

// ---------------------------------------------------------------- stage
if (!document.createElement("canvas").getContext("webgl2")) {
  showEmpty("WebGL 2 is not available", "This browser or device can't render Gaussian splats. Try an up-to-date Chrome, Edge or Safari.");
  throw new Error("WebGL2 unavailable");
}
const stage = new Stage($("stage"));
const rig = new CameraRig(stage.camera, $("stage"), {
  onUserInput: () => $("hint").classList.add("fade"),
});
rig.onSpinChange = (on) => $("tool-spin").setAttribute("aria-pressed", String(on));
stage.camera.position.set(0, 0.5, 2);
applySettings();

function applySettings() {
  document.body.className = `bg-${settings.background}`;
  stage.setQuality(settings.quality);
  for (const seg of document.querySelectorAll(".seg")) {
    const key = seg.dataset.setting;
    const value = key === "orient" ? String(state.orient) : String(settings[key]);
    for (const b of seg.querySelectorAll("button")) b.classList.toggle("on", b.dataset.value === value);
  }
  $("opt-ground").checked = settings.ground;
  $("opt-sh").checked = settings.sh;
  if (state.ground) state.ground.visible = settings.ground && !state.scene; // a room has its own floor
  $("opt-brightness").value = String(settings.brightness);
  $("brightness-value").textContent = `${Math.round(settings.brightness * 100)}%`;
  if (state.mesh) {
    state.mesh.maxSh = settings.sh ? 3 : 0;
    state.mesh.recolor.setScalar(settings.brightness);
    state.mesh.updateGenerator();
  }
  stage.wake();
}

// ---------------------------------------------------------------- loading
async function loadFromUrl(url, name) {
  name = name || nameFromUrl(url);
  try {
    if (IS_TOUCH && !params._anyway) {
      const size = await remoteSize(url);
      if (size > MOBILE_LIMIT) {
        showEmpty("This file is very large", `${fmt.bytes(size)} may be too much for a phone. The object-only splat is much smaller.`, {
          anyway: () => { params._anyway = true; loadFromUrl(url, name); },
        });
        return;
      }
    }
    setLoading(0, "Downloading…", name);
    const t0 = performance.now();
    const bytes = await fetchBytes(url, (p, got, total) =>
      setLoading(p, total ? `Downloading ${fmt.bytes(got)} of ${fmt.bytes(total)}` : `Downloading ${fmt.bytes(got)}`));
    const [metrics, refine] = await Promise.all([fetchJson(params.metrics), fetchJson(params.refine)]);
    await loadBytes(bytes, name, { metrics, refine, t0, kind: fileKind(url) || fileKind(name) });
  } catch (e) {
    fail(e);
  }
}

async function loadFile(file, jsonFiles = []) {
  try {
    const t0 = performance.now();
    setLoading(-1, "Reading file…", file.name);
    const bytes = new Uint8Array(await file.arrayBuffer());
    const extra = await readJsonFiles(jsonFiles);
    await loadBytes(bytes, file.name, { ...extra, t0, kind: fileKind(file.name) });
  } catch (e) {
    fail(e);
  }
}

async function loadBytes(bytes, name, { metrics = null, refine = null, t0 = performance.now(), kind = null } = {}) {
  state.status = "decoding";
  setLoading(-1, "Preparing splats…", name);
  const header = kind === "ply" || !kind ? parsePlyHeader(bytes.subarray(0, 64 * 1024)) : null;
  if (header && !header.isGaussian)
    throw new Error("This PLY is a point cloud or mesh, not a Gaussian splat (no opacity/scale/rotation).");

  if (state.mesh) {
    stage.scene.remove(state.mesh);
    state.mesh.dispose?.();
  }
  let mesh;
  try {
    mesh = new SplatMesh({ fileBytes: bytes, fileName: name, extSplats: true }); // full precision (colour, SH)
    await mesh.initialized;
  } catch (e) {
    console.warn("extended-precision load failed, using packed splats", e);
    mesh = new SplatMesh({ fileBytes: bytes, fileName: name });
    await mesh.initialized;
  }
  stage.scene.add(mesh);

  state.mesh = mesh;
  state.file = { name, size: bytes.length, kind: kind || "ply", header, loadSeconds: (performance.now() - t0) / 1000 };
  state.metrics = metrics;
  state.refine = refine;
  state.localCenters = sampleCenters(mesh);

  let orient = params.up === "-y" ? 1 : 0;
  try {
    const saved = localStorage.getItem(orientKey());
    if (saved !== null) orient = Number(saved);
  } catch { /* ignore */ }
  state.scene = isScene(metrics, params.mode);
  rig.exitWalk();
  setOrientation(orient, { animate: false });
  applySettings();
  $("tool-walk").hidden = !state.scene;

  const b = state.bounds;
  if (state.scene) {
    // Rooms: show the whole room from above for a moment, then glide inside to eye height.
    rig.jump(orbitPosition(b.center, rig.frameDistance() * 1.1, 20, 60), b.center);
    startWalk(1.8);
  } else {
    // Intro: start a little further out and higher, glide into the home view while splats fade in.
    rig.jump(orbitPosition(b.center, rig.frameDistance() * 1.6, 5, 34), b.center);
    rig.frame({ seconds: 1.3 });
  }
  stage.wake();
  state.status = "ready";

  $("loading").hidden = true;
  $("empty").hidden = true;
  $("toolbar").hidden = false;
  const sub = [fmt.count(mesh.numSplats) + " splats", fmt.bytes(bytes.length), header?.shDegree != null ? `SH${header.shDegree}` : null]
    .filter(Boolean).join(" · ");
  setTitle(params.name || prettyName(name), sub);
  showHint();
  if (!$("panel").hidden) renderPanel();
}

/** Walk-through start: the pipeline's suggested spot (metrics.splat_object.view) or the room's middle. */
function startWalk(seconds = 0.9) {
  const s = walkStart(state.bounds, state.metrics?.splat_object?.view);
  rig.enterWalk(s.eye, s.target, seconds);
  updateWalkButton();
}

function toggleWalk() {
  if (!state.scene) return;
  if (rig.walking) {
    rig.exitWalk();
    rig.frame({ seconds: 1.0, azimuth: 30, elevation: 55, scale: 0.9 }); // "dollhouse": the room from above
  } else startWalk();
  updateWalkButton();
  showHint();
}

function updateWalkButton() {
  $("tool-walk").querySelector("span").textContent = rig.walking ? "Dollhouse" : "Walk in";
  $("tool-walk").setAttribute("aria-pressed", String(rig.walking));
}

function prettyName(name) {
  return name.replace(/\.[a-z0-9]+$/i, "").replace(/[_-]+/g, " ").replace(/\s+\(\d+\)$/, "").trim() || name;
}

function fail(e) {
  console.error(e);
  state.status = "error";
  state.error = String(e?.message || e);
  showEmpty("Couldn't open this splat", `${escapeHtml(state.error)}<br>Try another file, or drop it here.`);
}

function escapeHtml(s) {
  return s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
}

/** Splat centres (mesh-local), subsampled to <= 300k, for robust framing. */
function sampleCenters(mesh) {
  const n = mesh.numSplats;
  const stride = Math.max(1, Math.ceil(n / 300000));
  const out = new Float32Array(Math.ceil(n / stride) * 3);
  let k = 0;
  mesh.forEachSplat((i, c) => {
    if (i % stride === 0) {
      out[k++] = c.x;
      out[k++] = c.y;
      out[k++] = c.z;
    }
  });
  return out.subarray(0, k);
}

/** Rotate the splat (0 upright, 1 flipped, 2/3 tilted) and recompute framing in world space. */
function setOrientation(o, { animate = true } = {}) {
  state.orient = o;
  const q = ORIENTS[o];
  state.mesh.quaternion.copy(q);
  state.mesh.updateMatrixWorld(true);
  const local = state.localCenters;
  const world = new Float32Array(local.length);
  const v = new THREE.Vector3();
  for (let i = 0; i < local.length; i += 3) {
    v.set(local[i], local[i + 1], local[i + 2]).applyQuaternion(q);
    world[i] = v.x;
    world[i + 1] = v.y;
    world[i + 2] = v.z;
  }
  state.bounds = robustBounds(world);
  rig.setBounds(state.bounds);
  placeGround();
  if (rig.walking) animate = false; // keep the walker where they are
  try {
    localStorage.setItem(orientKey(), String(o));
  } catch { /* ignore */ }
  applySettings();
  if (animate) rig.frame({ seconds: 0.8 });
  if (!$("panel").hidden) renderPanel();
}

function placeGround() {
  if (state.ground) stage.scene.remove(state.ground);
  const b = state.bounds;
  const r = Math.max(b.size[0], b.size[2]) * 0.62 + b.radius * 0.08;
  state.ground = makeGroundRing(r);
  state.ground.position.set(b.center[0], b.min[1], b.center[2]);
  state.ground.visible = settings.ground && !state.scene;
  stage.scene.add(state.ground);
}

// ---------------------------------------------------------------- details panel
function kv(k, v, cls = "", id = "") {
  return `<div class="kv"><span class="k">${k}</span><span class="v ${cls}"${id ? ` id="${id}"` : ""}>${v}</span></div>`;
}

function grade(x, good = 25, ok = 18) {
  return !(x > 0) ? "" : x >= good ? "good" : x >= ok ? "warn" : "bad";
}

function renderPanel() {
  const f = state.file;
  if (!f) {
    $("panel-body").innerHTML = `<p class="note">Open a splat to see its details.</p>`;
    return;
  }
  const h = f.header;
  const format = f.kind.toUpperCase() + (h?.compressed ? " · compressed" : h?.shDegree != null ? ` · SH degree ${h.shDegree}` : "");
  const b = state.bounds;
  const metric = !!(state.metrics || state.refine || params.url);
  let html = `<div class="section"><h3>${state.scene ? "Room" : "Object"}</h3>
      <div class="big-dims">${fmt.length(b.size[0], metric)} × ${fmt.length(b.size[2], metric)} × ${fmt.length(b.size[1], metric)}</div>
      <div class="dims-caption">width × depth × height of the ${state.scene ? "captured space" : "main body"} (96% of splats)${metric ? "" : " · assumes the file is in metres"}</div>
    </div>
    <div class="section"><h3>Model</h3>
      ${kv("File", escapeHtml(f.name))}
      ${kv("Format", format)}
      ${kv("Splats", fmt.count(state.mesh.numSplats))}
      ${kv("Size", fmt.bytes(f.size))}
      ${kv("Opened in", `${f.loadSeconds.toFixed(1)} s`)}
    </div>`;

  const m = state.metrics;
  if (m) {
    const po = m.psnr_object_mean, pt = m.psnr_test_mean;
    html += `<div class="section"><h3>Training</h3>
      ${po ? kv("Object quality", fmt.db(po), grade(po)) : ""}
      ${pt ? kv("Whole-image quality", fmt.db(pt), grade(pt)) : ""}
      ${m.train_seconds ? kv("Training time", fmt.duration(m.train_seconds)) : ""}
      ${m.steps ? kv("Steps", fmt.count(m.steps)) : ""}
      ${m.num_gaussians ? kv("Gaussians trained", fmt.count(m.num_gaussians)) : ""}
      ${m.device ? kv("Trained on", escapeHtml(String(m.device))) : ""}
      ${m.dry_run ? `<p class="note">This was a CPU test run (fake training) — not a real reconstruction.</p>` : ""}
      <p class="note">Quality = PSNR on photos held out from training: ≥ 25 dB is good, 18–25 fair. Shiny objects score lower.</p>
    </div>`;
  }
  const r = state.refine || m?.refine;
  if (r) {
    const drift = r.ar_drift_cm?.median;
    html += `<div class="section"><h3>Capture &amp; poses</h3>
      ${r.registered ? kv("Photos placed", `${r.registered} / ${r.total_images}`, r.registered >= 0.95 * r.total_images ? "good" : "warn") : ""}
      ${r.reproj_px ? kv("Pose accuracy", `${r.reproj_px.toFixed(2)} px`, r.reproj_px <= 1 ? "good" : r.reproj_px <= 2 ? "warn" : "bad") : ""}
      ${drift != null ? kv("Phone AR drift", `${drift} cm (max ${r.ar_drift_cm.max} cm)`) : ""}
      ${kv("Poses from", r.fallback ? "phone AR (refinement failed)" : "COLMAP refinement", r.fallback ? "warn" : "")}
    </div>`;
  }
  html += `<div class="section"><h3>Performance</h3>
      ${kv("Frame rate", "–", "", "perf-fps")}
      ${kv("Resolution", "–", "", "perf-res")}
      ${kv("Quality", settings.quality === "auto" ? "Auto (adapts to keep it smooth)" : settings.quality === "high" ? "Sharp" : "Battery saver")}
      ${kv("GPU", escapeHtml(stage.gpuName.replace(/^ANGLE \((.*)\)$/, "$1")).slice(0, 60))}
    </div>`;
  $("panel-body").innerHTML = html;
  updatePerf();
  stage.wake();
}

function updatePerf() {
  const fps = $("perf-fps");
  if (!fps) return;
  fps.textContent = stage.idle ? "idle (view still)" : stage.fps ? `${Math.round(stage.fps)} fps` : "–";
  fps.className = `v ${stage.idle || stage.fps >= 50 ? "good" : stage.fps >= 28 ? "warn" : "bad"}`;
  const s = stage.renderer.getDrawingBufferSize(new THREE.Vector2());
  $("perf-res").textContent = `${s.x} × ${s.y} (${stage.ratio.toFixed(2)}×)`;
}

// ---------------------------------------------------------------- wiring
$("btn-open").onclick = $("btn-choose").onclick = () => $("file-input").click();
$("file-input").onchange = (e) => {
  const { splat, json } = sortFiles(e.target.files);
  if (splat) loadFile(splat, json);
  else if (json.length && state.mesh) readJsonFiles(json).then((x) => { Object.assign(state, x); renderPanel(); toast("Details added"); });
  else toast("That isn't a splat file (.ply, .spz, .splat, .ksplat, .sog)", "var(--warning)");
  e.target.value = "";
};
$("btn-info").onclick = () => togglePanel("panel");
$("panel-close").onclick = () => togglePanel("panel", false);
$("btn-settings").onclick = () => togglePanel("settings");
$("settings-close").onclick = () => togglePanel("settings", false);
$("btn-help").onclick = () => ($("help").hidden = !$("help").hidden);
$("help-close").onclick = () => ($("help").hidden = true);
$("btn-fullscreen").onclick = () => {
  if (document.fullscreenElement) document.exitFullscreen();
  else document.documentElement.requestFullscreen?.().catch(() => toast("Fullscreen isn't available here", "var(--warning)"));
};
$("tool-reset").onclick = () => (rig.walking ? startWalk() : rig.frame());
$("tool-frame").onclick = () => (rig.walking ? toggleWalk() : rig.refit());
$("tool-walk").onclick = toggleWalk;
$("tool-spin").onclick = () => rig.setSpin(!rig.controls.autoRotate);
$("tool-flip").onclick = () => state.mesh && setOrientation(state.orient === 1 ? 0 : 1);

for (const seg of document.querySelectorAll(".seg")) {
  seg.addEventListener("click", (e) => {
    const v = e.target.closest("button")?.dataset.value;
    if (v == null) return;
    const key = seg.dataset.setting;
    if (key === "orient") {
      if (state.mesh) setOrientation(Number(v));
      return;
    }
    settings[key] = v;
    saveSettings();
    applySettings();
    if (!$("panel").hidden) renderPanel();
  });
}
$("opt-ground").onchange = (e) => { settings.ground = e.target.checked; saveSettings(); applySettings(); };
$("opt-sh").onchange = (e) => { settings.sh = e.target.checked; saveSettings(); applySettings(); };
$("opt-brightness").oninput = (e) => { settings.brightness = Number(e.target.value); applySettings(); };
$("opt-brightness").onchange = () => saveSettings();
$("brightness-reset").onclick = () => { settings.brightness = 1; saveSettings(); applySettings(); };

onDoubleTap($("stage"), (x, y) => {
  if (!state.mesh) return;
  const ndc = new THREE.Vector2((x / innerWidth) * 2 - 1, -(y / innerHeight) * 2 + 1);
  const ray = new THREE.Raycaster();
  ray.setFromCamera(ndc, stage.camera);
  const hits = ray.intersectObject(state.mesh, false);
  state.lastHit = hits.length ? hits[0].point.toArray().map((x) => +x.toFixed(4)) : null;
  if (hits.length) {
    if (rig.walking) rig.walkTo(hits[0].point.toArray());
    else rig.focusOn(hits[0].point.toArray());
  } else if (!rig.walking) rig.refit();
});

addEventListener("keydown", (e) => {
  if (e.target.closest?.("input")) return;
  const k = e.key;
  if (k === "o" || k === "O") $("file-input").click();
  if (!state.mesh) return;
  const step = 0.18;
  if (rig.walking) {
    if (setWalkKey(k, 1)) return; // held W/A/S/D/Q/E: smooth walking at WALK_SPEED
    const m = { "+": [0.35, 0, 0], "=": [0.35, 0, 0], "-": [-0.35, 0, 0], _: [-0.35, 0, 0] }[k];
    if (m) { rig.walkBy(...m); return; }
    if (k === "r" || k === "R") { startWalk(); return; }
    if (k === "f" || k === "F") { toggleWalk(); return; }
  }
  if (k === "w" || k === "W") toggleWalk();
  else if (k === "r" || k === "R") rig.frame();
  else if (k === "f" || k === "F") rig.refit();
  else if (k === " ") { e.preventDefault(); rig.setSpin(!rig.controls.autoRotate); }
  else if (k === "ArrowLeft") rig.orbitBy(-step, 0);
  else if (k === "ArrowRight") rig.orbitBy(step, 0);
  else if (k === "ArrowUp") rig.orbitBy(0, -step);
  else if (k === "ArrowDown") rig.orbitBy(0, step);
  else if (k === "+" || k === "=") rig.zoomBy(0.8);
  else if (k === "-" || k === "_") rig.zoomBy(1.25);
  else if (k === "u" || k === "U") setOrientation(state.orient === 1 ? 0 : 1);
  else if (k === "i" || k === "I") togglePanel("panel");
  else if (k === "?") $("help").hidden = !$("help").hidden;
  else if (k === "Escape") { togglePanel("panel", false); togglePanel("settings", false); $("help").hidden = true; }
});

/** W/S forward-back, A/D sideways, Q/E down-up; returns true if `key` is a walk key. */
function setWalkKey(key, on) {
  const axis = { w: ["forward", 1], s: ["forward", -1], d: ["right", 1], a: ["right", -1], e: ["up", 1], q: ["up", -1] }[key.toLowerCase()];
  if (!axis) return false;
  const [name, sign] = axis;
  const cur = rig.walkInput[name];
  if (on) rig.walkInput[name] = sign;
  else if (cur === sign) rig.walkInput[name] = 0;
  stage.wake();
  return true;
}
addEventListener("keyup", (e) => setWalkKey(e.key, 0));
addEventListener("blur", () => Object.assign(rig.walkInput, { forward: 0, right: 0, up: 0 })); // no stuck keys

let dragDepth = 0;
addEventListener("dragenter", (e) => { e.preventDefault(); dragDepth++; $("dropzone").hidden = false; });
addEventListener("dragleave", () => { if (--dragDepth <= 0) { dragDepth = 0; $("dropzone").hidden = true; } });
addEventListener("dragover", (e) => e.preventDefault());
addEventListener("drop", (e) => {
  e.preventDefault();
  dragDepth = 0;
  $("dropzone").hidden = true;
  const { splat, json } = sortFiles(e.dataTransfer.files);
  if (splat) loadFile(splat, json);
  else if (json.length && state.mesh) readJsonFiles(json).then((x) => { Object.assign(state, x); renderPanel(); toast("Details added"); });
  else toast("Drop a .ply, .spz, .splat, .ksplat or .sog file", "var(--warning)");
});

// ---------------------------------------------------------------- frame loop
let lastPanel = 0;
stage.renderer.setAnimationLoop((now) => {
  if (rig.update()) stage.wake(); // camera moving (input, inertia, animation or turntable)
  if (stage.shouldRender(now)) {
    stage.render();
    stage.tick(now);
  }
  if (now - lastPanel > 500) {
    lastPanel = now;
    if (!$("panel").hidden) updatePerf();
    if (params.debug) writeDebug();
  }
});

function writeDebug() {
  const b = state.bounds;
  $("debug").hidden = false;
  $("debug").textContent = JSON.stringify({
    status: state.status,
    error: state.error,
    splats: state.mesh?.numSplats ?? 0,
    fps: Math.round(stage.fps),
    idle: stage.idle,
    ratio: stage.ratio,
    orient: state.orient,
    mode: rig.mode,
    scene: state.scene,
    bounds: b && { center: b.center.map((x) => +x.toFixed(4)), size: b.size.map((x) => +x.toFixed(4)) },
    camera: stage.camera.position.toArray().map((x) => +x.toFixed(3)),
    target: rig.controls.target.toArray().map((x) => +x.toFixed(3)),
    lastHit: state.lastHit ?? null,
  });
}

// ---------------------------------------------------------------- start
if (params.url) loadFromUrl(params.url, params.name);
else showEmpty("Open a Gaussian splat", "Drop a <b>.ply</b> or <b>.spz</b> file here, or choose one from this device.");
