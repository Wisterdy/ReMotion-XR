"""ReMotion XR API â€” automatic pipeline (stub AI).

Run: uvicorn app.main:app --reload   (from the Server/ directory)
"""

import logging
import os

from fastapi import FastAPI

from app.config import ensure_dirs
from app.routers.sessions import router as sessions_router

logging.basicConfig(level=logging.INFO)

app = FastAPI(title="ReMotion XR", version="0.1.0")
app.include_router(sessions_router)


@app.on_event("startup")
async def _startup() -> None:
    ensure_dirs()
    if os.environ.get("REMOTION_REQUIRE_REMOTE") == "1" and not os.environ.get("REMOTION_API_KEY"):
        raise RuntimeError("Public gateway requires REMOTION_API_KEY")
    from app.services import dispatch
    await dispatch.recover()


@app.get("/health")
async def health():
    return {"status": "ok", "service": "remotion-xr"}


@app.on_event("shutdown")
async def _shutdown():
    from app.services import dispatch
    await dispatch.stop()
