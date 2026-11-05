"""Session endpoints.

Gateway contract (/v1, the Quest production path):
  POST   /v1/sessions                       multipart video+timeline+metadata,
                                            Idempotency-Key header supported
  GET    /v1/sessions/{id}                  durable state/stage/capabilities
  GET    /v1/sessions/{id}/video|package    authorized streaming downloads
  POST   /v1/sessions/{id}/rebuild          idempotent new attempt
  PATCH  /v1/sessions/{id}                  rename (actually persists)
  DELETE /v1/sessions/{id}?scope=...        original|virtual|all

Legacy /api/sessions/* routes remain for local development and share
the same store. Every protected route requires X-API-Key when
REMOTION_API_KEY is configured on the gateway; the key is never a
RunPod credential and is never forwarded to artifact URLs.
Uploads stream to a temp file, validate, then commit atomically â€”
no placeholder video is ever substituted.
"""

import json
import re
import uuid
from pathlib import Path

from fastapi import (APIRouter, BackgroundTasks, Depends, Header,
                     HTTPException, Request)
from fastapi.responses import FileResponse
from starlette.datastructures import UploadFile as StarletteUploadFile
from starlette.formparsers import MultiPartParser

from app.config import gateway_api_key, max_timeline_bytes, max_upload_bytes
from app.models import (SessionState, SessionStatus, SessionSummary,
                        UploadResponse, public_state)
from app.services import dispatch, job_queue, storage

router = APIRouter(tags=["sessions"])

# Timelines carry full joint positions + rotations (~2MB for a short
# capture) and videos are larger still; the framework default of 1MB
# per multipart part rejected real headset uploads with HTTP 400.
MAX_PART_BYTES = 64 * 1024 * 1024


async def require_api_key(x_api_key: str | None = Header(default=None)):
    expected = gateway_api_key()
    if expected and x_api_key != expected:
        raise HTTPException(status_code=401, detail="invalid or missing X-API-Key")


def _check_id(session_id: str) -> str:
    try:
        return storage.validate_session_id(session_id)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc


def _status_view(status: SessionStatus) -> SessionStatus:
    view = status.model_copy()
    view.state = public_state(status.state)
    view.capabilities = storage.capabilities(status.session_id)
    view.retryable = status.state == SessionState.FAILED
    return view


async def _parse_upload(request: Request):
    parser = MultiPartParser(request.headers, request.stream(),
                             max_part_size=MAX_PART_BYTES)
    form = await parser.parse()
    video = form.get("video")
    timeline = form.get("timeline", "{}")
    if not isinstance(video, StarletteUploadFile):
        raise HTTPException(status_code=422, detail="video file part is required")
    if not isinstance(timeline, str):
        timeline = (await timeline.read()).decode("utf-8", "replace")
    return video, timeline


def _validate_timeline(timeline: str) -> dict:
    if len(timeline.encode()) > max_timeline_bytes():
        raise HTTPException(status_code=413, detail="timeline too large")
    try:
        data = json.loads(timeline) if timeline else {}
    except json.JSONDecodeError as exc:
        raise HTTPException(status_code=422, detail=f"Invalid timeline JSON: {exc}") from exc
    if not isinstance(data, dict):
        raise HTTPException(status_code=422, detail="timeline must be a JSON object")
    return data


async def _commit_upload(session_id: str, video, timeline_data: dict) -> None:
    """Write video to a temp file, then atomically commit it."""
    dest = storage.original_path(session_id)
    dest.parent.mkdir(parents=True, exist_ok=True)
    tmp = dest.with_suffix(".uploading")
    size = 0
    with open(tmp, "wb") as fh:
        while True:
            chunk = await video.read(1024 * 1024)
            if not chunk:
                break
            size += len(chunk)
            if size > max_upload_bytes():
                tmp.unlink(missing_ok=True)
                raise HTTPException(status_code=413, detail="video too large")
            fh.write(chunk)
    if size == 0:
        tmp.unlink(missing_ok=True)
        raise HTTPException(status_code=422, detail="video file is empty")
    tmp.replace(dest)


# ---------------- /v1 gateway contract ----------------

@router.post("/v1/sessions", response_model=UploadResponse,
             dependencies=[Depends(require_api_key)])
