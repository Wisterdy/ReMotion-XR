"""Pinned image detector: establishes observed subjects from video.

Model: torchvision Faster R-CNN ResNet-50 FPN v2, COCO weights
(pinned URL + sha256 below; BSD-3-Clause, torchvision). This is a
real image detector — head/hand trajectories are never subjects.

Frames are sampled by capture timestamp (video PTS via OpenCV seek),
detected per frame, and associated across frames by box IoU into
stable track IDs. Output matches detection.normalize_detections:
{track_id, kind, label, confidence, bbox_xyxy, observations[]}.

Enablement: active when a readable video exists and torchvision is
importable. REMOTION_DETECTOR=off disables it; REMOTION_DETECTOR=
fasterrcnn requires it (a load failure then fails the job instead of
silently returning no subjects).
"""

import hashlib
import importlib.util
import logging
import os
from pathlib import Path

log = logging.getLogger("remotion.image_detector")

MODEL_NAME = "fasterrcnn_resnet50_fpn_v2"
WEIGHTS_URL = ("https://download.pytorch.org/models/"
               "fasterrcnn_resnet50_fpn_v2_coco-dd69338a.pth")
# Verified locally 2026-11-03 against download.pytorch.org.
WEIGHTS_SHA256 = "dd69338a24b8d7381807e247652bdc356325bcbaf1cd3e092e00e0a1a58706bf"
WEIGHTS_FILE = "fasterrcnn_resnet50_fpn_v2_coco-dd69338a.pth"
SCORE_THRESHOLD = float(os.environ.get("REMOTION_DETECTOR_SCORE", "0.5"))
MAX_SAMPLE_FRAMES = 8
IOU_ASSOCIATION = 0.3

_model = None
_categories: list[str] = []


def detector_requested() -> str:
    return os.environ.get("REMOTION_DETECTOR", "auto").lower()


def detector_available() -> bool:
    if detector_requested() == "off":
        return False
    return (importlib.util.find_spec("torch") is not None
            and importlib.util.find_spec("torchvision") is not None)


def _weights_path() -> Path:
    cache = Path(os.environ.get(
        "REMOTION_DETECTOR_CACHE",
        str(Path(__file__).resolve().parent.parent.parent / "checkpoints")))
    cache.mkdir(parents=True, exist_ok=True)
    path = cache / WEIGHTS_FILE
    if not path.exists():
        import urllib.request

        log.info("downloading pinned detector weights %s", WEIGHTS_URL)
        urllib.request.urlretrieve(WEIGHTS_URL, path)
    expected = os.environ.get("REMOTION_DETECTOR_SHA256", WEIGHTS_SHA256)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != expected:
        raise RuntimeError(
            f"detector weights sha256 mismatch: {digest} != {expected}")
    return path


def _get_model():
    """Load the pinned detector once per worker."""
    global _model, _categories
    if _model is None:
        import torch
        from torchvision.models.detection import (
            FasterRCNN_ResNet50_FPN_V2_Weights, fasterrcnn_resnet50_fpn_v2)

        weights = FasterRCNN_ResNet50_FPN_V2_Weights.COCO_V1
        _categories = list(weights.meta["categories"])
        model = fasterrcnn_resnet50_fpn_v2(weights=None)
        state = torch.load(_weights_path(), map_location="cpu", weights_only=True)
        model.load_state_dict(state)
        device = "cuda" if (torch.cuda.is_available()
                            and os.environ.get("REMOTION_DETECTOR_DEVICE", "cuda") == "cuda") \
            else "cpu"
        _model = model.to(device).eval()
        _model._remotion_device = device
        log.info("detector ready: %s on %s (%d categories)",
                 MODEL_NAME, device, len(_categories))
    return _model


