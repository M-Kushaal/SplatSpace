// Orbit camera with inertia, eased framing / focus animations, double-tap focus and keyboard control.
// Rooms also get a first-person "walk" mode: the orbit target sits WALK_LOOK ahead of the eye, so a drag
// turns the head, and zoom input (wheel / pinch) is converted into walking forward and back.
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";
import { WALK_LOOK, WALK_SPEED, dollyStep, ease, fitDistance, orbitPosition } from "./framing.js";

export class CameraRig {
  constructor(camera, dom, { onUserInput } = {}) {
    this.camera = camera;
    this.dom = dom;
    const c = (this.controls = new OrbitControls(camera, dom));
    c.enableDamping = true;
    c.dampingFactor = 0.075;
    c.rotateSpeed = 0.65;
    c.zoomSpeed = 0.9;
    c.panSpeed = 0.8;
    c.zoomToCursor = true;
    c.screenSpacePanning = true;
    c.minPolarAngle = 0.02;
    c.maxPolarAngle = Math.PI - 0.02;
    c.autoRotateSpeed = 1.2;
    c.touches = { ONE: THREE.TOUCH.ROTATE, TWO: THREE.TOUCH.DOLLY_PAN };
    c.addEventListener("start", () => {
      this.anim = null;
      if (this.controls.autoRotate) this.setSpin(false);
      onUserInput?.();
    });
    this.anim = null;
    this.bounds = null;
    this.onSpinChange = null;
    this.mode = "orbit";
    this.walkInput = { forward: 0, right: 0, up: 0 }; // held keys, -1..1 each
    this.lastUpdate = performance.now();
    this.orbitParams = { rotateSpeed: c.rotateSpeed, panSpeed: c.panSpeed, zoomToCursor: c.zoomToCursor };
  }

  get walking() {
    return this.mode === "walk";
  }

  /** First-person mode inside a room; glides to `eye` looking at `target` (WALK_LOOK ahead). */
  enterWalk(eye, target, seconds = 1.2) {
    const c = this.controls;
    this.mode = "walk";
    c.rotateSpeed = -0.35; // drag the world: swipe left turns the view right, like a photo sphere
    c.panSpeed = 0.8 * (2 / WALK_LOOK); // pan as if the target were 2 m away (strafe at a useful speed)
    c.zoomToCursor = false;
    c.minDistance = 1e-4;
    c.maxDistance = 1;
    c.autoRotate = false;
    this.camera.near = 0.02;
    this.camera.updateProjectionMatrix();
    this.animateTo(eye, target, seconds);
  }

  /** Back to orbiting the whole model (the room as a "dollhouse"). */
  exitWalk() {
    if (!this.walking) return;
    this.mode = "orbit";
    Object.assign(this.controls, this.orbitParams);
    if (this.bounds) this.setBounds(this.bounds);
  }

  /** Walk mode: move the eye (and its target) by metres along the view (level), right and up. */
  walkBy(forward, right = 0, up = 0, seconds = 0.18) {
    const dir = this.controls.target.clone().sub(this.camera.position);
    dir.y = 0;
    if (dir.lengthSq() < 1e-12) return;
    dir.normalize();
    const side = new THREE.Vector3().crossVectors(dir, this.camera.up).normalize();
    const d = dir.multiplyScalar(forward).addScaledVector(side, right).add(new THREE.Vector3(0, up, 0));
    this.animateTo(this.camera.position.clone().add(d).toArray(), this.controls.target.clone().add(d).toArray(), seconds);
  }

  /** Walk mode: glide toward a tapped point, stopping `stop` metres before it, looking at it. */
  walkTo(point, stop = 0.8) {
    const p = new THREE.Vector3(...point);
    const dir = p.clone().sub(this.camera.position);
    const dist = dir.length();
    if (dist < 1e-6) return;
    dir.normalize();
    const eye = this.camera.position.clone().addScaledVector(dir, Math.max(0, dist - stop));
    this.animateTo(eye.toArray(), eye.clone().addScaledVector(dir, WALK_LOOK).toArray(), 0.8);
  }

  setBounds(bounds) {
    this.bounds = bounds;
    if (this.walking) return; // walk mode keeps its own near plane / distance limits
    const r = bounds.radius;
    this.controls.minDistance = r * 0.08;
    this.controls.maxDistance = r * 12;
    this.camera.near = Math.max(r / 500, 1e-4);
    this.camera.far = r * 200;
    this.camera.updateProjectionMatrix();
  }

  /** Distance that frames the object in the current viewport. */
  frameDistance() {
    return fitDistance(this.bounds.radius, this.camera.fov, this.camera.aspect);
  }

  /** Animate camera position + orbit target with an ease-in-out curve. */
  animateTo(position, target, seconds = 0.7) {
    this.anim = {
      t0: performance.now(),
      dur: seconds * 1000,
      p0: this.camera.position.clone(),
      q0: this.controls.target.clone(),
      p1: new THREE.Vector3(...position),
      q1: new THREE.Vector3(...target),
    };
  }

  /** Home view: 3/4 from slightly above, the whole object in frame. */
  frame({ seconds = 0.7, azimuth = 35, elevation = 20, scale = 1 } = {}) {
    if (!this.bounds) return;
    const d = this.frameDistance() * scale;
    this.animateTo(orbitPosition(this.bounds.center, d, azimuth, elevation), this.bounds.center, seconds);
  }

