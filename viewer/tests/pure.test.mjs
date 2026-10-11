// Run: node --test viewer/tests
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, existsSync } from "node:fs";

import { robustBounds, fitDistance, orbitPosition, percentile, ease } from "../src/framing.js";
import { parsePlyHeader, fileKind } from "../src/plyinfo.js";
import { bytes, count, length, duration, parseParams } from "../src/format.js";

test("percentile interpolates", () => {
  assert.equal(percentile([0, 10], 0.5), 5);
  assert.equal(percentile([1, 2, 3], 1), 3);
});

test("robustBounds ignores a few far-away floaters", () => {
  const pts = [];
  // 10k points in a 0.1 x 0.3 x 0.1 box (a bottle) ...
  for (let i = 0; i < 10000; i++) pts.push(Math.random() * 0.1, Math.random() * 0.3, Math.random() * 0.1);
  // ... plus 1% floaters 5 m away
  for (let i = 0; i < 100; i++) pts.push(5 + Math.random(), 5, -5);
  const b = robustBounds(new Float32Array(pts));
  assert.ok(b.max[0] < 0.11 && b.max[1] < 0.31, `bounds polluted by floaters: ${b.max}`);
  assert.ok(Math.abs(b.center[1] - 0.15) < 0.02);
  assert.ok(b.radius > 0.14 && b.radius < 0.2);
});

test("fitDistance fits the sphere in the narrower FOV", () => {
  const d = fitDistance(1, 50, 0.5, 1); // portrait: horizontal FOV is the limit
  const hHalf = Math.atan(Math.tan((25 * Math.PI) / 180) * 0.5);
  assert.ok(Math.abs(d - 1 / Math.sin(hHalf)) < 1e-9);
  assert.ok(fitDistance(1, 50, 2, 1) < d); // landscape needs less distance
});

test("orbitPosition puts the camera above and at the right distance", () => {
  const p = orbitPosition([1, 2, 3], 10, 0, 30);
  assert.ok(Math.abs(Math.hypot(p[0] - 1, p[1] - 2, p[2] - 3) - 10) < 1e-9);
  assert.ok(p[1] > 2);
});

test("ease is clamped and symmetric", () => {
  assert.equal(ease(-1), 0);
  assert.equal(ease(2), 1);
  assert.ok(Math.abs(ease(0.5) - 0.5) < 1e-12);
});

function fakePly(props, n = 3) {
  const header = ["ply", "format binary_little_endian 1.0", `element vertex ${n}`,
    ...props.map((p) => `property float ${p}`), "end_header", ""].join("\n");
  return new TextEncoder().encode(header + "\u0000".repeat(8));
}

test("parsePlyHeader reads count and SH degree", () => {
  const rest = Array.from({ length: 45 }, (_, i) => `f_rest_${i}`);
  const info = parsePlyHeader(fakePly(["x", "y", "z", "f_dc_0", ...rest, "opacity", "scale_0", "rot_0"], 1234));
  assert.equal(info.vertexCount, 1234);
  assert.equal(info.shDegree, 3);
  assert.equal(info.isGaussian, true);
  assert.equal(parsePlyHeader(fakePly(["x", "y", "z"])).isGaussian, false);
  assert.equal(parsePlyHeader(new TextEncoder().encode("not a ply")), null);
  assert.throws(() => parsePlyHeader(new TextEncoder().encode("ply\nformat ascii 1.0\n")));
});

test("parsePlyHeader on a real trained splat (if present)", { skip: !existsSync(process.env.GS_SAMPLE_PLY || "-") }, () => {
  const buf = readFileSync(process.env.GS_SAMPLE_PLY);
  const info = parsePlyHeader(new Uint8Array(buf.buffer, buf.byteOffset, 64 * 1024));
  assert.equal(info.shDegree, 3);
  assert.ok(info.vertexCount > 1000);
});

test("fileKind", () => {
  assert.equal(fileKind("splat_object.ply"), "ply");
  assert.equal(fileKind("http://x/files/a/b.SPZ?exp=1&sig=2"), "spz");
  assert.equal(fileKind("model.obj"), null);
});

test("formatting", () => {
  assert.equal(bytes(26226539), "25.0 MB");
  assert.equal(count(606272), "606 k");
  assert.equal(count(1243525), "1.24 M");
  assert.equal(length(0.179), "17.9 cm");
  assert.equal(length(1.234), "1.23 m");
  assert.equal(length(0.005), "5.0 mm");
  assert.equal(duration(1522.5), "25 min");
});

test("parseParams", () => {
  const p = parseParams("?url=http%3A%2F%2Fpc%3A8000%2Ffiles%2Fa.ply%3Fsig%3D1&name=Bottle&up=-y&debug");
  assert.equal(p.url, "http://pc:8000/files/a.ply?sig=1");
  assert.equal(p.name, "Bottle");
  assert.equal(p.up, "-y");
  assert.equal(p.debug, true);
  assert.equal(parseParams("?up=sideways").up, null);
});

// ---------------------------------------------------------------- scene walk mode
import { isScene, walkStart, dollyStep, WALK_LOOK, EYE_HEIGHT } from "../src/framing.js";

test("scene detection: metrics capture_mode, overridable by ?mode=", () => {
  assert.equal(isScene({ capture_mode: "scene" }), true);
  assert.equal(isScene({ capture_mode: "object" }), false);
  assert.equal(isScene(null), false);
  assert.equal(isScene(null, "scene"), true);
  assert.equal(isScene({ capture_mode: "scene" }, "object"), false);
});

test("walk start: pipeline view when given, else room middle at eye height, looking level", () => {
  const bounds = { min: [-2, 0, -1.5], max: [2, 2.6, 1.5], center: [0, 1.3, 0] };
  const a = walkStart(bounds, { eye: [0.4, 1.35, -0.2], forward: [0, -0.5, -0.8] });
  assert.deepEqual(a.eye, [0.4, EYE_HEIGHT, -0.2]); // where the phone was, at eye height above the floor (y = 0)
  assert.ok(Math.abs(a.target[2] - (-0.2 - WALK_LOOK)) < 1e-9 && a.target[1] === EYE_HEIGHT); // level, along -z
  const b = walkStart(bounds);
  assert.deepEqual(b.eye, [0, EYE_HEIGHT, 0]);
  const low = { min: [-2, 0, -1.5], max: [2, 1.0, 1.5], center: [0, 0.5, 0] }; // scan without the ceiling
  assert.ok(Math.abs(walkStart(low).eye[1] - 0.9) < 1e-9); // 1.2 would hit the 1.0 m "ceiling"
  const c = walkStart(low, { eye: [0, 1.4, 0], forward: [1, 0, 0] }); // pipeline view: always eye height
  assert.ok(c.eye[1] === EYE_HEIGHT && c.target[0] > c.eye[0]);
});

test("zoom input becomes a step: zoom in = forward, out = back, symmetric", () => {
  assert.ok(dollyStep(0.05, 0.0475) > 0.15 && dollyStep(0.05, 0.0475) < 0.25); // one wheel notch ~ 20 cm
  assert.ok(Math.abs(dollyStep(0.05, 0.0475) + dollyStep(0.0475, 0.05)) < 1e-12);
  assert.equal(dollyStep(0.05, 0.05), 0);
  assert.equal(dollyStep(0.05, 0), 0);
});
