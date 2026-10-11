"""Where the object actually is.

The app's object_bounds is a cylinder the user tapped onto an early AR plane estimate, so it can be
off by 10+ cm (seen on the first OnePlus capture). The AR feature points are measured, so we refine
the bounds from the points that stand up off the floor near the tapped spot.
"""

from dataclasses import dataclass

import numpy as np

MIN_OBJECT_POINTS = 50


@dataclass
class ObjectBounds:
    base: np.ndarray  # footprint center on the floor (y up)
    radius: float
    height: float
    source: str       # "refined from N points" | "user cylinder"

    @property
    def middle(self) -> np.ndarray:
        return self.base + [0.0, 0.5 * self.height, 0.0]

    @property
    def sphere_radius(self) -> float:
        return float(np.hypot(self.radius, 0.5 * self.height))


def user_bounds(manifest: dict) -> ObjectBounds | None:
    b = manifest.get("object_bounds")
    if not b:
        return None
    return ObjectBounds(np.asarray(b["center"], float), float(b["radius"]),
                        float(b.get("height", b["radius"])), "user cylinder")


def refine_bounds(manifest: dict, xyz: np.ndarray | None) -> ObjectBounds | None:
    """Refine the user cylinder with AR points; falls back to the user cylinder if points are too sparse."""
    user = user_bounds(manifest)
    if user is None or xyz is None or len(xyz) == 0:
        return user
    horiz = np.hypot(xyz[:, 0] - user.base[0], xyz[:, 2] - user.base[2])

    # Floor = densest 1 cm height layer over a wide area (near the object, the object's own points can
    # outnumber the floor's). The AR plane the user tapped can be 10+ cm off, so search +-0.6 m around it.
    wide = xyz[(horiz < 1.5) & (np.abs(xyz[:, 1] - user.base[1]) < 0.6)]
    if len(wide) < MIN_OBJECT_POINTS:
        return user
    edges = np.arange(wide[:, 1].min(), wide[:, 1].max() + 0.02, 0.01)
    hist, edges = np.histogram(wide[:, 1], bins=edges)
    floor = float(edges[int(np.argmax(np.convolve(hist, np.ones(3) / 3, mode="same")))] + 0.005)

    # The object = points clearly above the floor near the tapped spot.
    search = max(2.5 * user.radius, user.radius + 0.15)
    top = floor + max(2 * user.height, 0.6)
    obj = xyz[(horiz < search) & (xyz[:, 1] > floor + 0.03) & (xyz[:, 1] < top)]
    if len(obj) < MIN_OBJECT_POINTS:
        return user

    center_xz = np.median(obj[:, [0, 2]], axis=0)
    r = float(np.percentile(np.hypot(obj[:, 0] - center_xz[0], obj[:, 2] - center_xz[1]), 90))
    h = float(np.percentile(obj[:, 1], 95) - floor)
    return ObjectBounds(np.array([center_xz[0], floor, center_xz[1]]), max(r, 0.5 * user.radius),
                        max(h, 0.5 * user.height), f"refined from {len(obj)} points")
