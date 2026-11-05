"""Meta Model API identification: concept cleaning, track shaping,
and SAM 2 fallback. All API calls are faked — no network, no key."""

import os
import sys
import tempfile
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
os.environ.setdefault("DATA_DIR", tempfile.mkdtemp(prefix="remotion-meta-test-"))

from app.services import meta_identify, segmentation  # noqa: E402


def test_clean_concepts_filters_people_surfaces_and_dupes():
    raw = ["Computer Monitor", "desk", "person", "computer monitor",
           "Mouse", "hand", "", "keyboard", "wall", "smartphone", "extra"]
    assert meta_identify.clean_concepts(raw) == [
        "computer monitor", "mouse", "keyboard", "smartphone"]


@pytest.mark.asyncio
async def test_identify_tracks_shapes_sam2_compatible_entries(tmp_path, monkeypatch):
    frame = np.zeros((100, 200, 3), dtype=np.uint8)

    async def fake_vision(frame_rgb):
        assert frame_rgb.shape == (200, 600, 3), "vision must see the sequence, not only the late keyframe"
        return ["computer monitor", "mouse"]

    async def fake_segment(frame_rgb, concept):
        mask = np.zeros((100, 200), dtype=np.uint8)
        mask[10:40, 20:90] = 1
        return mask, [20, 10, 70, 30], 2100

    monkeypatch.setattr(meta_identify, "vision_concepts", fake_vision)
    monkeypatch.setattr(meta_identify, "segment_concept", fake_segment)
    async def no_network_upload(*args):
        raise RuntimeError("Image fallback fixture; no network allowed")
    monkeypatch.setattr(meta_identify, "_upload_video", no_network_upload)
    class MetadataOnlyCapture:
        def get(self, prop):
            return 8 if prop == meta_identify.cv2.CAP_PROP_FPS else 80
        def release(self):
            pass
    monkeypatch.setattr(meta_identify.cv2, "VideoCapture", lambda *args: MetadataOnlyCapture())
    video = tmp_path / "clip.mp4"
    video.write_bytes(b"fake")
    base = [{"track_id": "capture_object", "kind": "object",
             "label": "Captured Object", "bbox_min": [0, 0, 0], "bbox_max": [1, 1, 1]}]
    tracks = await meta_identify.identify_tracks(
        video, {}, "sess1", base, 3, lambda p, i: frame)
    assert [t["track_id"] for t in tracks] == ["computer_monitor_01", "mouse_02"]
    assert all(t["sam_backend"] == "sam3" for t in tracks)
    assert tracks[0]["label"] == "Computer Monitor"
    assert tracks[0]["mask_bbox_xywh"] == [20, 10, 70, 30]
    assert tracks[0]["bbox_max"] == [1, 1, 1]  # 3D stats inherited
    assert Path(tracks[0]["cutout_ref"]).exists()


def test_choose_main_track_picks_dominant_object(monkeypatch):
    import meta_sam_parser
    from types import SimpleNamespace as NS

    monkeypatch.setattr(meta_sam_parser, "decode_mask_to_raster",
                        lambda m: m.raw)

    def mask_rec(oid, frame, w, h, fill):
        raw = bytes([fill]) * (w * h)
        return NS(kind="mask", object_id=oid,
                  frame=NS(frame_index=frame),
                  mask=NS(width=w, height=h, raw=raw))

    def box_rec(oid, frame):
        return NS(kind="box", object_id=oid, frame=NS(frame_index=frame),
                  left=0, top=0, right=10, bottom=10)

    records = [
        box_rec("0", 0), box_rec("0", 1), box_rec("1", 0),
        mask_rec("0", 0, 10, 10, 1), mask_rec("0", 1, 10, 10, 1),
        mask_rec("1", 0, 10, 10, 0),
    ]
    chosen = meta_identify.choose_main_track(records, 10, 10)
    assert chosen is not None
    oid, mask, bbox, area, frames_seen = chosen
    assert oid == "0"
    assert area == 100
    assert frames_seen == 2


