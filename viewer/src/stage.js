// three.js renderer + Spark splat renderer, with adaptive resolution for smooth frame rates on phones.
import * as THREE from "three";
import { SparkRenderer } from "@sparkjsdev/spark";

const IS_COARSE = matchMedia("(pointer: coarse)").matches;

export class Stage {
  constructor(canvas) {
    this.canvas = canvas;
    this.renderer = new THREE.WebGLRenderer({
      canvas, antialias: false, alpha: true, powerPreference: "high-performance", preserveDrawingBuffer: false,
    });
    this.renderer.setClearColor(0x000000, 0); // transparent: the CSS gradient is the background
    this.scene = new THREE.Scene();
    this.camera = new THREE.PerspectiveCamera(45, 1, 0.01, 1000);
    this.spark = new SparkRenderer({
      renderer: this.renderer,
      accumExtSplats: true, // full-precision splat data on the GPU (pairs with SplatMesh extSplats)
      maxPixelRadius: 320,  // cap on-screen splat size: no huge spikes when the camera is very close
      // blurAmount stays at Spark's 0.3, which matches the 0.3 px low-pass used in training (gsplat eps2d)
    });
    this.scene.add(this.spark);

    this.maxRatio = Math.min(window.devicePixelRatio || 1, IS_COARSE ? 1.75 : 2);
    this.ratio = this.maxRatio;
    this.movingRatio = this.maxRatio; // adaptive resolution while the camera moves
    this.lastChange = performance.now();
    this.idle = false;
    this.quality = "auto";
    this.fps = 0;
    this._frames = 0;
    this._fpsT0 = performance.now();
    this._lastAdapt = 0;
    this.renderer.setPixelRatio(this.ratio);
    this.resize();
    addEventListener("resize", () => this.resize());
  }

  get gpuName() {
    const gl = this.renderer.getContext();
    const ext = gl.getExtension("WEBGL_debug_renderer_info");
    return (ext && gl.getParameter(ext.UNMASKED_RENDERER_WEBGL)) || gl.getParameter(gl.RENDERER) || "unknown";
  }

  resize() {
    this.wake?.();
    const w = innerWidth, h = innerHeight;
    this.renderer.setSize(w, h, false);
    this.camera.aspect = w / h;
    this.camera.updateProjectionMatrix();
  }

  setQuality(q) {
    this.quality = q;
    if (q === "high") this.setRatio((this.movingRatio = this.maxRatio));
    if (q === "battery") this.setRatio((this.movingRatio = 1));
    this.wake();
    this.spark.maxStdDev = q === "battery" ? Math.sqrt(5) : Math.sqrt(8);
  }

  setRatio(r) {
    r = Math.max(0.6, Math.min(this.maxRatio, r));
    if (Math.abs(r - this.ratio) < 0.01) return;
    this.ratio = r;
    this.renderer.setPixelRatio(r);
    this.resize();
  }

  /** Something visible changed (camera, settings, animation): keep rendering for a while. */
  wake() {
    this.lastChange = performance.now();
    if (this.idle) {
      this.idle = false;
      this._fpsT0 = performance.now();
      this._frames = 0;
      if (this.quality === "auto") this.setRatio(this.movingRatio); // resume the adaptive moving resolution
    }
  }

  /**
   * Render only while something changes or Spark is still sorting. Once the view has been still for a moment,
   * draw a few last frames at full resolution and stop: sharp when still, smooth when moving, kind to batteries.
   */
  shouldRender(now) {
    if (now - this.lastChange < 700 || this.spark.sorting || this.spark.sortDirty) return true;
    if (!this.idle) {
      if (this.quality !== "battery" && this.ratio < this.maxRatio) {
        this.setRatio(this.maxRatio);
        this.lastChange = now - 400; // a few more frames so the full-resolution frame gets sorted and drawn
        return true;
      }
      this.idle = true;
    }
    return false;
  }

  /** Call once per rendered frame: measures FPS and nudges the moving resolution in "auto" quality. */
  tick(now) {
    this._frames++;
    const dt = now - this._fpsT0;
    if (dt < 1000) return;
    this.fps = (this._frames * 1000) / dt;
    this._frames = 0;
    this._fpsT0 = now;
    if (this.quality !== "auto" || document.hidden || now - this._lastAdapt < 2500 || now - this.lastChange > 600) return;
    if (this.fps < 28 && this.movingRatio > 0.6) {
      this.movingRatio = Math.max(0.6, this.movingRatio - 0.2);
      this.setRatio(this.movingRatio);
      this._lastAdapt = now;
    } else if (this.fps > 55 && this.movingRatio < this.maxRatio) {
      this.movingRatio = Math.min(this.maxRatio, this.movingRatio + 0.15);
      this.setRatio(this.movingRatio);
      this._lastAdapt = now;
    }
  }

  render() {
    this.renderer.render(this.scene, this.camera);
  }
}

/** Faint ring + disc on the ground plane under the object, for orientation. */
export function makeGroundRing(radius) {
  const group = new THREE.Group();
  const ring = new THREE.Mesh(
    new THREE.RingGeometry(radius * 0.985, radius, 128),
    new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.22, depthWrite: false, side: THREE.DoubleSide }),
  );
  const disc = new THREE.Mesh(
    new THREE.CircleGeometry(radius, 96),
    new THREE.MeshBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.035, depthWrite: false, side: THREE.DoubleSide }),
  );
  ring.rotation.x = disc.rotation.x = -Math.PI / 2;
  group.add(disc, ring);
  group.renderOrder = -1;
  return group;
}