async def create_session_v1(request: Request, background_tasks: BackgroundTasks,
                            idempotency_key: str | None = Header(default=None)):
    if idempotency_key:
        existing = storage.find_by_idempotency_key(idempotency_key)
        if existing is not None:
            return UploadResponse(session_id=existing.session_id,
                                  state=public_state(existing.state),
                                  job_id=existing.session_id,
                                  capabilities=storage.capabilities(existing.session_id))
    video, timeline = await _parse_upload(request)
    timeline_data = _validate_timeline(timeline)
    session_id = uuid.uuid4().hex
    await _commit_upload(session_id, video, timeline_data)
    job_queue.create_job(session_id)
    if idempotency_key:
        storage.bind_idempotency_key(session_id, idempotency_key)
    storage.save_job(job_queue.get_status(session_id), timeline_data)
    background_tasks.add_task(dispatch.start if dispatch.endpoint() else dispatch.run, session_id, timeline_data)
    return UploadResponse(session_id=session_id, state=SessionState.QUEUED,
                          job_id=session_id,
                          capabilities=storage.capabilities(session_id))


@router.get("/v1/sessions/{session_id}", dependencies=[Depends(require_api_key)])
async def get_session_v1(session_id: str):
    _check_id(session_id)
    status = job_queue.get_status(session_id)
    if status is None or status.state == SessionState.DELETED:
        raise HTTPException(status_code=404, detail="Session not found")
    return _status_view(status)


@router.get("/v1/sessions/{session_id}/video", dependencies=[Depends(require_api_key)])
async def get_video_v1(session_id: str):
    _check_id(session_id)
    if job_queue.get_status(session_id) is None:
        raise HTTPException(status_code=404, detail="Session not found")
    path = storage.original_path(session_id)
    if not path.exists():
        raise HTTPException(status_code=404, detail="Original video not available")
    return FileResponse(path, media_type="video/mp4",
                        filename=f"{session_id}.mp4")


@router.get("/v1/sessions/{session_id}/package", dependencies=[Depends(require_api_key)])
async def get_package_v1(session_id: str):
    _check_id(session_id)
    status = job_queue.get_status(session_id)
    if status is None:
        raise HTTPException(status_code=404, detail="Session not found")
    if status.state != SessionState.READY:
        raise HTTPException(status_code=409, detail=f"Scene not ready (state={status.state.value})")
    path = job_queue.package_path(session_id)
    if not path.exists():
        raise HTTPException(status_code=409, detail="Package not built yet")
    return FileResponse(path, media_type="application/zip",
                        filename=f"{session_id}.remotion")


@router.post("/v1/sessions/{session_id}/rebuild", dependencies=[Depends(require_api_key)])
async def rebuild_v1(session_id: str, background_tasks: BackgroundTasks,
                     idempotency_key: str | None = Header(default=None)):
    _check_id(session_id)
    status = job_queue.get_status(session_id)
    if status is None or status.state == SessionState.DELETED:
        raise HTTPException(status_code=404, detail="Session not found")
    if not storage.original_path(session_id).exists():
        raise HTTPException(status_code=409,
                            detail="Original input no longer exists; rebuild unavailable")
    if status.state in (SessionState.PROCESSING, *[
            SessionState.DETECTING, SessionState.CUTTING_OUT,
            SessionState.RECONSTRUCTING_3D, SessionState.EXTRACTING_KEY_POSES,
            SessionState.BUILDING_SCENE]):
        return _status_view(status)  # already in flight: idempotent no-op
    status.attempt = (status.attempt or 1) + 1
    status.state, status.progress = SessionState.QUEUED, 5
    status.message, status.error = "Rebuild queued", None
    storage.save_job(status)
    timeline = storage.load_timeline(session_id)
    dispatch.job_file(session_id).unlink(missing_ok=True)
    background_tasks.add_task(dispatch.start if dispatch.endpoint() else dispatch.run, session_id, timeline)
    return _status_view(job_queue.get_status(session_id))


@router.patch("/v1/sessions/{session_id}", dependencies=[Depends(require_api_key)])
async def rename_v1(session_id: str, request: Request):
    _check_id(session_id)
    body = await request.json()
    name = str(body.get("display_name") or "").strip()
    if not name or len(name) > 120:
        raise HTTPException(status_code=422, detail="display_name is required (<=120 chars)")
    status = storage.rename(session_id, name)
    if status is None:
        raise HTTPException(status_code=404, detail="Session not found")
    return _status_view(status)


