"""Pipeline runner + job store facade (SQLite-backed via storage.py).

Used identically by FastAPI BackgroundTasks and the RunPod Serverless
handler, so job state survives worker restarts.
"""

import asyncio
import logging
from pathlib import Path

from app.models import SessionState, SessionStatus
from app.services import storage

log = logging.getLogger("remotion.jobs")


def create_job(session_id: str) -> SessionStatus:
    return storage.save_job(SessionStatus(
        session_id=session_id, state=SessionState.UPLOADING,
        progress=5, message="Upload received"))


def get_status(session_id: str) -> SessionStatus | None:
    return storage.load_job(session_id)


def list_sessions() -> list[SessionStatus]:
    return storage.list_jobs()


def get_timeline(session_id: str) -> dict:
    return storage.load_timeline(session_id)


def package_path(session_id: str) -> Path:
    return storage.virtual_path(session_id)


def _set(session_id: str, state: SessionState, progress: int, message: str,
         timeline: dict | None = None) -> None:
    s = storage.load_job(session_id) or SessionStatus(session_id=session_id, state=state, progress=progress)
    s.state, s.progress, s.message = state, progress, message
    storage.save_job(s, timeline)
    log.info("session=%s state=%s progress=%s", session_id, state.value, progress)


async def run_pipeline(session_id: str, timeline: dict) -> SessionStatus:
    """Detecting -> Cutting out -> Reconstructing -> Key poses -> Building -> Ready."""
    _set(session_id, SessionState.UPLOADING, 10, "Job accepted", timeline)
    try:
        # The CPU gateway imports the job store, but only workers load the
        # video/model stack. Keep these imports inside the guarded pipeline.
        from app.services import detection, keypose, packaging, reconstruction, segmentation

        _set(session_id, SessionState.DETECTING, 20, "Detecting people and objects")
        tracks = await detection.detect(
            timeline, video_path=storage.original_path(session_id))

        _set(session_id, SessionState.CUTTING_OUT, 40, "Identifying + cutting out objects")
        cutouts = await segmentation.cut_out(tracks, timeline, session_id=session_id)

        _set(session_id, SessionState.RECONSTRUCTING_3D, 60, "SAM 3D / trajectory reconstruction")
        assets = await reconstruction.reconstruct(
            cutouts, video_path=storage.original_path(session_id))

        _set(session_id, SessionState.EXTRACTING_KEY_POSES, 80, "Extracting key poses")
        poses = await keypose.extract_key_poses(timeline)
        from app.services.object_motion import attach_motion
        attach_motion(assets, timeline)
        observed_times = sorted({p["timestamp"] for a in assets for p in a.get("object_poses", [])})
        if observed_times:
            if len(observed_times) > 20:
                observed_times = [observed_times[round(i*(len(observed_times)-1)/19)] for i in range(20)]
            poses["key_poses"] = [{"id": i+1, "timestamp": t, "label": f"Tool motion {i+1}",
                                   "segment": "observed_tool_motion", "phase": "contact",
                                   "is_best_pose": False} for i,t in enumerate(observed_times)]

        _set(session_id, SessionState.BUILDING_SCENE, 90, "Building .remotion scene")
        packaging.build_package(session_id, assets, poses, timeline, package_path(session_id))

        # A delete that raced the pipeline wins: never resurrect it.
        if storage.is_tombstoned(session_id):
            storage.delete_virtual(session_id)
            _set(session_id, SessionState.DELETED, 0, "Deleted during processing")
            return storage.load_job(session_id)
        pkg = package_path(session_id)
        if pkg.exists():
            storage.record_package(session_id, pkg.read_bytes())
        ready_msg = ("Scene ready in Library" if assets else
                     "Original video ready; no observed subjects detected")
        _set(session_id, SessionState.READY, 100, ready_msg)
    except Exception as exc:  # keep the job inspectable instead of crashing the worker
        log.exception("pipeline failed session=%s", session_id)
        s = storage.load_job(session_id) or SessionStatus(session_id=session_id, state=SessionState.FAILED, progress=0)
        s.state, s.message, s.error = SessionState.FAILED, "Pipeline failed", str(exc)
        storage.save_job(s)
    return storage.load_job(session_id)
