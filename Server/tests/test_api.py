"""Focused tests: pipeline READY flow, deletes, serverless IN_QUEUE.

Run: python -m pytest tests/ -q   (from Server/)
Isolated DATA_DIR per run so the dev database is never touched.
"""

import os
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
os.environ["DATA_DIR"] = tempfile.mkdtemp(prefix="remotion-test-")
os.environ["REMOTION_STUB_DELAY_SECONDS"] = "0.01"

from fastapi.testclient import TestClient  # noqa: E402

from app.main import app  # noqa: E402
from app.services import storage  # noqa: E402

client = TestClient(app)
TIMELINE = '{"action_sequence":"dribble_crossover_layup","duration_seconds":5.2,"frames":[]}'


def _upload() -> str:
    r = client.post("/api/sessions/upload",
                    files={"video": ("clip.mp4", b"fake", "video/mp4")},
                    data={"timeline": TIMELINE})
    assert r.status_code == 200, r.text
    return r.json()["session_id"]


def _wait_ready(session_id: str) -> dict:
    for _ in range(100):
        s = client.get(f"/api/sessions/{session_id}/status").json()
        if s["state"] in ("READY", "FAILED"):
            return s
        time.sleep(0.1)
    raise AssertionError("pipeline never finished")


def test_health():
    assert client.get("/health").json()["status"] == "ok"


def test_upload_accepts_large_timeline_part():
    # Regression: timelines with joint rotations exceed the framework's
    # 1MB multipart part default; real headset uploads got HTTP 400.
    big_timeline = '{"action_sequence":"recorded_action","frames":[' + \
        ','.join('{"timestamp":0,"pad":"' + 'x' * 4000 + '"}' for _ in range(400)) + ']}'
    assert len(big_timeline) > 1_500_000
    r = client.post("/api/sessions/upload",
                    files={"video": ("clip.mp4", b"fake", "video/mp4")},
                    data={"timeline": big_timeline})
    assert r.status_code == 200, r.text
    assert _wait_ready(r.json()["session_id"])["state"] == "READY"


def test_upload_ready_download_then_deletes():
    sid = _upload()
    assert _wait_ready(sid)["state"] == "READY"
    assert storage.original_path(sid).exists()
    assert storage.virtual_path(sid).exists()
    assert client.get(f"/api/sessions/{sid}/download").status_code == 200

    # Delete original only: scene still downloadable.
    assert client.delete(f"/api/sessions/{sid}/original").status_code == 200
    assert not storage.original_path(sid).exists()
    assert storage.virtual_path(sid).exists()

    # Delete virtual only: job remains, rebuild possible.
    assert client.delete(f"/api/sessions/{sid}/virtual").status_code == 200
    assert not storage.virtual_path(sid).exists()
    assert client.get(f"/api/sessions/{sid}/status").status_code == 200

    # Delete everything.
    assert client.delete(f"/api/sessions/{sid}").status_code == 200
    assert client.get(f"/api/sessions/{sid}/status").status_code == 404
    assert client.delete(f"/api/sessions/{sid}").status_code == 404


def _sample_timeline() -> dict:
    frames = []
    for i in range(9):
        frames.append({
            "timestamp": i * 0.1,
            "headPosition": {"x": 0.0, "y": 1.5, "z": 0.0},
            "leftHandJoints": [{"x": -0.2, "y": 1.0, "z": 0.4}],
            "rightHandJoints": [{"x": 0.2 + 0.05 * i, "y": 1.0 + 0.02 * i, "z": 0.4}],
        })
    return {"action_sequence": "recorded_action", "duration_seconds": 0.8,
            "frames": frames,
            # Observed image detections (the only subject source).
            "detections": [
                {"track_id": "capture_object", "kind": "object",
                 "label": "Captured Object", "confidence": 0.9,
                 "bbox_xyxy": [10, 10, 60, 60]},
                {"track_id": "person_01", "kind": "person",
                 "label": "Person", "confidence": 0.8,
                 "bbox_xyxy": [100, 20, 180, 200]}],
            "grab_events": [{"timestamp": 0.2, "hand": "right",
                             "objectId": "capture_object", "type": "grab"},
                            {"timestamp": 0.6, "hand": "right",
                             "objectId": "capture_object", "type": "release"}]}


def test_detection_and_keyposes_are_computed_from_timeline():
    import asyncio as _asyncio

    from app.services import detection, keypose

    timeline = _sample_timeline()
    tracks = _asyncio.run(detection.detect(timeline))
    kinds = {t["kind"] for t in tracks}
    assert kinds == {"object", "person"}
    obj = next(t for t in tracks if t["kind"] == "object")
    assert obj["track_id"] == "capture_object"
    assert obj["bbox_xyxy"] == [10, 10, 60, 60]
    # No observed detections -> no fabricated subjects from tracking.
    bare = {"frames": timeline["frames"], "grab_events": timeline["grab_events"]}
    assert _asyncio.run(detection.detect(bare)) == []

    poses = _asyncio.run(keypose.extract_key_poses(timeline))["key_poses"]
    labels = {p["label"] for p in poses}
    assert {"Start", "Grab Object", "Release Object"} <= labels
    assert 2 <= len(poses) <= 12


