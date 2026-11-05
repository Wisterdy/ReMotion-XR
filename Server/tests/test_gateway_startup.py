"""The deployable CPU gateway must start without the worker's model stack."""
import os
from pathlib import Path
import subprocess
import sys


def test_gateway_starts_without_video_or_model_dependencies(tmp_path):
    env = dict(os.environ, DATA_DIR=str(tmp_path), REMOTION_REQUIRE_REMOTE="1",
               REMOTION_API_KEY="startup-test-only")
    script = '''
import sys
for name in ("cv2", "torch", "numpy", "meta_sam_parser", "openai"):
    sys.modules[name] = None
from fastapi.testclient import TestClient
from app.main import app
with TestClient(app) as client:
    assert client.get("/health").status_code == 200
    assert client.get("/v1/sessions/missing").status_code == 401
    assert client.get("/v1/sessions/missing", headers={"X-API-Key": "startup-test-only"}).status_code == 404
'''
    result = subprocess.run([sys.executable, "-c", script], env=env,
                            cwd=Path(__file__).resolve().parents[1],
                            capture_output=True, text=True, timeout=30)
    assert result.returncode == 0, result.stderr
