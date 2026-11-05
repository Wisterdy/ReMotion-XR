"""Storage layer shared by the FastAPI gateway and the RunPod handler.

Layout under DATA_DIR:
  uploads/{session_id}_original_video.mp4   (original recording)
  packages/{session_id}.remotion            (virtual scene package)
  jobs.db                                   (SQLite job store)

Delete semantics (same logic everywhere):
  delete_original -> only the original video
  delete_virtual  -> only the scene package / virtual assets
  delete_all      -> both, plus a durable tombstone so an in-flight
                     worker cannot resurrect the session afterwards

Session IDs are validated before any filesystem access: untrusted IDs
must never become paths (traversal) or glob patterns.
"""

import hashlib
import json
import re
import sqlite3
import threading
from pathlib import Path

from app.config import DATA_DIR, JOBS_DB, PACKAGE_DIR, UPLOAD_DIR, ensure_dirs
from app.models import SessionState, SessionStatus

_lock = threading.Lock()
_ID_RE = re.compile(r"^[A-Za-z0-9_-]{1,64}$")


def validate_session_id(session_id: str) -> str:
    if not isinstance(session_id, str) or not _ID_RE.match(session_id):
        raise ValueError(f"invalid session_id: {session_id!r}")
    return session_id


def original_path(session_id: str) -> Path:
    return UPLOAD_DIR / f"{validate_session_id(session_id)}_original_video.mp4"


def virtual_path(session_id: str) -> Path:
    return PACKAGE_DIR / f"{validate_session_id(session_id)}.remotion"


def artifact_key(session_id: str, kind: str) -> str:
    """Canonical volume key for the worker contract (sessions/<id>/...)."""
    validate_session_id(session_id)
    names = {"video": "source.mp4", "timeline": "timeline.json",
             "manifest": "input.json", "package": "package.remotion"}
    if kind not in names:
        raise ValueError(f"unknown artifact kind: {kind!r}")
    return f"sessions/{session_id}/{names[kind]}"


def _db() -> sqlite3.Connection:
    ensure_dirs()
    conn = sqlite3.connect(JOBS_DB)
    conn.execute(
        "CREATE TABLE IF NOT EXISTS jobs ("
        " session_id TEXT PRIMARY KEY, state TEXT, progress INTEGER,"
        " message TEXT, error TEXT, timeline TEXT)")
    cols = {r[1] for r in conn.execute("PRAGMA table_info(jobs)").fetchall()}
    for name, decl in [
            ("attempt", "INTEGER DEFAULT 1"),
            ("idempotency_key", "TEXT"),
            ("tombstone", "INTEGER DEFAULT 0"),
            ("display_name", "TEXT DEFAULT ''"),
            ("package_hash", "TEXT"),
            ("package_revision", "INTEGER DEFAULT 0")]:
        if name not in cols:
            conn.execute(f"ALTER TABLE jobs ADD COLUMN {name} {decl}")
    conn.execute(
        "CREATE UNIQUE INDEX IF NOT EXISTS jobs_idempotency "
        "ON jobs(idempotency_key) WHERE idempotency_key IS NOT NULL")
    # Tombstones outlive the job row: delete_all removes the job (the
    # historical load_job contract) while workers still refuse to
    # resurrect the session.
    conn.execute(
        "CREATE TABLE IF NOT EXISTS tombstones (session_id TEXT PRIMARY KEY)")
    return conn


# --- job store (SQLite so jobs survive worker restarts) ---

def save_job(status: SessionStatus, timeline: dict | None = None) -> SessionStatus:
    validate_session_id(status.session_id)
    with _lock, _db() as conn:
        row = conn.execute(
            "SELECT timeline, attempt, idempotency_key, tombstone,"
            " display_name, package_hash, package_revision"
            " FROM jobs WHERE session_id=?", (status.session_id,)).fetchone()
        tl = json.dumps(timeline) if timeline is not None else (row[0] if row else "{}")
        conn.execute(
            "INSERT INTO jobs VALUES (?,?,?,?,?,?,?,?,?,?,?,?)"
            " ON CONFLICT(session_id) DO UPDATE SET state=excluded.state,"
            " progress=excluded.progress, message=excluded.message,"
            " error=excluded.error, timeline=excluded.timeline,"
            " attempt=excluded.attempt, display_name=excluded.display_name,"
            " package_hash=excluded.package_hash,"
            " package_revision=excluded.package_revision",
            (status.session_id, status.state.value, status.progress,
             status.message, status.error, tl,
             max(1, status.attempt or 1),
             getattr(status, "_idempotency_key", None) or (row[2] if row else None),
             1 if status.state == SessionState.DELETED else (row[3] if row else 0),
             status.display_name or (row[4] if row else ""),
             status.package_hash or (row[5] if row else None),
             status.package_revision or (row[6] if row else 0)))
    return status


def _row_to_status(session_id: str, row) -> SessionStatus:
    state = SessionState(row[0])
    return SessionStatus(
        session_id=session_id, state=state, progress=row[1],
        message=row[2] or "", error=row[3],
        stage=state.value if state.value in {
            "DETECTING", "CUTTING_OUT", "RECONSTRUCTING_3D",
            "EXTRACTING_KEY_POSES", "BUILDING_SCENE"} else None,
        job_id=session_id, attempt=row[4] or 1,
        display_name=row[6] or "", package_hash=row[7],
        package_revision=row[8] or 0)


