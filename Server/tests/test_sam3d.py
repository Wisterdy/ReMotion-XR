"""SAM 3D wiring: endpoint client config, reconstruction choice,
handler validation. All network/model calls are faked."""

import os
import sys
import tempfile
from pathlib import Path

import numpy as np
import pytest

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
os.environ.setdefault("DATA_DIR", tempfile.mkdtemp(prefix="remotion-sam3d-test-"))

from app.services import reconstruction, sam3d  # noqa: E402

FAKE_GLB = b"glTF" + b"\x02\x00\x00\x00" + b"\x00" * 32


def test_sam3d_configured_by_env(monkeypatch):
    monkeypatch.delenv("REMOTION_SAM3D_ENDPOINT_URL", raising=False)
    assert sam3d.sam3d_configured() is False
    monkeypatch.setenv("REMOTION_SAM3D_ENDPOINT_URL", "https://api.runpod.ai/v2/x")
    assert sam3d.sam3d_configured() is True


@pytest.mark.asyncio
async def test_reconstruction_uses_sam3d_when_configured(monkeypatch, tmp_path):
    import cv2

    monkeypatch.setenv("REMOTION_SAM3D_ENDPOINT_URL", "https://api.runpod.ai/v2/x")
    mask_path = tmp_path / "mask.png"
    cv2.imwrite(str(mask_path), np.full((20, 30), 255, dtype=np.uint8))

    async def fake_reconstruct(frame, mask, seed=42):
        assert frame is not None and mask.any()
        return FAKE_GLB

    monkeypatch.setattr(sam3d, "reconstruct_object", fake_reconstruct)
    # _try_sam3d imports the client lazily from the same module object.
    monkeypatch.setattr(reconstruction, "_extract_frame", None, raising=False)
    import app.services.segmentation as segmentation
    monkeypatch.setattr(segmentation, "_extract_frame",
                        lambda p, i: np.zeros((20, 30, 3), dtype=np.uint8))
    video = tmp_path / "clip.mp4"
    video.write_bytes(b"fake")
    cutouts = [{"track_id": "keyboard_01", "kind": "object",
                "cutout_ref": str(mask_path), "best_frame_index": 0}]
    assets = await reconstruction.reconstruct(cutouts, video_path=video)
    assert assets[0]["model_bytes"] == FAKE_GLB
    assert assets[0]["reconstruction_backend"] == "sam3d"


@pytest.mark.asyncio
async def test_reconstruction_falls_back_when_sam3d_fails(monkeypatch, tmp_path):
    import cv2

    monkeypatch.setenv("REMOTION_SAM3D_ENDPOINT_URL", "https://api.runpod.ai/v2/x")
    mask_path = tmp_path / "mask.png"
    cv2.imwrite(str(mask_path), np.full((20, 30), 255, dtype=np.uint8))

    async def boom(frame, mask, seed=42):
        raise RuntimeError("endpoint down")

    monkeypatch.setattr(sam3d, "reconstruct_object", boom)
    import app.services.segmentation as segmentation
    monkeypatch.setattr(segmentation, "_extract_frame",
                        lambda p, i: np.zeros((20, 30, 3), dtype=np.uint8))
    video = tmp_path / "clip.mp4"
    video.write_bytes(b"fake")
    cutouts = [{"track_id": "keyboard_01", "kind": "object",
                "cutout_ref": str(mask_path), "best_frame_index": 0,
                "mask_bbox_xywh": [0, 0, 30, 20]}]
    assets = await reconstruction.reconstruct(cutouts, video_path=video)
    assert assets[0]["model_bytes"][:4] == b"glTF"
    assert assets[0]["reconstruction_backend"] in ("sam-mask-mesh", "preview-geometry")


def test_handler_requires_payloads():
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
    import sam3d_handler

    assert "error" in sam3d_handler.handle({"input": {}})
    assert "error" in sam3d_handler.handle(
        {"input": {"image_b64": "!!!", "mask_b64": "!!!"}})

@pytest.mark.asyncio
async def test_timeout_cancels_remote_job(monkeypatch):
    calls = []
    async def post(client, path, body=None):
        calls.append(path)
        return {'id': 'orphan-test'}
    monkeypatch.setattr(sam3d, '_post', post)
    monkeypatch.setattr(sam3d, '_TIMEOUT_SECONDS', 0)
    with pytest.raises(RuntimeError, match='timed out'):
        await sam3d.reconstruct_object(np.zeros((8,8,3), dtype=np.uint8), np.ones((8,8), dtype=bool))
    assert calls == ['/run', '/cancel/orphan-test']

@pytest.mark.asyncio
async def test_completed_job_is_not_cancelled(monkeypatch):
    import base64
    calls = []
    async def post(client, path, body=None):
        calls.append(path)
        return {'id': 'complete-test'}
    async def get(client, path):
        return {'status': 'COMPLETED', 'output': {'glb_b64': base64.b64encode(FAKE_GLB).decode()}}
    async def sleep(_):
        pass
    monkeypatch.setattr(sam3d, '_post', post)
    monkeypatch.setattr(sam3d, '_get', get)
    monkeypatch.setattr(sam3d.asyncio, 'sleep', sleep)
    result = await sam3d.reconstruct_object(np.zeros((8,8,3), dtype=np.uint8), np.ones((8,8), dtype=bool))
    assert result == FAKE_GLB
    assert calls == ['/run']