def test_context_covers_sequence_independent_of_peak_motion_frame():
    indices = meta_identify.context_frame_indices(114)
    assert len(indices) == 6
    assert indices == sorted(set(indices))
    assert indices[0] < 15 and indices[-1] > 95
    assert all(b-a < 22 for a,b in zip(indices,indices[1:]))
    assert meta_identify.context_frame_indices(1) == [0]


def test_contact_sheet_preserves_chronology_and_all_observed_images():
    frames = [np.full((10, 20, 3), i, dtype=np.uint8) for i in range(6)]
    sheet = meta_identify.contact_sheet(frames)
    assert sheet.shape == (20, 60, 3)
    assert [int(sheet[(i//3)*10, (i%3)*20, 0]) for i in range(6)] == list(range(6))


def test_grab_frame_index_maps_time_to_frame():
    timeline = {
        "frames": [{"timestamp": i * 0.1} for i in range(101)],
        "grab_events": [{"timestamp": 5.0, "type": "grab"}],
    }
    assert meta_identify._grab_frame_index(timeline) == 50
    assert meta_identify._grab_frame_index({"frames": [], "grab_events": []}) is None


@pytest.mark.asyncio
async def test_missing_middle_frame_object_uses_observed_earlier_frame(tmp_path, monkeypatch):
    class Capture:
        def get(self, prop): return 8 if prop == meta_identify.cv2.CAP_PROP_FPS else 80
        def release(self): pass
    monkeypatch.setattr(meta_identify.cv2,'VideoCapture',lambda *_:Capture())
    async def vision(_): return ['generic tool']
    async def upload(_): return 'file-fixture'
    async def video(*_): return None
    first=meta_identify.context_frame_indices(80)[0]
    async def segment(image, concept):
        if int(image[0,0,0]) != first:return None
        mask=np.ones((20,30),dtype=np.uint8)
        return mask,[0,0,30,20],600
    monkeypatch.setattr(meta_identify,'vision_concepts',vision)
    monkeypatch.setattr(meta_identify,'_upload_video',upload)
    monkeypatch.setattr(meta_identify,'segment_concept_video',video)
    monkeypatch.setattr(meta_identify,'segment_concept',segment)
    path=tmp_path/'video.mp4';path.write_bytes(b'fixture')
    tracks=await meta_identify.identify_tracks(path,{},'early',[],70,
        lambda p,i:np.full((20,30,3),i,dtype=np.uint8))
    assert tracks[0]['best_frame_index']==first
    assert tracks[0]['video_observations']==[]  # still image is not an observed trajectory


@pytest.mark.asyncio
async def test_cut_out_falls_back_when_meta_fails(monkeypatch):
    monkeypatch.setenv("MODEL_API_KEY", "test-key")
    monkeypatch.delenv("REMOTION_SAM2_CHECKPOINT", raising=False)

    async def boom(*a, **k):
        raise RuntimeError("api down")

    monkeypatch.setattr(meta_identify, "identify_tracks", boom)
    tracks = [{"track_id": "capture_object", "kind": "object", "label": "Captured Object"}]
    out = await segmentation.cut_out(tracks, {"frames": []}, session_id="s",
                                     video_path=Path("nope.mp4"))
    assert out[0]["sam_backend"] == "not_configured"  # SAM 2 fallback path


@pytest.mark.asyncio
async def test_cut_out_uses_meta_when_configured(monkeypatch):
    monkeypatch.setenv("MODEL_API_KEY", "test-key")

    async def fake_identify(*a, **k):
        return [{"track_id": "mouse_01", "kind": "object", "label": "Mouse",
                 "sam_backend": "sam3", "cutout_ref": None, "best_frame_index": 0}]

    monkeypatch.setattr(meta_identify, "identify_tracks", fake_identify)
    tracks = [{"track_id": "capture_object", "kind": "object"}]
    out = await segmentation.cut_out(tracks, {"frames": []}, session_id="s")
    assert out[0]["track_id"] == "mouse_01"
    assert out[0]["sam_backend"] == "sam3"
