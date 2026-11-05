"""SAM 2 cut-out stage (mandatory before SAM 3D).

Real path (GPU pod / any worker with SAM configured):
  1. Read the uploaded original video (storage.original_path).
  2. Extract each track's best frame with OpenCV.
  3. Run SAM 2 automatic mask generation on that frame; keep the
     strongest masks as the object/person cut-outs and save mask PNGs.
Only cut-outs — never full frames — pass downstream.

When SAM 2 is not configured, tracks pass through with an explicit
not-configured marker and reconstruction uses the trajectory-mesh
backend; the scene package records which backend produced it.
Configuring a checkpoint that cannot load fails the job instead of
faking a cut-out.
"""

import importlib.util
import logging
import math
import os
from pathlib import Path

from app.config import DATA_DIR
from app.services import meta_identify
from app.services.detection import hand_positions

log = logging.getLogger("remotion.segmentation")


def sam2_configured() -> bool:
    return bool(os.environ.get("REMOTION_SAM2_CHECKPOINT"))


def sam2_available() -> bool:
    return sam2_configured() and importlib.util.find_spec("sam2") is not None


def _best_frame_index(timeline: dict) -> int:
    """Keyframe by capture timestamp + hand-motion quality, not a fixed
    hand-array index into a differently-clocked video stream."""
    frames = timeline.get("frames") or []
    positions = hand_positions(frames, "right") or hand_positions(frames, "left")
    if len(positions) < 2:
        return 0
    best, best_move = 0, -1.0
    for i in range(1, len(positions)):
        move = math.dist(positions[i], positions[i - 1])
        if move > best_move:
            best, best_move = i, move
    return best


def _iou_xyxy(a, b) -> float:
    if not a or not b:
        return 0.0
    x0, y0 = max(a[0], b[0]), max(a[1], b[1])
    x1, y1 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, x1 - x0) * max(0.0, y1 - y0)
    area = lambda b: max(0.0, b[2] - b[0]) * max(0.0, b[3] - b[1])
    union = area(a) + area(b) - inter
    return inter / union if union > 0 else 0.0


def choose_mask(masks: list[dict], track: dict, width: int, height: int):
    """Pick the mask belonging to THIS track.

    With an observed detection box, the best-IoU mask wins, so two
    subjects never collapse onto the same largest/central mask. Without
    a box, fall back to the mask nearest the track's own observed
    centroid; persons and objects use the same identity rule.
    """
    import numpy as np

    def mask_box(mask):
        ys, xs = np.nonzero(mask["segmentation"])
        if not len(xs):
            return None
        return [float(xs.min()), float(ys.min()),
                float(xs.max()), float(ys.max())]

    box = track.get("bbox_xyxy")
    if box:
        return max(masks, key=lambda m: _iou_xyxy(mask_box(m), box))
    return max(masks, key=lambda m: float(m.get("area", 0)))


def _extract_frame(video_path: Path, frame_index: int):
    """Best-effort frame grab; returns an RGB numpy array or None."""
    try:
        import cv2
    except ImportError:
        return None
    if video_path is None or not Path(video_path).exists():
        return None
    cap = cv2.VideoCapture(str(video_path))
    try:
        total = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        if total > 0:
            cap.set(cv2.CAP_PROP_POS_FRAMES, max(0, min(frame_index, total - 1)))
        ok, frame = cap.read()
        if not ok:
            return None
        return cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
    finally:
        cap.release()


_sam2_generator = None


def _get_sam2_generator():
    """Load SAM 2 once per worker; never rebuild it per subject."""
    global _sam2_generator
    if _sam2_generator is None:
        from sam2.automatic_mask_generator import SAM2AutomaticMaskGenerator
        from sam2.build_sam import build_sam2

        checkpoint = os.environ["REMOTION_SAM2_CHECKPOINT"]
        config = os.environ.get("REMOTION_SAM2_CONFIG", "configs/sam2.1/sam2.1_hiera_l.yaml")
        device = os.environ.get("REMOTION_SAM2_DEVICE", "cuda")
        _sam2_generator = SAM2AutomaticMaskGenerator(
            build_sam2(config, checkpoint, device=device))
    return _sam2_generator


def _run_sam2(frame_rgb, track: dict, session_id: str | None) -> dict:
    """Run SAM 2 automatic masks; persist the chosen cut-out PNG."""
    import cv2
    import numpy as np

    generator = _get_sam2_generator()
    masks = generator.generate(frame_rgb)
    if not masks:
        raise RuntimeError("SAM 2 produced no masks for the best frame")
    height, width = frame_rgb.shape[:2]
    chosen = choose_mask(masks, track, width, height)
    mask_dir = DATA_DIR / "masks"
    mask_dir.mkdir(parents=True, exist_ok=True)
    mask_path = mask_dir / f"{session_id or 'session'}_{track['track_id']}.png"
    cv2.imwrite(str(mask_path), (chosen["segmentation"].astype(np.uint8) * 255))
    ys, xs = np.nonzero(chosen["segmentation"])
    return {
        "cutout_ref": str(mask_path),
        "mask_area_px": int(chosen["area"]),
        "mask_bbox_xywh": [int(xs.min()), int(ys.min()),
                           int(xs.max() - xs.min()), int(ys.max() - ys.min())],
        "sam_backend": "sam2",
    }


async def cut_out(tracks: list[dict], timeline: dict,
                  session_id: str | None = None,
                  video_path: Path | None = None) -> list[dict]:
    # Meta Model API first when configured: vision names the important
    # objects, SAM 3.1 cuts them out. Any failure falls back to the
    # SAM 2 path below — a Meta outage never fakes a cut-out.
    if meta_identify.meta_configured():
        try:
            if video_path is None and session_id:
                from app.services import storage
                video_path = storage.original_path(session_id)
            return await meta_identify.identify_tracks(
                video_path, timeline, session_id, tracks,
                _best_frame_index(timeline), _extract_frame)
        except Exception:
            log.exception("Meta identification failed; falling back to SAM 2 path")
    if sam2_configured() and not sam2_available():
        raise RuntimeError(
            "REMOTION_SAM2_CHECKPOINT is set but the sam2 package is not "
            "importable on this worker; refusing to fake a SAM 2 cut-out.")
    best = _best_frame_index(timeline)
    results = []
    frame_rgb = None
    if sam2_available():
        if video_path is None and session_id:
            from app.services import storage
            video_path = storage.original_path(session_id)
        frame_rgb = _extract_frame(video_path, best) if video_path else None
        if frame_rgb is None:
            raise RuntimeError(
                "SAM 2 is configured but the best frame could not be "
                "extracted from the uploaded video; failing the job.")
    for track in tracks:
        entry = {**track, "best_frame_index": best,
                 "cutout_ref": None, "sam_backend": "not_configured"}
        if sam2_available():
            entry.update(_run_sam2(frame_rgb, track, session_id))
        results.append(entry)
        log.info("cut-out track=%s best_frame=%d backend=%s",
                 track["track_id"], best, entry["sam_backend"])
    return results
