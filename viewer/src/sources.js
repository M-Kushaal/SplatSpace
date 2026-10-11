// Getting splat bytes: URL (streamed, with progress), file picker, drag & drop.
import { fileKind } from "./plyinfo.js";

/** Fetch a URL into a Uint8Array, reporting progress (0..1, or -1 when the size is unknown). */
export async function fetchBytes(url, onProgress, { signal } = {}) {
  const res = await fetch(url, { signal });
  if (!res.ok) throw new Error(`Download failed (${res.status} ${res.statusText || ""})`.trim());
  const total = Number(res.headers.get("content-length")) || 0;
  if (!res.body) {
    const buf = new Uint8Array(await res.arrayBuffer());
    onProgress?.(1, buf.length, buf.length);
    return buf;
  }
  const reader = res.body.getReader();
  let buf = new Uint8Array(total || 1 << 20); // exact size when known; grows otherwise
  let n = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    if (n + value.length > buf.length) {
      const bigger = new Uint8Array(Math.max(buf.length * 2, n + value.length));
      bigger.set(buf.subarray(0, n));
      buf = bigger;
    }
    buf.set(value, n);
    n += value.length;
    onProgress?.(total ? Math.min(n / total, 1) : -1, n, total);
  }
  return n === buf.length ? buf : buf.slice(0, n);
}

/** HEAD-like size probe (falls back to unknown). */
export async function remoteSize(url) {
  try {
    const res = await fetch(url, { method: "HEAD" });
    return Number(res.headers.get("content-length")) || 0;
  } catch {
    return 0;
  }
}

/** Split dropped/picked files into { splat: File|null, json: File[] }. */
export function sortFiles(fileList) {
  const files = Array.from(fileList || []);
  return {
    splat: files.find((f) => fileKind(f.name)) || null,
    json: files.filter((f) => f.name.toLowerCase().endsWith(".json")),
  };
}

export async function readJsonFiles(files) {
  const out = {};
  for (const f of files) {
    try {
      const data = JSON.parse(await f.text());
      if (data && ("registered" in data || "reproj_px" in data)) out.refine = data;
      else out.metrics = data;
    } catch {
      /* ignore unreadable json */
    }
  }
  return out;
}

export async function fetchJson(url) {
  if (!url) return null;
  try {
    const res = await fetch(url);
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

export function nameFromUrl(url) {
  try {
    const p = new URL(url, location.href).pathname;
    return decodeURIComponent(p.substring(p.lastIndexOf("/") + 1)) || "splat";
  } catch {
    return "splat";
  }
}
