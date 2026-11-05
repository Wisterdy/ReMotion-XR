"""Pydantic models for the ReMotion XR API (gateway /v1 + legacy)."""

from enum import Enum

from pydantic import BaseModel, Field


class SessionState(str, Enum):
    LOCAL = "LOCAL"
    UPLOADING = "UPLOADING"
    QUEUED = "QUEUED"
    PROCESSING = "PROCESSING"
    DETECTING = "DETECTING"
    CUTTING_OUT = "CUTTING_OUT"
    RECONSTRUCTING_3D = "RECONSTRUCTING_3D"
    EXTRACTING_KEY_POSES = "EXTRACTING_KEY_POSES"
    BUILDING_SCENE = "BUILDING_SCENE"
    READY = "READY"
    FAILED = "FAILED"
    CANCELLED = "CANCELLED"
    DELETING = "DELETING"
    DELETED = "DELETED"


# Internal pipeline states double as the exposed processing stage.
PIPELINE_STAGES = {
    SessionState.DETECTING, SessionState.CUTTING_OUT,
    SessionState.RECONSTRUCTING_3D, SessionState.EXTRACTING_KEY_POSES,
    SessionState.BUILDING_SCENE,
}


def public_state(state: SessionState) -> SessionState:
    """Collapse pipeline stages to PROCESSING for the /v1 contract."""
    return SessionState.PROCESSING if state in PIPELINE_STAGES else state


class SessionStatus(BaseModel):
    session_id: str
    state: SessionState
    progress: int  # 0-100
    message: str = ""
    error: str | None = None
    stage: str | None = None
    retryable: bool = False
    job_id: str | None = None
    attempt: int = 1
    package_revision: int = 0
    package_hash: str | None = None
    capabilities: dict = Field(default_factory=dict)
    display_name: str = ""


class SessionSummary(BaseModel):
    session_id: str
    state: SessionState
    progress: int
    action_sequence: str = "unknown"
    duration_seconds: float = 0.0


class UploadResponse(BaseModel):
    session_id: str
    state: SessionState
    job_id: str | None = None
    capabilities: dict = Field(default_factory=dict)
