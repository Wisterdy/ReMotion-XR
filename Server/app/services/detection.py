"""Detection stage: observed image detections only.

Head/hand trajectories are tracking data, not image detections: the
wearer's head pose must never become a "person", and hand motion alone
must never invent an object. Subjects come from an actual image
detector/tracker, supplied in the timeline as ``detections`` (or
``observed_subjects``):

    {"track_id": "mug_01", "kind": "object"|"person",
     "label": "Coffee Mug", "confidence": 0.87,
     "bbox_xyxy": [x0, y0, x1, y1],
     "observations": [{"timestamp": 1.2, "bbox_xyxy": [...]}, ...]}

Stable track IDs and boxes are preserved verbatim. Without observed
detections this stage returns no tracks and the package records the
missing capability instead of fabricating subjects.
Track schema: {"track_id", "kind", "label", ...stats}
"""

import logging
import math

log = logging.getLogger("remotion.detection")


def _vec(point) -> tuple[float, float, float] | None:
    if isinstance(point, dict):
        try:
            return (float(point["x"]), float(point["y"]), float(point["z"]))
        except (KeyError, TypeError, ValueError):
            return None
    if isinstance(point, (list, tuple)) and len(point) == 3:
        try:
            return (float(point[0]), float(point[1]), float(point[2]))
        except (TypeError, ValueError):
            return None
    return None


def hand_positions(frames: list[dict], hand: str) -> list[tuple[float, float, float]]:
    key = "leftHandJoints" if hand == "left" else "rightHandJoints"
    positions = []
    for frame in frames or []:
        joints = frame.get(key) or []
        if joints:
            pos = _vec(joints[0])
            if pos is not None:
                positions.append(pos)
    return positions


def trajectory_stats(positions: list[tuple[float, float, float]]) -> dict:
    if not positions:
        return {"samples": 0, "path_length": 0.0, "bbox_min": None, "bbox_max": None}
    length = 0.0
    for a, b in zip(positions, positions[1:]):
        length += math.dist(a, b)
    mins = [min(p[i] for p in positions) for i in range(3)]
    maxs = [max(p[i] for p in positions) for i in range(3)]
    return {"samples": len(positions), "path_length": round(length, 4),
            "bbox_min": [round(v, 4) for v in mins],
            "bbox_max": [round(v, 4) for v in maxs]}


def _bbox(value) -> list[float] | None:
    if isinstance(value, (list, tuple)) and len(value) == 4:
        try:
            box = [float(v) for v in value]
        except (TypeError, ValueError):
            return None
        if all(math.isfinite(v) for v in box) and box[2] > box[0] and box[3] > box[1]:
            return box
    return None


def normalize_detections(raw) -> list[dict]:
    """Validate/dedupe observed detections; drop anything unobserved."""
    tracks, seen = [], set()
    for item in raw or []:
        if not isinstance(item, dict):
            continue
        track_id = str(item.get("track_id") or item.get("id") or "").strip()
        kind = item.get("kind")
        if not track_id or track_id in seen or kind not in ("object", "person"):
            continue
        try:
            confidence = float(item.get("confidence", 0.0))
        except (TypeError, ValueError):
            continue
        if not math.isfinite(confidence):
            continue
        seen.add(track_id)
        track = {
            "track_id": track_id, "kind": kind,
            "label": str(item.get("label") or track_id),
            "confidence": confidence,
            "detection_source": str(item.get("source") or "image-detector"),
        }
        box = _bbox(item.get("bbox_xyxy") or item.get("bbox"))
        if box is not None:
            track["bbox_xyxy"] = box
        observations = []
        for obs in item.get("observations") or []:
            if isinstance(obs, dict) and _bbox(obs.get("bbox_xyxy")) is not None:
                observations.append({
                    "timestamp": float(obs.get("timestamp", 0.0)),
                    "bbox_xyxy": _bbox(obs.get("bbox_xyxy"))})
        if observations:
            track["observations"] = observations
        tracks.append(track)
    return tracks


async def detect(timeline: dict, video_path=None) -> list[dict]:
    """Return only subjects an image detector actually observed.

    Order: explicit observed detections in the timeline (e.g. from the
    Meta identification chain), else the pinned image detector over
    the uploaded video. Tracking data alone yields no subjects.
    """
    raw = timeline.get("detections")
    if raw is None:
        raw = timeline.get("observed_subjects")
    tracks = normalize_detections(raw)
    if not tracks and video_path is not None:
        from app.services import image_detector

        if image_detector.detector_requested() == "fasterrcnn" \
                and not image_detector.detector_available():
            raise RuntimeError(
                "REMOTION_DETECTOR=fasterrcnn but torch/torchvision is "
                "not importable on this worker; refusing to skip detection.")
        if image_detector.detector_available():
            tracks = normalize_detections(
                image_detector.detect_in_video(video_path, timeline))
    log.info("detection: %d observed track(s): %s",
             len(tracks), [t["track_id"] for t in tracks])
    return tracks
