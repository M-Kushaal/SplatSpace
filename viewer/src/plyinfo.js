// Pure PLY header inspection (no DOM): splat count, SH degree and layout, from the first bytes of a file.

const SH_DEGREE_BY_REST = { 0: 0, 9: 1, 24: 2, 45: 3 };

/**
 * Parse the ASCII header of a .ply. Returns null if the bytes are not a PLY file.
 * { format, vertexCount, properties, shDegree, compressed, headerBytes }
 */
export function parsePlyHeader(bytes) {
  const max = Math.min(bytes.length, 64 * 1024);
  if (max < 4 || bytes[0] !== 0x70 || bytes[1] !== 0x6c || bytes[2] !== 0x79) return null; // "ply"
  let text = "";
  let end = -1;
  for (let i = 0; i < max; i++) {
    text += String.fromCharCode(bytes[i]);
    if (text.endsWith("end_header\n") || text.endsWith("end_header\r\n")) {
      end = i + 1;
      break;
    }
  }
  if (end < 0) throw new Error("PLY header has no end_header");

  let format = null;
  let element = null;
  let vertexCount = 0;
  const properties = [];
  const elements = [];
  for (const raw of text.split(/\r?\n/)) {
    const parts = raw.trim().split(/\s+/);
    if (parts[0] === "format") format = parts[1];
    else if (parts[0] === "element") {
      element = parts[1];
      elements.push(element);
      if (element === "vertex") vertexCount = parseInt(parts[2], 10);
    } else if (parts[0] === "property" && element === "vertex") {
      properties.push(parts[parts.length - 1]);
    }
  }
  const rest = properties.filter((p) => p.startsWith("f_rest_")).length;
  const compressed = elements.includes("chunk"); // SuperSplat / PlayCanvas compressed PLY
  return {
    format,
    vertexCount,
    properties,
    shDegree: compressed ? null : SH_DEGREE_BY_REST[rest] ?? null,
    compressed,
    isGaussian: compressed || ["opacity", "scale_0", "rot_0"].every((p) => properties.includes(p)),
    headerBytes: end,
  };
}

/** File type from a name or URL (for Spark's loader and for the info panel). */
export function fileKind(name) {
  const m = /\.([a-z0-9]+)(?:$|[?#])/i.exec(name || "");
  const ext = m ? m[1].toLowerCase() : "";
  return ["ply", "spz", "splat", "ksplat", "sog", "zip", "rad"].includes(ext) ? ext : null;
}
