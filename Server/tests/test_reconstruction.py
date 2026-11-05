"""Mesh generation: outward faces (invisible-mesh regression) and
SAM mask silhouettes with real object proportions."""

import os
import sys
import tempfile
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
os.environ.setdefault("DATA_DIR", tempfile.mkdtemp(prefix="remotion-mesh-test-"))

from app.services import reconstruction  # noqa: E402


def _outward_ratio(positions, indices):
    p = np.array(positions, dtype=np.float64)
    idx = np.array(indices, dtype=np.int64).reshape(-1, 3)
    v0, v1, v2 = p[idx[:, 0]], p[idx[:, 1]], p[idx[:, 2]]
    normals = np.cross(v1 - v0, v2 - v0)
    centroids = (v0 + v1 + v2) / 3.0
    dots = np.einsum("ij,ij->i", normals, centroids - p.mean(axis=0))
    return float((dots > 0).mean())


def test_sphere_faces_point_outward():
    positions, indices = reconstruction._sphere(0.5)
    assert _outward_ratio(positions, indices) >= 0.9


def test_mask_mesh_is_object_shaped_and_outward(tmp_path):
    import cv2

    mask = np.zeros((270, 480), dtype=np.uint8)
    mask[90:260, 0:272] = 255  # ultrawide-monitor-like silhouette
    ref = tmp_path / "mask.png"
    cv2.imwrite(str(ref), mask)
    track = {"kind": "object", "cutout_ref": str(ref),
             "mask_bbox_xywh": [0, 90, 272, 170]}
    out = reconstruction._mask_mesh(track)
    assert out is not None
    positions, indices, _color = out
    xs = [p[0] for p in positions]
    ys = [p[1] for p in positions]
    width = max(xs) - min(xs)
    height = max(ys) - min(ys)
    assert width > 0.7, f"monitor mesh should be wide, got {width:.2f}m"
    assert width > height * 1.4, "silhouette aspect must survive (wide, not a ball)"
    assert _outward_ratio(positions, indices) > 0.9
