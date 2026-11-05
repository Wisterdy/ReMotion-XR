"""Pinned image detector: stable tracks from observed boxes only."""

import os
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
os.environ.setdefault("DATA_DIR", tempfile.mkdtemp(prefix="remotion-det-test-"))

from app.services import detection, image_detector  # noqa: E402


def test_associate_tracks_keeps_two_objects_distinct():
    per_frame = [
        (0.0, [{"label": "cup", "score": 0.9, "bbox_xyxy": [0, 0, 40, 40]},
               {"label": "laptop", "score": 0.8, "bbox_xyxy": [200, 0, 320, 90]}]),
        (0.5, [{"label": "cup", "score": 0.85, "bbox_xyxy": [5, 2, 45, 42]},
               {"label": "laptop", "score": 0.82, "bbox_xyxy": [205, 3, 325, 93]}]),
    ]
    tracks = image_detector.associate_tracks(per_frame)
    assert [t["track_id"] for t in tracks] == ["cup_01", "laptop_02"]
    assert all(len(t["observations"]) == 2 for t in tracks)
    people = image_detector.associate_tracks(
        [(0.0, [{"label": "person", "score": 0.9, "bbox_xyxy": [0, 0, 50, 150]}])])
    assert people[0]["kind"] == "person"


def test_detect_in_video_with_injected_infer(tmp_path):
    import cv2
    import numpy as np

    video = tmp_path / "clip.mp4"
    writer = cv2.VideoWriter(str(video), cv2.VideoWriter_fourcc(*"mp4v"),
                             10, (320, 240))
    for _ in range(20):
        writer.write(np.zeros((240, 320, 3), dtype=np.uint8))
    writer.release()

    def fake_infer(frame_rgb):
        return [{"label": "cup", "score": 0.9,
                 "bbox_xyxy": [10.0, 10.0, 60.0, 60.0]}]

    timeline = {"duration_seconds": 2.0, "grab_events": [{"timestamp": 0.5}]}
    tracks = image_detector.detect_in_video(video, timeline, infer=fake_infer)
    assert len(tracks) == 1 and tracks[0]["label"] == "Cup"
    # The same shape flows through detection.normalize_detections.
    assert detection.normalize_detections(tracks)[0]["track_id"] == "cup_01"


def test_detect_without_video_or_detections_stays_empty():
    import asyncio

    assert asyncio.run(detection.detect({"frames": []})) == []
