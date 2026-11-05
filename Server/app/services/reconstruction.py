"""3D reconstruction stage.

Backends, recorded per-asset in the scene package:
- SAM 3D (Objects for object tracks, Body for people): used when the
  sam_3d_objects package is importable on the GPU worker. Consumes
  cut-outs ONLY, never full photos.
- trajectory-mesh (default): generates a real .glb mesh per track from
  the recorded trajectory (bounding volume of the object's path; a
  capsule-proportioned mesh for a person). This is generated geometry
  from the capture, not a stub URL.
REMOTION_REQUIRE_SAM=1 makes a missing SAM backend fail the job rather
than silently shipping the fallback.
"""

import importlib.util
import json
import logging
import math
import os
import struct

log = logging.getLogger("remotion.reconstruction")


def sam3d_available() -> bool:
    """SAM 3D is usable when the endpoint client is configured (or the
    official in-process `sam3d_objects` package is importable)."""
    from app.services import sam3d

    return sam3d.sam3d_configured() or importlib.util.find_spec("sam3d_objects") is not None


PERSON_BACKEND_UNAVAILABLE = (
    "SAM 3D Body / RTMPose person reconstruction is not configured on "
    "this worker; no person mesh was fabricated from trajectories.")


def _sphere(radius: float, slices: int = 14, stacks: int = 10):
    positions, indices = [], []
    for s in range(stacks + 1):
        phi = math.pi * s / stacks
        for k in range(slices + 1):
            theta = 2 * math.pi * k / slices
            positions.append((radius * math.sin(phi) * math.cos(theta),
                              radius * math.cos(phi),
                              radius * math.sin(phi) * math.sin(theta)))
    for s in range(stacks):
        for k in range(slices):
            a = s * (slices + 1) + k
            b, c, d = a + slices + 1, a + 1, a + slices + 2
            indices += [a, d, b, a, c, d]  # outward-facing winding
    return positions, indices


def _mask_mesh(track: dict):
    """Extruded silhouette from the SAM cut-out mask (real object shape:
    a monitor becomes a wide slab, a keyboard a flat rectangle).

    Returns (positions, indices, color) or None when no usable mask.
    """
    ref = track.get("cutout_ref")
    if not ref or not str(ref).endswith(".png"):
        return None
    import cv2
    import numpy as np

    img = cv2.imread(str(ref), cv2.IMREAD_GRAYSCALE)
    if img is None:
        return None
    frame_h, frame_w = img.shape[:2]
    bbox = track.get("mask_bbox_xywh")
    if bbox and len(bbox) == 4 and bbox[2] > 0 and bbox[3] > 0:
        x, y, w, h = (int(v) for v in bbox)
        x, y = max(0, x), max(0, y)
        crop = img[y:y + h, x:x + w]
        if crop.size == 0:
            return None
    else:
        crop = img
        x, y, w, h = 0, 0, frame_w, frame_h
    filled = crop > 127
    if filled.sum() < 40:
        return None
    cols = 64
    rows = max(8, int(round(cols * (h / max(1, w)))))
    small = cv2.resize(filled.astype(np.uint8), (cols, rows),
                       interpolation=cv2.INTER_NEAREST) > 0
    if small.sum() < 12:
        return None
    width_m = min(1.1, max(0.12, (w / max(1, frame_w)) * 1.5))
    height_m = width_m * (h / max(1, w))
    depth = max(0.03, width_m * 0.08)
    cw, ch = width_m / cols, height_m / rows

    def vid(r: int, c: int, front: bool) -> int:
        return (r * (cols + 1) + c) * 2 + (0 if front else 1)

    positions = []
    for r in range(rows + 1):
        for c in range(cols + 1):
            px = -width_m / 2 + c * cw
            py = height_m / 2 - r * ch  # image top = mesh top
            positions.append((px, py, depth / 2))
            positions.append((px, py, -depth / 2))
    indices = []
    for r in range(rows):
        for c in range(cols):
            if not small[r, c]:
                continue
            a, b = vid(r, c, True), vid(r, c + 1, True)
            cc, d = vid(r + 1, c, True), vid(r + 1, c + 1, True)
            indices += [a, b, d, a, d, cc]          # front (+z)
            a2, b2 = vid(r, c, False), vid(r, c + 1, False)
            c2, d2 = vid(r + 1, c, False), vid(r + 1, c + 1, False)
            indices += [a2, d2, b2, a2, c2, d2]      # back (-z)
            # Side walls where a neighbour cell is empty.
            if r == 0 or not small[r - 1, c]:
                indices += [a, a2, b2, a, b2, b]    # top
            if r == rows - 1 or not small[r + 1, c]:
                indices += [cc, d, d2, cc, d2, c2]  # bottom
            if c == 0 or not small[r, c - 1]:
                indices += [a, cc, c2, a, c2, a2]   # left
            if c == cols - 1 or not small[r, c + 1]:
                indices += [b, b2, d2, b, d2, d]    # right
    if not indices:
        return None
    # Grid winding above comes out inward (y axis is flipped vs the
    # image); flip every triangle so all faces point outward.
    flipped = []
    for i in range(0, len(indices) - 2, 3):
        flipped += [indices[i], indices[i + 2], indices[i + 1]]
    return positions, flipped, (0.2, 0.45, 1.0, 1.0)


