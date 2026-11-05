"""Gateway submits compute to RunPod; only the gateway owns provider credentials.

Gateway and GPU worker mount the same existing network volume at /runpod-volume.
Persist the input timeline before submitting, and the provider id before polling.
Restart recovery resumes polling existing jobs instead of running GPU work on CPU.
"""
import asyncio
import json
import os
from pathlib import Path
import httpx
from app.config import DATA_DIR
from app.models import SessionState
from app.services import job_queue, storage

_tasks = {}

def endpoint():
    return os.environ.get("REMOTION_WORKER_ENDPOINT_URL", "").rstrip("/")

def job_file(sid):
    return DATA_DIR / "dispatch" / (storage.validate_session_id(sid) + ".json")

def save_dispatch(sid, data):
    path = job_file(sid)
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(data))
    tmp.replace(path)

def failed(sid, message):
    status = storage.load_job(sid)
    if status is not None and not storage.is_tombstoned(sid):
        status.state = SessionState.FAILED
        status.message = "RunPod processing failed"
        status.error = message
        storage.save_job(status)

async def run(sid, timeline):
    if not endpoint():
        if os.environ.get("REMOTION_REQUIRE_REMOTE") == "1":
            failed(sid, "RunPod worker endpoint is not configured")
            return
        await job_queue.run_pipeline(sid, timeline)
        return
    key = os.environ.get("RUNPOD_API_KEY")
    if not key:
        failed(sid, "Gateway RunPod credential is missing")
        return
    status = storage.load_job(sid)
    if status is None or storage.is_tombstoned(sid): return
    storage.save_job(status, timeline)
    existing = json.loads(job_file(sid).read_text()) if job_file(sid).exists() else {}
    operation = "input preparation"
    try:
        async with httpx.AsyncClient(timeout=60, headers={"Authorization": "Bearer " + key}) as client:
            job_id = existing.get("job_id")
            if not job_id:
                status.state = SessionState.QUEUED
                status.message = "Waiting for RunPod GPU"
                storage.save_job(status, timeline)
                transfer = {}
                if os.environ.get("REMOTION_INLINE_TRANSFER") == "1":
                    from app.services.artifact_transfer import encode
                    transfer = {"video_transfer": encode(storage.original_path(sid).read_bytes()), "timeline": timeline}
                operation = "scene submission"
                response = await client.post(endpoint() + "/run", json={"input": {
                    **transfer,
                    "schema_version": 1, "session_id": sid,
                    "video_key": storage.artifact_key(sid, "video"),
                    "timeline_key": storage.artifact_key(sid, "timeline"),
                    "input_manifest_key": storage.artifact_key(sid, "manifest")},
                    "policy": {"executionTimeout": 1800000, "ttl": 3600000}})
                response.raise_for_status()
                job_id = response.json()["id"]
                save_dispatch(sid, {"job_id": job_id})
            for _ in range(720):
                if storage.is_tombstoned(sid):
                    operation = "job cancellation"
                    await client.post(endpoint() + "/cancel/" + job_id)
                    return
                operation = "job status check"
                response = await client.get(endpoint() + "/status/" + job_id)
                response.raise_for_status()
                result = response.json()
                state = result.get("status")
                if state == "COMPLETED":
                    output = result.get("output") or {}
                    if not isinstance(output, dict) or output.get("state") != "READY":
                        failed(sid, (output.get("error") if isinstance(output, dict) else None)
                               or "GPU worker did not produce a scene")
                    elif output.get("package_transfer"):
                        from app.services.artifact_transfer import decode, validate_package
                        data = decode(output["package_transfer"])
                        validate_package(data)
                        if storage.is_tombstoned(sid):
                            return
                        dest = storage.virtual_path(sid)
                        dest.parent.mkdir(parents=True, exist_ok=True)
                        tmp = dest.with_suffix(".transfer.tmp")
                        tmp.write_bytes(data)
                        tmp.replace(dest)
                        storage.record_package(sid, data)
                        status = storage.load_job(sid)
                        status.state = SessionState.READY
                        status.progress = 100
                        status.message = "Scene package downloaded from GPU worker"
                        status.error = None
                        storage.save_job(status, timeline)
                    elif not storage.virtual_path(sid).is_file():
                        failed(sid, "Worker completed but package is missing from the shared volume")
                    return
                if state in ("FAILED", "CANCELLED", "TIMED_OUT"):
                    failed(sid, "RunPod job " + state + ": " + str(result.get("error", ""))[:500])
                    return
                await asyncio.sleep(5)
            failed(sid, "RunPod job exceeded one hour; retry from Library")
    except httpx.HTTPStatusError as exc:
        # Keep the actionable status and operation, never response bodies,
        # request URLs, credentials, or uploaded capture data. A 409 alone
        # does not establish whether the endpoint is paused or why it rejected.
        failed(sid, f"RunPod rejected {operation} (HTTP {exc.response.status_code}). "
               "The recording is saved; check the server before retrying.")
    except (httpx.HTTPError, ValueError, KeyError) as exc:
        # No request headers, tokens, or provider response body in errors.
        failed(sid, "Gateway could not complete RunPod request (" + type(exc).__name__ + ")")

async def start(sid, timeline):
    if sid in _tasks and not _tasks[sid].done(): return
    task = asyncio.create_task(run(sid, timeline))
    _tasks[sid] = task
    task.add_done_callback(lambda done: _tasks.pop(sid, None))

async def recover():
    if not endpoint(): return
    for status in storage.list_jobs():
        if status.state not in (SessionState.READY, SessionState.FAILED, SessionState.DELETED):
            await start(status.session_id, storage.load_timeline(status.session_id))

async def stop():
    tasks = list(_tasks.values())
    for task in tasks: task.cancel()
    await asyncio.gather(*tasks, return_exceptions=True)
