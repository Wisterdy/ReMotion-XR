"""Gateway /v1 contract: auth boundary, idempotency, lifecycle, scopes.

Run: python -m pytest tests/test_gateway_v1.py -q   (from Server/)
"""

import os
import sys
import tempfile
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
os.environ["DATA_DIR"] = tempfile.mkdtemp(prefix="remotion-v1-test-")
os.environ["REMOTION_STUB_DELAY_SECONDS"] = "0.01"

from fastapi.testclient import TestClient  # noqa: E402

from app.main import app  # noqa: E402
from app.services import storage  # noqa: E402

client = TestClient(app)
TIMELINE = '{"action_sequence":"recorded_action","duration_seconds":1.0,"frames":[]}'
KEY = {"X-API-Key": "test-key"}


def _wait_ready(session_id: str, headers=KEY) -> dict:
    for _ in range(100):
        s = client.get(f"/v1/sessions/{session_id}", headers=headers).json()
        if s["state"] in ("READY", "FAILED"):
            return s
        time.sleep(0.1)
    raise AssertionError("pipeline never finished")


def _create(headers=KEY, idem=None):
    h = dict(headers)
    if idem:
        h["Idempotency-Key"] = idem
    return client.post("/v1/sessions", headers=h,
                       files={"video": ("clip.mp4", b"fake-video", "video/mp4")},
                       data={"timeline": TIMELINE})


def test_auth_required_when_key_configured(monkeypatch):
    monkeypatch.setenv("REMOTION_API_KEY", "test-key")
    assert client.post("/v1/sessions", files={
        "video": ("c.mp4", b"x", "video/mp4")},
        data={"timeline": TIMELINE}).status_code == 401
    assert client.get("/v1/sessions/nope").status_code == 401
    assert client.get("/health").status_code == 200  # health stays open


def test_v1_upload_ready_download_rename_rebuild(monkeypatch):
    monkeypatch.setenv("REMOTION_API_KEY", "test-key")
    r = _create(idem="upload-1")
    assert r.status_code == 200, r.text
    sid = r.json()["session_id"]
    assert r.json()["capabilities"]["original_video"] is True

    # Duplicate submit with the same idempotency key = one logical job.
    dup = _create(idem="upload-1")
    assert dup.json()["session_id"] == sid

    status = _wait_ready(sid)
    assert status["state"] == "READY"
    assert status["capabilities"]["original_video"] is True
    assert status["package_hash"]

    assert client.get(f"/v1/sessions/{sid}/video", headers=KEY).status_code == 200
    assert client.get(f"/v1/sessions/{sid}/package", headers=KEY).status_code == 200

    renamed = client.patch(f"/v1/sessions/{sid}", headers=KEY,
                           json={"display_name": "My Capture"})
    assert renamed.status_code == 200
    assert renamed.json()["display_name"] == "My Capture"
    assert storage.load_job(sid).display_name == "My Capture"

    # Scoped delete: virtual only keeps the original + rebuild path.
    assert client.delete(f"/v1/sessions/{sid}?scope=virtual",
                         headers=KEY).status_code == 200
    assert not storage.virtual_path(sid).exists()
    assert storage.original_path(sid).exists()
    rebuilt = client.post(f"/v1/sessions/{sid}/rebuild", headers=KEY)
    assert rebuilt.status_code == 200
    assert _wait_ready(sid)["state"] == "READY"

    # Scoped delete: all tombstones; the session cannot come back.
    assert client.delete(f"/v1/sessions/{sid}?scope=all",
                         headers=KEY).status_code == 200
    assert client.get(f"/v1/sessions/{sid}", headers=KEY).status_code == 404
    assert client.post(f"/v1/sessions/{sid}/rebuild",
                       headers=KEY).status_code == 404
    assert storage.is_tombstoned(sid)


def test_v1_rejects_empty_video_and_bad_scope(monkeypatch):
    monkeypatch.setenv("REMOTION_API_KEY", "test-key")
    r = client.post("/v1/sessions", headers=KEY,
                    files={"video": ("c.mp4", b"", "video/mp4")},
                    data={"timeline": TIMELINE})
    assert r.status_code == 422
    sid = _create().json()["session_id"]
    _wait_ready(sid)
    assert client.delete(f"/v1/sessions/{sid}?scope=everything",
                         headers=KEY).status_code == 422
    assert client.get("/v1/sessions/..%2Fescape", headers=KEY).status_code in (404, 422)