  /** Keep the current viewing direction, re-centre and fit the object. */
  refit(seconds = 0.6) {
    if (!this.bounds) return;
    const dir = this.camera.position.clone().sub(this.controls.target).normalize();
    const c = new THREE.Vector3(...this.bounds.center);
    const p = c.clone().addScaledVector(dir, this.frameDistance());
    this.animateTo(p.toArray(), this.bounds.center, seconds);
  }

  /** Glide so `point` becomes the orbit centre, moving `zoom` (0..1) of the way closer. */
  focusOn(point, zoom = 0.45) {
    const target = new THREE.Vector3(...point);
    const offset = this.camera.position.clone().sub(this.controls.target);
    const dist = Math.max(offset.length() * (1 - zoom), this.controls.minDistance * 1.5);
    const p = target.clone().addScaledVector(offset.normalize(), dist);
    this.animateTo(p.toArray(), target.toArray(), 0.65);
  }

  /** Place the camera instantly (used for the intro start pose). */
  jump(position, target) {
    this.anim = null;
    this.camera.position.set(...position);
    this.controls.target.set(...target);
    this.controls.update();
  }

  setSpin(on) {
    this.controls.autoRotate = on;
    this.onSpinChange?.(on);
  }

  orbitBy(dAzimuth, dPolar) {
    this.anim = null;
    const offset = this.camera.position.clone().sub(this.controls.target);
    const s = new THREE.Spherical().setFromVector3(offset);
    s.theta += dAzimuth;
    s.phi = THREE.MathUtils.clamp(s.phi + dPolar, this.controls.minPolarAngle, this.controls.maxPolarAngle);
    const p = new THREE.Vector3().setFromSpherical(s).add(this.controls.target);
    this.animateTo(p.toArray(), this.controls.target.toArray(), 0.25);
  }

  zoomBy(factor) {
    const offset = this.camera.position.clone().sub(this.controls.target);
    const len = THREE.MathUtils.clamp(offset.length() * factor, this.controls.minDistance, this.controls.maxDistance);
    const p = this.controls.target.clone().addScaledVector(offset.normalize(), len);
    this.animateTo(p.toArray(), this.controls.target.toArray(), 0.25);
  }

  /** Advance animations + damping. Returns true if the camera moved this frame. */
  update() {
    const now = performance.now();
    const dt = Math.min((now - this.lastUpdate) / 1000, 0.1); // clamp: no jump after a stalled frame
    this.lastUpdate = now;
    const animating = !!this.anim;
    if (this.anim) {
      const a = this.anim;
      const t = ease((performance.now() - a.t0) / a.dur);
      this.camera.position.lerpVectors(a.p0, a.p1, t);
      this.controls.target.lerpVectors(a.q0, a.q1, t);
      if (t >= 1) this.anim = null;
    }
    const moved = this.controls.update();
    let walked = false;
    if (this.walking && !this.anim) {
      this.applyWalkDolly();
      walked = this.applyWalkKeys(dt);
    }
    return moved || animating || walked || this.controls.autoRotate;
  }

  /** Held walk keys: move at WALK_SPEED m/s, level along the view (forward/right) and vertically (up). */
  applyWalkKeys(dt) {
    const k = this.walkInput;
    if (!k.forward && !k.right && !k.up) return false;
    const dir = this.controls.target.clone().sub(this.camera.position);
    dir.y = 0;
    if (dir.lengthSq() < 1e-12) return false;
    dir.normalize();
    const side = new THREE.Vector3().crossVectors(dir, this.camera.up).normalize();
    const v = dir.multiplyScalar(k.forward).addScaledVector(side, k.right).add(new THREE.Vector3(0, k.up, 0));
    if (v.lengthSq() > 1) v.normalize(); // diagonal isn't faster
    v.multiplyScalar(WALK_SPEED * dt);
    this.camera.position.add(v);
    this.controls.target.add(v);
    return true;
  }

  /** Turn the radius change OrbitControls made (wheel / pinch) into a step, and restore the radius. */
  applyWalkDolly() {
    const cam = this.camera.position, target = this.controls.target;
    const r = cam.distanceTo(target);
    if (Math.abs(r - WALK_LOOK) < 1e-7) return;
    const dir = target.clone().sub(cam).normalize();
    const step = dollyStep(WALK_LOOK, r);
    target.addScaledVector(dir, step); // move the target by the step...
    cam.copy(target).addScaledVector(dir, -WALK_LOOK); // ...and keep the eye WALK_LOOK behind it
  }
}

/**
 * Double-tap / double-click detection on top of OrbitControls (which doesn't use it). Calls
 * onDouble(clientX, clientY) when two taps land within 330 ms and 26 px of each other.
 */
export function onDoubleTap(dom, onDouble) {
  let last = null;
  let down = null;
  dom.addEventListener("pointerdown", (e) => { down = { x: e.clientX, y: e.clientY, t: performance.now() }; });
  dom.addEventListener("pointerup", (e) => {
    if (!down || Math.hypot(e.clientX - down.x, e.clientY - down.y) > 10 || performance.now() - down.t > 300) {
      down = null;
      return; // a drag, not a tap
    }
    const now = performance.now();
    if (last && now - last.t < 330 && Math.hypot(e.clientX - last.x, e.clientY - last.y) < 26) {
      last = null;
      onDouble(e.clientX, e.clientY);
    } else last = { x: e.clientX, y: e.clientY, t: now };
    down = null;
  });
}
