import asyncio
import json
import uuid
import httpx
import pytest
from app.models import SessionState
from app.services import dispatch, job_queue, storage

def test_remote_dispatch_persists_input_uses_bearer_and_requires_shared_output(monkeypatch):
    sid = uuid.uuid4().hex
    monkeypatch.setenv("REMOTION_WORKER_ENDPOINT_URL", "https://api.runpod.ai/v2/test")
    monkeypatch.setenv("RUNPOD_API_KEY", "provider-test-secret")
    seen = []
    def respond(request):
        seen.append(request)
        assert request.headers["Authorization"] == "Bearer provider-test-secret"
        if request.url.path.endswith("/run"):
            body = json.loads(request.content)
            assert body["input"]["session_id"] == sid
            assert storage.load_timeline(sid)["duration_seconds"] == 2
            return httpx.Response(200, json={"id":"job1"})
        return httpx.Response(200, json={"status":"COMPLETED", "output":{"state":"READY"}})
    original = httpx.AsyncClient
    monkeypatch.setattr(dispatch.httpx, "AsyncClient", lambda **kw: original(transport=httpx.MockTransport(respond), **kw))
    job_queue.create_job(sid)
    asyncio.run(dispatch.run(sid, {"duration_seconds":2}))
    assert len(seen) == 2
    assert storage.load_job(sid).state == SessionState.FAILED
    assert "shared volume" in storage.load_job(sid).error
    assert json.loads(dispatch.job_file(sid).read_text())["job_id"] == "job1"

def test_resume_polls_existing_job_without_resubmit(monkeypatch):
    sid=uuid.uuid4().hex
    monkeypatch.setenv("REMOTION_WORKER_ENDPOINT_URL", "https://api.runpod.ai/v2/test")
    monkeypatch.setenv("RUNPOD_API_KEY", "test")
    job_queue.create_job(sid); dispatch.save_dispatch(sid, {"job_id":"saved"})
    def respond(request):
        assert request.method == "GET"
        assert request.url.path.endswith("/status/saved")
        return httpx.Response(200,json={"status":"FAILED", "error":"GPU unavailable"})
    original=httpx.AsyncClient
    monkeypatch.setattr(dispatch.httpx,"AsyncClient",lambda **kw: original(transport=httpx.MockTransport(respond),**kw))
    asyncio.run(dispatch.run(sid,{}))
    assert storage.load_job(sid).state == SessionState.FAILED

def test_public_gateway_never_silently_runs_local_pipeline(monkeypatch):
    monkeypatch.delenv("REMOTION_WORKER_ENDPOINT_URL", raising=False)
    monkeypatch.setenv("REMOTION_REQUIRE_REMOTE", "1")
    sid=uuid.uuid4().hex; job_queue.create_job(sid)
    asyncio.run(dispatch.run(sid,{}))
    assert storage.load_job(sid).state == SessionState.FAILED

def test_worker_missing_video_fails(monkeypatch):
    import runpod_handler
    monkeypatch.setenv("REMOTION_GPU_AVAILABLE","1")
    result=runpod_handler.handle({"input":{"session_id":uuid.uuid4().hex}})
    assert result["state"]=="FAILED"
    assert "video missing" in result["error"]


@pytest.mark.parametrize("resume,code", [(False, 409), (False, 401), (True, 404)])
def test_provider_http_failure_is_actionable_without_leaking_capture_or_resubmitting(monkeypatch, resume, code):
    sid = uuid.uuid4().hex
    monkeypatch.setenv("REMOTION_WORKER_ENDPOINT_URL", "https://api.runpod.ai/v2/test")
    monkeypatch.setenv("RUNPOD_API_KEY", "private-provider-key")
    monkeypatch.delenv("REMOTION_INLINE_TRANSFER", raising=False)
    job_queue.create_job(sid)
    video = storage.original_path(sid)
    video.parent.mkdir(parents=True, exist_ok=True)
    video.write_bytes(b"saved-private-recording")
    timeline = {"duration_seconds": 9}
    if resume:
        dispatch.save_dispatch(sid, {"job_id": "existing-job"})
    requests = []

    def respond(request):
        requests.append(request)
        return httpx.Response(code, text="private-provider-key saved-private-recording")

    original = httpx.AsyncClient
    monkeypatch.setattr(dispatch.httpx, "AsyncClient", lambda **kw:
                        original(transport=httpx.MockTransport(respond), **kw))
    asyncio.run(dispatch.run(sid, timeline))
    status = storage.load_job(sid)
    assert status.state == SessionState.FAILED
    assert f"HTTP {code}" in status.error
    assert ("job status check" if resume else "scene submission") in status.error
    assert "private-provider-key" not in status.error
    assert "saved-private-recording" not in status.error
    assert "paused" not in status.error
    assert len(requests) == 1  # No blind retry or duplicate submission.
    assert requests[0].method == ("GET" if resume else "POST")
    assert video.read_bytes() == b"saved-private-recording"
    assert storage.load_timeline(sid) == timeline
