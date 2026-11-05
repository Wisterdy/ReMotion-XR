"""Server configuration (env-overridable, no secrets committed)."""

import os
from pathlib import Path


def _default_data_dir() -> Path:
    # DATA_DIR env wins; on RunPod Serverless the network volume mounts here.
    if os.environ.get("DATA_DIR"):
        return Path(os.environ["DATA_DIR"])
    if os.environ.get("REMOTION_DATA_DIR"):
        return Path(os.environ["REMOTION_DATA_DIR"])
    if Path("/runpod-volume").is_dir():
        return Path("/runpod-volume/data")
    return Path(__file__).resolve().parent.parent / "data"


DATA_DIR = _default_data_dir()
UPLOAD_DIR = DATA_DIR / "uploads"
PACKAGE_DIR = DATA_DIR / "packages"
JOBS_DB = DATA_DIR / "jobs.db"

# RunPod Serverless GPU fallback order (deploy config, see deploy workflow).
GPU_FALLBACK = ["RTX 4090", "RTX 3090", "RTX A5000"]
ENDPOINT_NAME = "remotion-xr-worker"

# Simulated per-stage delay for stub AI services (seconds).
STUB_DELAY_SECONDS = float(os.environ.get("REMOTION_STUB_DELAY_SECONDS", "0.4"))


def ensure_dirs() -> None:
    UPLOAD_DIR.mkdir(parents=True, exist_ok=True)
    PACKAGE_DIR.mkdir(parents=True, exist_ok=True)


# --- Gateway auth / limits (read dynamically so tests + rotation work) ---

def gateway_api_key() -> str:
    """REMOTION_API_KEY is the application credential (X-API-Key).

    It authenticates the Quest app to this gateway only. It is never a
    RunPod platform credential; RUNPOD_API_KEY stays server-side.
    """
    return os.environ.get("REMOTION_API_KEY", "")


def max_upload_bytes() -> int:
    return int(os.environ.get("REMOTION_MAX_UPLOAD_BYTES", str(512 * 1024 * 1024)))


def max_timeline_bytes() -> int:
    return int(os.environ.get("REMOTION_MAX_TIMELINE_BYTES", str(16 * 1024 * 1024)))