def build_glb(positions, indices, color) -> bytes:
    """Minimal valid GLB (POSITION + indices + one PBR material)."""
    pos_bytes = b"".join(struct.pack("<fff", *p) for p in positions)
    idx_bytes = b"".join(struct.pack("<I", i) for i in indices)
    mins = [min(p[i] for p in positions) for i in range(3)]
    maxs = [max(p[i] for p in positions) for i in range(3)]
    bin_chunk = pos_bytes + idx_bytes
    gltf = {
        "asset": {"version": "2.0", "generator": "ReMotion-XR trajectory-mesh"},
        "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"mesh": 0}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0}, "indices": 1, "material": 0}]}],
        "materials": [{"pbrMetallicRoughness": {"baseColorFactor": list(color),
                                                "metallicFactor": 0.0, "roughnessFactor": 0.8}}],
        "buffers": [{"byteLength": len(bin_chunk)}],
        "bufferViews": [
            {"buffer": 0, "byteOffset": 0, "byteLength": len(pos_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": len(pos_bytes), "byteLength": len(idx_bytes), "target": 34963},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(positions), "type": "VEC3",
             "min": mins, "max": maxs},
            {"bufferView": 1, "componentType": 5125, "count": len(indices), "type": "SCALAR"},
        ],
    }
    json_bytes = json.dumps(gltf).encode()
    json_bytes += b" " * ((4 - len(json_bytes) % 4) % 4)
    bin_padded = bin_chunk + b"\x00" * ((4 - len(bin_chunk) % 4) % 4)
    total = 12 + 8 + len(json_bytes) + 8 + len(bin_padded)
    return (struct.pack("<III", 0x46546C67, 2, total)
            + struct.pack("<II", len(json_bytes), 0x4E4F534A) + json_bytes
            + struct.pack("<II", len(bin_padded), 0x004E4942) + bin_padded)


def _track_mesh(track: dict):
    # Persons are never trajectory ellipsoids: reconstruct() marks them
    # unavailable instead of calling this for a person track.
    # Real shape first: extrude the SAM mask silhouette when we have one.
    mask_mesh = _mask_mesh(track)
    if mask_mesh is not None:
        track["_mask_mesh"] = True
        return mask_mesh
    bbox_min, bbox_max = track.get("bbox_min"), track.get("bbox_max")
    radius = 0.12
    if bbox_min and bbox_max:
        radius = max(0.06, min(0.3, max(b - a for a, b in zip(bbox_min, bbox_max)) * 0.25))
    positions, indices = _sphere(radius)
    return positions, indices, (0.2, 0.45, 1.0, 1.0)


async def _try_sam3d(cutout: dict, video_path) -> bytes | None:
    """SAM 3D Objects via the serverless endpoint; None -> fall back."""
    from app.services import sam3d

    if video_path is None or not cutout.get("cutout_ref"):
        return None
    import cv2

    from app.services.segmentation import _extract_frame

    frame = _extract_frame(video_path, int(cutout.get("best_frame_index", 0)))
    mask_img = cv2.imread(str(cutout["cutout_ref"]), cv2.IMREAD_GRAYSCALE)
    if frame is None or mask_img is None:
        return None
    mask = mask_img > 127
    if not mask.any():
        return None
    return await sam3d.reconstruct_object(frame, mask)


async def reconstruct(cutouts: list[dict], video_path=None) -> list[dict]:
    require_sam = os.environ.get("REMOTION_REQUIRE_SAM") == "1"
    from app.services import sam3d

    endpoint_sam3d = sam3d.sam3d_configured()
    use_sam = endpoint_sam3d or sam3d_available()
    if require_sam and not use_sam:
        raise RuntimeError(
            "REMOTION_REQUIRE_SAM=1 but SAM 3D is not available "
            "(no endpoint configured, package not importable); "
            "failing instead of shipping a fallback mesh.")
    backend = "sam3d" if use_sam else "preview-geometry"
    assets = []
    for cutout in cutouts:
        if cutout.get("kind") == "person":
            if require_sam:
                raise RuntimeError(
                    "REMOTION_REQUIRE_SAM=1 and person reconstruction "
                    "backend is unavailable; refusing a fallback person.")
            assets.append({
                **cutout, "model": None, "model_bytes": None,
                "skeleton": None, "reconstruction_backend": "unavailable",
                "capabilities": [],
                "unavailable_reason": PERSON_BACKEND_UNAVAILABLE,
            })
            log.info("reconstruct track=%s kind=person backend=unavailable",
                     cutout["track_id"])
            continue
        glb = None
        asset_backend = backend
        if endpoint_sam3d and cutout.get("kind") == "object":
            try:
                glb = await _try_sam3d(cutout, video_path)
                if glb is not None:
                    asset_backend = "sam3d"
            except Exception:
                log.exception("SAM 3D failed for %s; falling back",
                              cutout.get("track_id"))
                glb = None
            if glb is None and require_sam:
                raise RuntimeError(
                    f"REMOTION_REQUIRE_SAM=1 and SAM 3D produced no model "
                    f"for {cutout.get('track_id')}")
        if glb is None:
            if require_sam:
                raise RuntimeError(
                    f"REMOTION_REQUIRE_SAM=1 and no SAM model was produced "
                    f"for {cutout.get('track_id')}; refusing preview geometry.")
            positions, indices, color = _track_mesh(cutout)
            glb = build_glb(positions, indices, color)
            # Honest label: preview geometry, never "reconstruction".
            asset_backend = "sam-mask-mesh" if cutout.pop("_mask_mesh", False) else "preview-geometry"
        assets.append({
            **cutout,
            "model": f"assets/{cutout['track_id']}.glb",
            "model_bytes": glb,
            "skeleton": None,
            "reconstruction_backend": asset_backend,
            "capabilities": (["reconstructed"] if asset_backend == "sam3d"
                             else ["preview"]),
        })
        log.info("reconstruct track=%s kind=%s backend=%s glb=%d bytes",
                 cutout["track_id"], cutout["kind"], asset_backend, len(glb))
    return assets