def load_job(session_id: str) -> SessionStatus | None:
    validate_session_id(session_id)
    with _lock, _db() as conn:
        row = conn.execute(
            "SELECT state, progress, message, error, attempt,"
            " idempotency_key, display_name, package_hash, package_revision"
            " FROM jobs WHERE session_id=?", (session_id,)).fetchone()
    if row is None:
        return None
    return _row_to_status(session_id, row)


def find_by_idempotency_key(key: str) -> SessionStatus | None:
    if not key:
        return None
    with _lock, _db() as conn:
        row = conn.execute(
            "SELECT session_id FROM jobs WHERE idempotency_key=?",
            (key,)).fetchone()
    return load_job(row[0]) if row else None


def bind_idempotency_key(session_id: str, key: str) -> None:
    if not key:
        return
    validate_session_id(session_id)
    with _lock, _db() as conn:
        conn.execute("UPDATE jobs SET idempotency_key=? WHERE session_id=?",
                     (key, session_id))


def rename(session_id: str, display_name: str) -> SessionStatus | None:
    status = load_job(session_id)
    if status is None:
        return None
    status.display_name = display_name
    return save_job(status)


def is_tombstoned(session_id: str) -> bool:
    validate_session_id(session_id)
    with _lock, _db() as conn:
        row = conn.execute("SELECT 1 FROM tombstones WHERE session_id=?",
                           (session_id,)).fetchone()
        job = conn.execute("SELECT tombstone, state FROM jobs WHERE session_id=?",
                           (session_id,)).fetchone()
    return bool(row) or bool(job and (job[0] or job[1] == SessionState.DELETED.value))


def mark_tombstone(session_id: str) -> None:
    validate_session_id(session_id)
    with _lock, _db() as conn:
        conn.execute(
            "INSERT OR IGNORE INTO tombstones (session_id) VALUES (?)",
            (session_id,))
        conn.execute(
            "INSERT INTO jobs (session_id, state, progress, tombstone)"
            " VALUES (?, 'DELETING', 0, 1)"
            " ON CONFLICT(session_id) DO UPDATE SET tombstone=1, state='DELETING'",
            (session_id,))


def record_package(session_id: str, package_bytes: bytes) -> str:
    """Record the committed package hash/revision; returns the sha256."""
    digest = hashlib.sha256(package_bytes).hexdigest()
    status = load_job(session_id)
    if status is not None:
        status.package_hash = digest
        status.package_revision = (status.package_revision or 0) + 1
        save_job(status)
    return digest


def load_timeline(session_id: str) -> dict:
    validate_session_id(session_id)
    with _lock, _db() as conn:
        row = conn.execute("SELECT timeline FROM jobs WHERE session_id=?",
                           (session_id,)).fetchone()
    try:
        return json.loads(row[0]) if row and row[0] else {}
    except json.JSONDecodeError:
        return {}


def list_jobs() -> list[SessionStatus]:
    with _lock, _db() as conn:
        ids = [r[0] for r in conn.execute("SELECT session_id FROM jobs").fetchall()]
    return [s for s in (load_job(i) for i in ids)
            if s is not None and s.state != SessionState.DELETED]


def capabilities(session_id: str) -> dict:
    """Original-video availability is tracked separately from reconstruction."""
    status = load_job(session_id)
    has_original = original_path(session_id).exists()
    has_package = virtual_path(session_id).exists()
    reconstructed = bool(has_package and status and status.state == SessionState.READY)
    return {
        "original_video": has_original,
        "reconstructed_scene": reconstructed,
        "package_available": has_package,
        "rebuild": has_original,
        "retryable": bool(status and status.state == SessionState.FAILED),
    }


# --- deletes (scoped, idempotent) ---

def delete_original(session_id: str) -> bool:
    path = original_path(session_id)
    removed = False
    for candidate in UPLOAD_DIR.glob(f"{validate_session_id(session_id)}_*"):
        candidate.unlink(missing_ok=True)
        removed = True
    return removed or path.exists()


def delete_virtual(session_id: str) -> bool:
    path = virtual_path(session_id)
    existed = path.exists()
    path.unlink(missing_ok=True)
    masks = DATA_DIR / "masks"
    if masks.is_dir():
        for candidate in masks.glob(f"{validate_session_id(session_id)}_*"):
            candidate.unlink(missing_ok=True)
    return existed


def delete_all(session_id: str) -> bool:
    validate_session_id(session_id)
    prior = load_job(session_id)
    existed = prior is not None and prior.state != SessionState.DELETED
    # Tombstone first: an in-flight worker checks it before committing.
    mark_tombstone(session_id)
    delete_original(session_id)
    delete_virtual(session_id)
    with _lock, _db() as conn:
        conn.execute("DELETE FROM jobs WHERE session_id=?", (session_id,))
    return existed


def data_dir() -> Path:
    return DATA_DIR
