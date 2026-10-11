// Pure formatting helpers for the UI.

export function bytes(n) {
  if (!(n >= 0)) return "–";
  if (n >= 1 << 30) return `${(n / (1 << 30)).toFixed(1)} GB`;
  if (n >= 1 << 20) return `${(n / (1 << 20)).toFixed(n >= 100 << 20 ? 0 : 1)} MB`;
  if (n >= 1024) return `${Math.round(n / 1024)} KB`;
  return `${n} B`;
}

export function count(n) {
  if (!(n >= 0)) return "–";
  if (n >= 1e6) return `${(n / 1e6).toFixed(n >= 1e7 ? 0 : 2)} M`;
  if (n >= 1e4) return `${Math.round(n / 1e3)} k`;
  if (n >= 1e3) return `${(n / 1e3).toFixed(1)} k`;
  return String(n);
}

/** Length in metres -> "8.2 mm" / "17.9 cm" / "1.24 m"; non-metric -> plain units. */
export function length(m, metric = true) {
  if (!Number.isFinite(m)) return "–";
  if (!metric) return `${m.toPrecision(3)} units`;
  if (m < 0.01) return `${(m * 1000).toFixed(1)} mm`;
  if (m < 1) return `${(m * 100).toFixed(m < 0.1 ? 1 : 1)} cm`;
  return `${m.toFixed(2)} m`;
}

export function duration(seconds) {
  if (!(seconds >= 0)) return "–";
  if (seconds < 90) return `${Math.round(seconds)} s`;
  if (seconds < 5400) return `${Math.round(seconds / 60)} min`;
  return `${(seconds / 3600).toFixed(1)} h`;
}

export function db(x) {
  return Number.isFinite(x) && x > 0 ? `${x.toFixed(1)} dB` : "–";
}

/** Viewer query parameters (pure: takes the search string). */
export function parseParams(search) {
  const q = new URLSearchParams(search);
  const up = q.get("up");
  return {
    url: q.get("url"),
    metrics: q.get("metrics"),
    refine: q.get("refine"),
    name: q.get("name"),
    up: up === "-y" || up === "y" ? up : null,
    debug: q.has("debug"),
  };
}