def _infer(frame_rgb) -> list[dict]:
    """One frame -> [{label, score, bbox_xyxy}] above threshold."""
    import torch
    from torchvision.transforms import functional as F

    model = _get_model()
    tensor = F.to_tensor(frame_rgb).to(model._remotion_device)
    with torch.no_grad():
        out = model([tensor])[0]
    results = []
    for box, label, score in zip(
            out["boxes"].cpu().tolist(), out["labels"].cpu().tolist(),
            out["scores"].cpu().tolist()):
        if score < SCORE_THRESHOLD:
            continue
        name = _categories[label] if 0 <= label < len(_categories) else str(label)
        results.append({"label": name, "score": float(score),
                        "bbox_xyxy": [float(v) for v in box]})
    return results


def _iou(a, b) -> float:
    x0, y0 = max(a[0], b[0]), max(a[1], b[1])
    x1, y1 = min(a[2], b[2]), min(a[3], b[3])
    inter = max(0.0, x1 - x0) * max(0.0, y1 - y0)
    area = lambda b: max(0.0, b[2] - b[0]) * max(0.0, b[3] - b[1])
    union = area(a) + area(b) - inter
    return inter / union if union > 0 else 0.0


def associate_tracks(per_frame: list[tuple[float, list[dict]]]) -> list[dict]:
    """IoU-associate per-frame detections into stable observed tracks."""
    import re

    def slug(text: str) -> str:
        return re.sub(r"[^a-z0-9]+", "_", text.lower()).strip("_") or "object"

    tracks: list[dict] = []
    for timestamp, detections in per_frame:
        for det in detections:
            best, best_iou = None, IOU_ASSOCIATION
            for track in tracks:
                if track["label"] != det["label"]:
                    continue
                iou = _iou(track["bbox_xyxy"], det["bbox_xyxy"])
                if iou > best_iou:
                    best, best_iou = track, iou
            if best is None:
                best = {
                    "track_id": f"{slug(det['label'])}_{len(tracks) + 1:02d}",
                    "kind": "person" if det["label"] == "person" else "object",
                    "label": det["label"],  # raw during association
                    "confidence": det["score"],
                    "bbox_xyxy": det["bbox_xyxy"],
                    "source": MODEL_NAME,
                    "observations": [],
                }
                tracks.append(best)
            best["bbox_xyxy"] = det["bbox_xyxy"]
            best["confidence"] = max(best["confidence"], det["score"])
            best["observations"].append(
                {"timestamp": timestamp, "bbox_xyxy": det["bbox_xyxy"]})
    for track in tracks:
        track["label"] = track["label"].title()
    return tracks


def sample_timestamps(timeline: dict, duration: float) -> list[float]:
    """Keyframe times by timestamp: grab moments + evenly spaced."""
    times = set()
    for event in timeline.get("grab_events") or []:
        try:
            times.add(float(event.get("timestamp", 0.0)))
        except (TypeError, ValueError):
            pass
    if duration > 0:
        for i in range(MAX_SAMPLE_FRAMES):
            times.add(duration * i / max(1, MAX_SAMPLE_FRAMES - 1))
    return sorted(t for t in times if 0.0 <= t <= max(duration, 0.0))[:MAX_SAMPLE_FRAMES]


def detect_in_video(video_path, timeline: dict,
                    infer=None) -> list[dict]:
    """Observed tracks from the actual uploaded video (never tracking)."""
    if video_path is None or not Path(video_path).exists():
        return []
    import cv2

    cap = cv2.VideoCapture(str(video_path))
    try:
        fps = cap.get(cv2.CAP_PROP_FPS) or 0.0
        count = cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0.0
        duration = float(timeline.get("duration_seconds") or 0.0)
        if duration <= 0 and fps > 0 and count > 0:
            duration = count / fps
        if duration <= 0:
            return []
        infer = infer or _infer
        per_frame = []
        for t in sample_timestamps(timeline, duration):
            cap.set(cv2.CAP_PROP_POS_MSEC, t * 1000.0)
            ok, frame = cap.read()
            if not ok:
                continue
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            per_frame.append((t, infer(rgb)))
        tracks = associate_tracks(per_frame)
        log.info("image detector: %d track(s) from %d sampled frame(s)",
                 len(tracks), len(per_frame))
        return tracks
    finally:
        cap.release()
