"""Client for the SAM 3D Objects serverless endpoint.

Configured purely by env (set from GitHub Secrets / endpoint config):
  REMOTION_SAM3D_ENDPOINT_URL   e.g. https://api.runpod.ai/v2/<id>
  REMOTION_API_KEY              bearer for that endpoint (falls back
                                to RUNPOD_API_KEY)

When unconfigured, sam3d_configured() is False and reconstruction
keeps its mask-mesh/trajectory fallbacks. When configured, a failure
raises to the caller, which falls back per-object and records the
backend that actually produced each model — never a fake 'sam3d'.
"""

import asyncio
import base64
import logging
import os

import cv2
import numpy as np

log = logging.getLogger("remotion.sam3d")

_POLL_SECONDS = 10
_TIMEOUT_SECONDS = 900  # cold start + checkpoint download + inference


def sam3d_configured() -> bool:
    return bool(os.environ.get("REMOTION_SAM3D_ENDPOINT_URL"))


def _endpoint() -> str:
    return os.environ["REMOTION_SAM3D_ENDPOINT_URL"].rstrip("/")


def _api_key() -> str:
    # Server-side RunPod platform credential only. REMOTION_API_KEY is
    # the application gateway credential and must never be forwarded
    # as a RunPod Bearer [REDACTED]
    return os.environ.get("RUNPOD_API_KEY", "")


async def _post(client, path: str, body: dict | None = None) -> dict:
    resp = await client.post(
        f"{_endpoint()}{path}",
        json=body,
        headers={"Authorization": f"Bearer {_api_key()}",
                 "Content-Type": "application/json"},
    )
    resp.raise_for_status()
    return resp.json()


async def _get(client, path: str) -> dict:
    """RunPod job status is a GET, never a POST."""
    resp = await client.get(
        f"{_endpoint()}{path}",
        headers={"Authorization": f"Bearer {_api_key()}"},
    )
    resp.raise_for_status()
    return resp.json()


async def reconstruct_object(frame_rgb: np.ndarray, mask_bin: np.ndarray,
                             seed: int = 42) -> bytes:
    """One masked object -> textured GLB bytes from SAM 3D."""
    import httpx

    ok, img_buf = cv2.imencode(".jpg", cv2.cvtColor(frame_rgb, cv2.COLOR_RGB2BGR))
    ok2, mask_buf = cv2.imencode(".png", (mask_bin.astype(np.uint8) * 255))
    if not ok or not ok2:
        raise RuntimeError("could not encode frame/mask for SAM 3D")
    body = {"input": {
        "image_b64": base64.b64encode(img_buf.tobytes()).decode(),
        "mask_b64": base64.b64encode(mask_buf.tobytes()).decode(),
        "seed": seed,
    }}
    async with httpx.AsyncClient(timeout=60.0) as client:
        run = await _post(client, "/run", body)
        job_id = run.get("id")
        if not job_id:
            raise RuntimeError(f"SAM 3D submit returned no job id: {run}")
        terminal = False
        try:
            waited = 0
            while waited < _TIMEOUT_SECONDS:
                await asyncio.sleep(_POLL_SECONDS)
                waited += _POLL_SECONDS
                status = await _get(client, f"/status/{job_id}")
                state = status.get("status")
                if state == "COMPLETED":
                    terminal = True
                    output = status.get("output") or {}
                    if output.get("error"):
                        raise RuntimeError(f"SAM 3D job failed: {output['error']}")
                    glb_b64 = output.get("glb_b64")
                    if not glb_b64:
                        raise RuntimeError("SAM 3D job completed without glb_b64")
                    data = base64.b64decode(glb_b64)
                    if data[:4] != b"glTF":
                        raise RuntimeError("SAM 3D returned a non-GLB payload")
                    log.info("sam3d object reconstructed (%d bytes)", len(data))
                    return data
                if state in ("FAILED", "CANCELLED", "TIMED_OUT"):
                    terminal = True
                    raise RuntimeError(f"SAM 3D job {state}: {status.get('error')}")
            raise RuntimeError("SAM 3D job timed out")
        finally:
            # A local timeout/cancellation must not leave billable orphan work.
            if not terminal:
                try:
                    await _post(client, f"/cancel/{job_id}")
                    log.info("Cancelled abandoned SAM job %s", job_id)
                except Exception:
                    log.warning("Could not confirm SAM job cancellation: %s", job_id)
