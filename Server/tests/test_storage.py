"""Unit tests for the storage layer + key-pose stub (no HTTP needed)."""

import asyncio
import os
import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
os.environ.setdefault("DATA_DIR", tempfile.mkdtemp(prefix="remotion-unit-"))
os.environ["REMOTION_STUB_DELAY_SECONDS"] = "0.01"

from app.models import SessionState, SessionStatus  # noqa: E402
from app.services import keypose, storage  # noqa: E402


def test_job_roundtrip_survives_reload():
    storage.save_job(SessionStatus(session_id="s1", state=SessionState.READY,
                                   progress=100, message="done"),
                     {"action_sequence": "x", "duration_seconds": 2.0})
    loaded = storage.load_job("s1")
    assert loaded is not None and loaded.state == SessionState.READY
    assert storage.load_timeline("s1")["duration_seconds"] == 2.0
    assert any(s.session_id == "s1" for s in storage.list_jobs())


def test_delete_semantics():
    storage.save_job(SessionStatus(session_id="s2", state=SessionState.READY, progress=100))
    storage.original_path("s2").write_bytes(b"video")
    storage.virtual_path("s2").write_bytes(b"scene")

    assert storage.delete_original("s2") is True
    assert not storage.original_path("s2").exists()
    assert storage.virtual_path("s2").exists()  # virtual untouched

    assert storage.delete_virtual("s2") is True
    assert not storage.virtual_path("s2").exists()
    assert storage.load_job("s2") is not None  # job untouched

    storage.original_path("s2").write_bytes(b"video")
    assert storage.delete_all("s2") is True
    assert not storage.original_path("s2").exists()
    assert storage.load_job("s2") is None
    assert storage.delete_all("s2") is False  # already gone


def test_keypose_stub_shape():
    out = asyncio.run(keypose.extract_key_poses(
        {"action_sequence": "dribble_crossover_layup", "duration_seconds": 5.2}))
    assert out["action_sequence"] == "dribble_crossover_layup"
    poses = out["key_poses"]
    assert len(poses) >= 2
    assert any(p["is_best_pose"] for p in poses)
    assert all(0.0 <= p["timestamp"] <= 5.2 for p in poses)