def test_package_contains_real_glb_and_no_stub_markers():
    import json as _json_upload
    r = client.post("/api/sessions/upload",
                    files={"video": ("clip.mp4", b"fake", "video/mp4")},
                    data={"timeline": _json_upload.dumps(_sample_timeline())})
    assert r.status_code == 200, r.text
    sid = r.json()["session_id"]
    assert _wait_ready(sid)["state"] == "READY"
    import io
    import json as _json
    import zipfile as _zip

    data = client.get(f"/api/sessions/{sid}/download").content
    with _zip.ZipFile(io.BytesIO(data)) as zf:
        names = zf.namelist()
        scene = _json.loads(zf.read("scene.json"))
    assert scene["format"] == "remotion-2"
    assert scene["schema_version"] == 2
    person = next(a for a in scene["assets"] if a["kind"] == "person")
    assert person["reconstruction_backend"] == "unavailable"
    assert person["model"] is None
    assert "stub" not in _json.dumps(scene).lower()
    assert any(n.endswith(".glb") for n in names), names
    # GLB magic header.
    with _zip.ZipFile(io.BytesIO(data)) as zf:
        glb_name = next(n for n in names if n.endswith(".glb"))
        assert zf.read(glb_name)[:4] == b"glTF"


def test_sam2_checkpoint_without_package_fails_closed(monkeypatch):
    monkeypatch.setenv("REMOTION_SAM2_CHECKPOINT", "/tmp/nope.pt")
    sid = _upload()
    status = _wait_ready(sid)
    assert status["state"] == "FAILED"
    assert "SAM 2" in (status.get("error") or "")


def test_require_sam_fails_closed_without_sam(monkeypatch):
    monkeypatch.setenv("REMOTION_REQUIRE_SAM", "1")
    sid = _upload()
    status = _wait_ready(sid)
    assert status["state"] == "FAILED"
    assert "SAM" in (status.get("error") or "") or "sam" in (status.get("message") or "").lower()


def test_serverless_fails_retryably_without_gpu(monkeypatch):
    # A finished handler cannot requeue a platform job; no-GPU is a
    # retryable failure, never a synthesized IN_QUEUE result.
    monkeypatch.setenv("REMOTION_GPU_AVAILABLE", "0")
    import runpod_handler

    out = runpod_handler.handle({"input": {"session_id": "q1", "timeline": {}}})
    assert out["state"] == "FAILED" and out["retryable"] is True


def test_serverless_rejects_bad_keys_and_actions(monkeypatch):
    monkeypatch.setenv("REMOTION_GPU_AVAILABLE", "1")
    import runpod_handler

    bad = runpod_handler.handle({"input": {
        "schema_version": 1, "session_id": "q9",
        "video_key": "sessions/other/source.mp4", "timeline": {}}})
    assert bad["state"] == "FAILED"
    bad = runpod_handler.handle({"input": {
        "session_id": "../escape", "timeline": {}}})
    assert bad["state"] == "FAILED"
    bad = runpod_handler.handle({"input": {
        "session_id": "q9", "action": "explode"}})
    assert bad["state"] == "FAILED"


def test_serverless_ready_with_gpu(monkeypatch):
    monkeypatch.setenv("REMOTION_GPU_AVAILABLE", "1")
    import runpod_handler

    from app.services import storage
    storage.original_path("q2").parent.mkdir(parents=True, exist_ok=True)
    storage.original_path("q2").write_bytes(b"video-fixture")
    out = runpod_handler.handle({"input": {"session_id": "q2", "timeline": {"duration_seconds": 1.0}}})
    assert out["state"] == "READY"
    assert storage.virtual_path("q2").exists()
    # Client contract: READY carries a download_url key (None until
    # REMOTION_PACKAGE_BASE_URL publishes packages); the Quest app fails
    # closed on None instead of treating a worker path as a download.
    assert "download_url" in out
    monkeypatch.setenv("REMOTION_PACKAGE_BASE_URL", "https://cdn.example/pkg/")
    from app.services import storage
    storage.original_path("q2").parent.mkdir(parents=True, exist_ok=True)
    storage.original_path("q2").write_bytes(b"video-fixture")
    out = runpod_handler.handle({"input": {"session_id": "q2", "timeline": {"duration_seconds": 1.0}}})
    assert out["download_url"] == "https://cdn.example/pkg/q2.remotion"
    from app.services import storage
    storage.original_path("q2").parent.mkdir(parents=True, exist_ok=True)
    storage.original_path("q2").write_bytes(b"video-fixture")
    out = runpod_handler.handle({"input": {"session_id": "q2", "action": "delete_all"}})
    assert out["deleted"] == "all"
    assert not storage.virtual_path("q2").exists()


def test_serverless_runs_inside_existing_event_loop(monkeypatch):
    import asyncio
    import inspect
    import runpod_handler
    from app.services import storage

    monkeypatch.setenv("REMOTION_GPU_AVAILABLE", "1")
    storage.original_path("asyncq").parent.mkdir(parents=True, exist_ok=True)
    storage.original_path("asyncq").write_bytes(b"video-fixture")

    async def invoke_like_runpod():
        assert inspect.iscoroutinefunction(runpod_handler.handle_async)
        return await runpod_handler.handle_async({"input": {
            "session_id": "asyncq", "timeline": {"duration_seconds": 1.0}}})

    result = asyncio.run(invoke_like_runpod())
    assert result["state"] == "READY"
    assert storage.virtual_path("asyncq").exists()
