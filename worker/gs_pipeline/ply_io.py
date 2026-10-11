"""Binary little-endian PLY I/O for point clouds and 3DGS splats (no extra deps).

Splat files use the standard INRIA 3DGS layout that SuperSplat, Unity/Unreal
splat plugins and most viewers read.
"""

import numpy as np

_PLY_TYPES = {
    "float": "<f4", "float32": "<f4", "double": "<f8", "float64": "<f8",
    "uchar": "u1", "uint8": "u1", "char": "i1", "int8": "i1",
    "ushort": "<u2", "uint16": "<u2", "short": "<i2", "int16": "<i2",
    "uint": "<u4", "uint32": "<u4", "int": "<i4", "int32": "<i4",
}
_NP_TO_PLY = {"<f4": "float", "<f8": "double", "|u1": "uchar", "<i4": "int"}


def write_ply(path, data: np.ndarray) -> None:
    """Write a structured numpy array as a single 'vertex' element."""
    header = ["ply", "format binary_little_endian 1.0", f"element vertex {len(data)}"]
    header += [f"property {_NP_TO_PLY[data.dtype[name].str]} {name}" for name in data.dtype.names]
    header.append("end_header")
    with open(path, "wb") as fh:
        fh.write(("\n".join(header) + "\n").encode("ascii"))
        fh.write(data.tobytes())


def read_ply(path) -> np.ndarray:
    """Read the 'vertex' element of a binary little-endian PLY (must be the first element)."""
    with open(path, "rb") as fh:
        if fh.readline().strip() != b"ply":
            raise ValueError(f"{path}: not a PLY file")
        fmt, count, fields, current = None, 0, [], None
        while True:
            line = fh.readline().decode("ascii").strip()
            if line == "end_header":
                break
            parts = line.split()
            if not parts:
                continue
            if parts[0] == "format":
                fmt = parts[1]
            elif parts[0] == "element":
                current = parts[1]
                if current == "vertex":
                    count = int(parts[2])
            elif parts[0] == "property" and current == "vertex":
                if parts[1] == "list":
                    raise ValueError(f"{path}: list properties on vertices are not supported")
                fields.append((parts[2], _PLY_TYPES[parts[1]]))
        if fmt != "binary_little_endian":
            raise ValueError(f"{path}: only binary_little_endian PLY is supported (got {fmt})")
        dtype = np.dtype(fields)
        return np.frombuffer(fh.read(dtype.itemsize * count), dtype=dtype, count=count)


def write_points_ply(path, xyz: np.ndarray, rgb: np.ndarray | None = None) -> None:
    fields = [("x", "<f4"), ("y", "<f4"), ("z", "<f4")]
    if rgb is not None:
        fields += [("red", "u1"), ("green", "u1"), ("blue", "u1")]
    data = np.empty(len(xyz), dtype=fields)
    xyz = np.asarray(xyz, dtype=np.float32)
    data["x"], data["y"], data["z"] = xyz[:, 0], xyz[:, 1], xyz[:, 2]
    if rgb is not None:
        rgb = np.asarray(rgb, dtype=np.uint8)
        data["red"], data["green"], data["blue"] = rgb[:, 0], rgb[:, 1], rgb[:, 2]
    write_ply(path, data)


def read_points_ply(path) -> tuple[np.ndarray, np.ndarray | None]:
    """Returns (xyz Nx3 float32, rgb Nx3 uint8 or None). Non-finite points (ARCore emits some) are dropped."""
    v = read_ply(path)
    xyz = np.stack([v["x"], v["y"], v["z"]], axis=1).astype(np.float32)
    rgb = None
    if {"red", "green", "blue"} <= set(v.dtype.names):
        rgb = np.stack([v["red"], v["green"], v["blue"]], axis=1).astype(np.uint8)
    ok = np.isfinite(xyz).all(axis=1)
    if not ok.all():
        xyz, rgb = xyz[ok], (rgb[ok] if rgb is not None else None)
    return xyz, rgb


def write_gaussians_ply(path, means, scales_log, quats_wxyz, opacities_logit, sh0, shN) -> None:
    """INRIA 3DGS layout. sh0: (N,3), shN: (N,K,3) with K = (deg+1)^2 - 1.

    Values are stored pre-activation (log scales, logit opacity), as viewers expect.
    """
    n = len(means)
    shN = np.asarray(shN, dtype=np.float32)
    sh_rest = shN.transpose(0, 2, 1).reshape(n, shN.shape[1] * 3)  # channel-major; also valid for n == 0
    names = ["x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2"]
    names += [f"f_rest_{i}" for i in range(sh_rest.shape[1])]
    names += ["opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3"]
    cols = np.concatenate([
        np.asarray(means, np.float32), np.zeros((n, 3), np.float32), np.asarray(sh0, np.float32), sh_rest,
        np.asarray(opacities_logit, np.float32).reshape(n, 1), np.asarray(scales_log, np.float32),
        np.asarray(quats_wxyz, np.float32),
    ], axis=1)
    data = np.empty(n, dtype=[(name, "<f4") for name in names])
    for i, name in enumerate(names):
        data[name] = cols[:, i]
    write_ply(path, data)


def read_gaussians_ply(path) -> dict:
    v = read_ply(path)
    rest = sorted((n for n in v.dtype.names if n.startswith("f_rest_")), key=lambda s: int(s[7:]))
    n = len(v)

    def col(names):
        return np.stack([v[k] for k in names], axis=1)

    return {
        "means": col(["x", "y", "z"]),
        "sh0": col(["f_dc_0", "f_dc_1", "f_dc_2"]),
        "shN": (col(rest).reshape(n, 3, len(rest) // 3).transpose(0, 2, 1) if rest
                else np.zeros((n, 0, 3), np.float32)),
        "opacities_logit": v["opacity"].copy(),
        "scales_log": col(["scale_0", "scale_1", "scale_2"]),
        "quats_wxyz": col(["rot_0", "rot_1", "rot_2", "rot_3"]),
    }
