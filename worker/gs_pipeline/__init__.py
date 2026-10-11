"""Shared capture-bundle + Gaussian splat training pipeline.

Pure-numpy modules (cameras, manifest, colmap_io, ply_io, validate, visualize)
run anywhere. Only `train` needs torch + a CUDA GPU.
"""

__version__ = "0.1.0"
SCHEMA_VERSION = "1.0"
