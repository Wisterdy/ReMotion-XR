"""RunPod Serverless worker for ReMotion XR (GPU pool, not a single pod).

Worker input contract (schema_version 1):
  {"schema_version": 1, "session_id": "<server-issued id>",
   "video_key": "sessions/<id>/source.mp4",
   "timeline_key": "sessions/<id>/timeline.json",
   "input_manifest_key": "sessions/<id>/input.json"}

Artifact keys are validated against the session ID before any
filesystem access; traversal and unknown actions are rejected. A
legacy inline {"session_id", "timeline"} job is still accepted for
local development. The handler is a compute/artifact producer only:
the FastAPI gateway remains the durable status/download authority, so
a finished handler never reports a queued platform job as its result,
and it checks the deletion tombstone before committing output.

RunPod lifecycle states (IN_QUEUE etc.) belong to the platform API
(POST /run, GET /status with the server-side RUNPOD_API_KEY); they are
never synthesized here. If no GPU is visible the job fails retryably
instead of pretending to requeue itself.
"""

import asyncio
import json
import logging
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from app.services import job_queue, storage  # noqa: E402

logging.basicConfig(level=logging.INFO)
log = logging.getLogger("remotion.runpod")

_DELETE_ACTIONS = {"delete_original", "delete_virtual", "delete_all"}


def gpu_available() -> bool:
    """True when a CUDA GPU is visible to this worker."""
    if os.environ.get("REMOTION_GPU_AVAILABLE") is not None:
        return os.environ["REMOTION_GPU_AVAILABLE"] == "1"
    try:
        import torch

        return bool(torch.cuda.is_available())
    except Exception:
        return False


def _validate_keys(job_input: dict, session_id: str) -> str | None:
    """Return an error string, or None when the artifact keys are exact."""
    expected = {
        "video_key": storage.artifact_key(session_id, "video"),
        "timeline_key": storage.artifact_key(session_id, "timeline"),
        "input_manifest_key": storage.artifact_key(session_id, "manifest"),
    }
    for field, want in expected.items():
        got = job_input.get(field)
        if got is not None and got != want:
            return f"{field} does not match the session artifact key"
    return None


async def handle_async(event: dict) -> dict:
    job_input = (event or {}).get("input") or {}
    session_id = job_input.get("session_id")
    if not session_id:
        return {"state": "FAILED", "error": "input.session_id is required"}
    try:
        storage.validate_session_id(session_id)
    except ValueError as exc:
        return {"state": "FAILED", "error": str(exc)}

    action = job_input.get("action", "process")
    if action in _DELETE_ACTIONS:
        getattr(storage, action)(session_id)
        return {"session_id": session_id, "state": "READY",
                "deleted": action.removeprefix("delete_")}
    if action != "process":
        return {"state": "FAILED", "error": f"unrecognized action: {action!r}"}

    if job_input.get("schema_version") not in (None, 1):
        return {"state": "FAILED",
                "error": f"unsupported schema_version: {job_input.get('schema_version')!r}"}
    key_error = _validate_keys(job_input, session_id)
    if key_error:
        return {"state": "FAILED", "error": key_error}
    if storage.is_tombstoned(session_id):
        return {"session_id": session_id, "state": "CANCELLED",
                "error": "session was deleted"}

    if not gpu_available():
        log.info("no GPU available; failing retryably session=%s", session_id)
        return {"session_id": session_id, "state": "FAILED",
                "retryable": True, "error": "no GPU available on this worker"}

    if job_input.get("video_transfer"):
        from app.services.artifact_transfer import decode
        data = decode(job_input["video_transfer"])
        dest = storage.original_path(session_id)
        dest.parent.mkdir(parents=True, exist_ok=True)
        tmp = dest.with_suffix(".transfer.tmp")
        tmp.write_bytes(data)
        tmp.replace(dest)
    if not storage.original_path(session_id).is_file():
        return {"session_id": session_id, "state": "FAILED",
                "error": "Uploaded video missing on shared network volume"}

    timeline = job_input.get("timeline") or storage.load_timeline(session_id)
    # A scoped recovery may supply hardware calibration read from this
    # capture's own headset. It is never an activity/object-name override.
    calibration_override = os.environ.get("REMOTION_CAPTURE_CALIBRATION")
    if calibration_override:
        correction = json.loads(calibration_override)
        if correction.get("session_id") == session_id:
            timeline = {**timeline, "camera_calibration": correction["calibration"]}
    status = await job_queue.run_pipeline(session_id, timeline)
    if storage.is_tombstoned(session_id):
        return {"session_id": session_id, "state": "CANCELLED",
                "error": "session was deleted during processing"}
    base = os.environ.get("REMOTION_PACKAGE_BASE_URL", "").rstrip("/")
    transferred = {}
    if job_input.get("video_transfer") and status.state.value == "READY":
        from app.services.artifact_transfer import encode
        transferred["package_transfer"] = encode(storage.virtual_path(session_id).read_bytes())
    return {
        **transferred,
        "session_id": session_id,
        "state": status.state.value,
        "progress": status.progress,
        "message": status.message,
        "error": status.error,
        "package_key": storage.artifact_key(session_id, "package"),
        "package_hash": status.package_hash,
        # Only a real gateway/CDN URL is downloadable by the client;
        # without one the client fails closed instead of marking a
        # worker filesystem path as a delivered package.
        "download_url": f"{base}/{session_id}.remotion" if base else None,
    }


def handle(event: dict) -> dict:
    """Synchronous local/test entry point; RunPod uses handle_async."""
    return asyncio.run(handle_async(event))


if __name__ == "__main__":
    import runpod  # only required inside the Serverless worker image

    runpod.serverless.start({"handler": handle_async})