@router.delete("/v1/sessions/{session_id}", dependencies=[Depends(require_api_key)])
async def delete_v1(session_id: str, scope: str = "all"):
    _check_id(session_id)
    if scope not in ("original", "virtual", "all"):
        raise HTTPException(status_code=422, detail="scope must be original|virtual|all")
    if job_queue.get_status(session_id) is None:
        raise HTTPException(status_code=404, detail="Session not found")
    if scope == "original":
        storage.delete_original(session_id)
    elif scope == "virtual":
        storage.delete_virtual(session_id)
        status = job_queue.get_status(session_id)
        if status is not None and status.state == SessionState.READY:
            status.state, status.message = (
                SessionState.QUEUED, "Virtual scene deleted; rebuild available")
            storage.save_job(status)
    else:
        storage.delete_all(session_id)
    return {"session_id": session_id, "deleted": scope,
            "state": "DELETED" if scope == "all" else "READY"}


# ---------------- legacy local-dev routes (same store) ----------------

@router.post("/api/sessions/upload", response_model=UploadResponse,
             dependencies=[Depends(require_api_key)])
async def upload_session(request: Request, background_tasks: BackgroundTasks):
    video, timeline = await _parse_upload(request)
    timeline_data = _validate_timeline(timeline)
    session_id = uuid.uuid4().hex[:12]
    job_queue.create_job(session_id)
    await _commit_upload(session_id, video, timeline_data)
    storage.save_job(job_queue.get_status(session_id), timeline_data)
    background_tasks.add_task(dispatch.start if dispatch.endpoint() else dispatch.run, session_id, timeline_data)
    return UploadResponse(session_id=session_id, state=SessionState.UPLOADING,
                          job_id=session_id)


@router.get("/api/sessions", response_model=list[SessionSummary],
            dependencies=[Depends(require_api_key)])
async def list_sessions():
    out = []
    for s in job_queue.list_sessions():
        tl = job_queue.get_timeline(s.session_id)
        out.append(SessionSummary(
            session_id=s.session_id, state=s.state, progress=s.progress,
            action_sequence=str(tl.get("action_sequence", "unknown")),
            duration_seconds=float(tl.get("duration_seconds", 0.0)),
        ))
    return out


@router.get("/api/sessions/{session_id}/status", response_model=SessionStatus,
            dependencies=[Depends(require_api_key)])
async def session_status(session_id: str):
    _check_id(session_id)
    status = job_queue.get_status(session_id)
    if status is None or status.state == SessionState.DELETED:
        raise HTTPException(status_code=404, detail="Session not found")
    return status


@router.delete("/api/sessions/{session_id}", dependencies=[Depends(require_api_key)])
async def delete_session(session_id: str):
    _check_id(session_id)
    if not storage.delete_all(session_id):
        raise HTTPException(status_code=404, detail="Session not found")
    return {"session_id": session_id, "deleted": "all"}


@router.delete("/api/sessions/{session_id}/original", dependencies=[Depends(require_api_key)])
async def delete_original(session_id: str):
    _check_id(session_id)
    if job_queue.get_status(session_id) is None:
        raise HTTPException(status_code=404, detail="Session not found")
    storage.delete_original(session_id)
    return {"session_id": session_id, "deleted": "original"}


@router.delete("/api/sessions/{session_id}/virtual", dependencies=[Depends(require_api_key)])
async def delete_virtual(session_id: str):
    _check_id(session_id)
    if job_queue.get_status(session_id) is None:
        raise HTTPException(status_code=404, detail="Session not found")
    storage.delete_virtual(session_id)
    s = job_queue.get_status(session_id)
    if s is not None and s.state == SessionState.READY:
        s.state, s.message = SessionState.UPLOADING, "Virtual scene deleted; rebuild available"
        storage.save_job(s)
    return {"session_id": session_id, "deleted": "virtual"}


@router.get("/api/sessions/{session_id}/download", dependencies=[Depends(require_api_key)])
async def download_scene(session_id: str):
    _check_id(session_id)
    status = job_queue.get_status(session_id)
    if status is None:
        raise HTTPException(status_code=404, detail="Session not found")
    if status.state != SessionState.READY:
        raise HTTPException(status_code=409, detail=f"Scene not ready (state={status.state.value})")
    path = job_queue.package_path(session_id)
    if not path.exists():
        raise HTTPException(status_code=409, detail="Package not built yet")
    return FileResponse(path, media_type="application/zip",
                        filename=f"{session_id}.remotion")
