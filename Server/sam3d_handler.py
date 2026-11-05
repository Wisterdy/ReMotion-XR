"""RunPod serverless handler for SAM 3D Objects.

One job: {image_b64 (jpg frame), mask_b64 (png mask), seed?}
  -> {glb_b64} a textured GLB of the masked object.

SAM 3D Objects needs a 32GB+ VRAM GPU and the gated checkpoints from
huggingface.co/facebook/sam-3d-objects. Checkpoints download once to
the network volume (HF_TOKEN env, a read token from a GitHub Secret)
and are reused across workers. Nothing is baked into the image.

Local import is side-effect free: the heavy Inference object builds
lazily on the first job so health checks and CPU-only imports work.
"""

import base64
import io
import logging
import os
import sys
from pathlib import Path

logging.basicConfig(level=logging.INFO)
log = logging.getLogger("remotion.sam3d")

CHECKPOINTS = Path(os.environ.get("REMOTION_SAM3D_DIR", "/runpod-volume/sam3d"))
_inference = None


# Pinned HF checkpoint revision (facebook/sam-3d-objects); override via env.
MODEL_REVISION = os.environ.get(
    "SAM3D_MODEL_REVISION", "2e73555018d2741ccd486e56c24fac41155a1dc6")


def _ensure_checkpoints() -> Path:
    """Download checkpoints once, into their final directory.

    Downloads into a staging sibling and atomically renames it into
    place, so a worker replaced mid-download never sees a half cache
    and no second full checkpoint copy is ever made.
    """
    import shutil

    config = CHECKPOINTS / "hf" / "pipeline.yaml"
    if config.exists():
        return config
    token = os.environ.get("HF_TOKEN")
    if not token:
        raise RuntimeError(
            "SAM 3D checkpoints missing and HF_TOKEN is not set "
            "(needs a Hugging Face read token with access to "
            "facebook/sam-3d-objects)")
    CHECKPOINTS.mkdir(parents=True, exist_ok=True)
    free_gb = shutil.disk_usage(CHECKPOINTS).free / 1e9
    if free_gb < 20:
        raise RuntimeError(
            f"only {free_gb:.1f}GB free on the checkpoint volume; "
            "refusing to start a checkpoint download that cannot finish")
    from huggingface_hub import snapshot_download

    staging = CHECKPOINTS / "hf.staging"
    log.info("downloading SAM 3D checkpoints (revision=%s) to %s",
             MODEL_REVISION, staging)
    snapshot_download(
        repo_id="facebook/sam-3d-objects",
        revision=MODEL_REVISION,
        token=token,
        local_dir=str(staging),
        max_workers=1,
    )
    src = staging / "checkpoints"
    if not (src / "pipeline.yaml").exists():
        raise RuntimeError("checkpoint download finished but pipeline.yaml not found")
    dst = CHECKPOINTS / "hf"
    if dst.exists():
        shutil.rmtree(dst)
    src.replace(dst)
    shutil.rmtree(staging, ignore_errors=True)
    if not config.exists():
        raise RuntimeError(f"checkpoint download finished but {config} not found")
    return config


def _get_inference():
    global _inference
    if _inference is None:
        config = _ensure_checkpoints()
        repo = os.environ.get("REMOTION_SAM3D_REPO", "/opt/sam-3d-objects")
        sys.path.insert(0, str(Path(repo) / "notebook"))
        from inference import Inference  # sam-3d-objects notebook module

        _inference = Inference(str(config), compile=False)
        log.info("SAM 3D inference ready (config=%s)", config)
    return _inference


def _export_glb(output: dict) -> bytes:
    """Best mesh GLB from a pipeline result; raises if none usable."""
    mesh = None
    # Prefer the finalized textured artifact over a raw stage mesh.
    for key in ("glb", "textured_mesh", "mesh"):
        if output.get(key) is not None:
            mesh = output[key]
            break
    if mesh is None:
        raise RuntimeError(f"pipeline returned no mesh (keys: {sorted(output)})")
    if isinstance(mesh, (bytes, bytearray)):
        data = bytes(mesh)
    elif hasattr(mesh, "export"):
        out = mesh.export(file_type="glb")
        data = out if isinstance(out, (bytes, bytearray)) else out.getvalue()
    elif hasattr(mesh, "save"):
        buf = io.BytesIO()
        mesh.save(buf)
        data = buf.getvalue()
    else:
        # pytorch3d-style tensors -> trimesh
        import numpy as np
        import trimesh

        verts = mesh.verts_packed().detach().cpu().numpy()
        faces = mesh.faces_packed().detach().cpu().numpy()
        data = trimesh.Trimesh(verts, faces, process=False).export(file_type="glb")
    if data[:4] != b"glTF":
        raise RuntimeError("mesh export did not produce a GLB")
    return bytes(data)


def reconstruct_glb(image_rgb, mask_bin, seed: int = 42) -> bytes:
    """frame (HxWx3 uint8 RGB) + binary mask (HxW) -> GLB bytes."""
    import numpy as np
    from PIL import Image

    inference = _get_inference()
    pipeline = inference._pipeline
    rgba = inference.merge_mask_to_rgba(
        np.asarray(image_rgb, dtype=np.uint8),
        np.asarray(mask_bin).astype(bool))
    output = pipeline.run(
        rgba,
        None,
        seed,
        stage1_only=False,
        with_mesh_postprocess=True,
        with_texture_baking=True,
        with_layout_postprocess=False,
        use_vertex_color=False,
        stage1_inference_steps=None,
        pointmap=None,
    )
    return _export_glb(output)


def handle(job: dict) -> dict:
    import cv2
    import numpy as np

    payload = (job or {}).get("input") or {}
    image_b64, mask_b64 = payload.get("image_b64"), payload.get("mask_b64")
    if not image_b64 or not mask_b64:
        return {"error": "image_b64 and mask_b64 are required"}
    try:
        image = cv2.imdecode(np.frombuffer(base64.b64decode(image_b64), np.uint8),
                             cv2.IMREAD_COLOR)
        mask = cv2.imdecode(np.frombuffer(base64.b64decode(mask_b64), np.uint8),
                            cv2.IMREAD_GRAYSCALE)
        if image is None or mask is None:
            return {"error": "could not decode image/mask"}
        glb = reconstruct_glb(cv2.cvtColor(image, cv2.COLOR_BGR2RGB),
                              mask > 127, int(payload.get("seed", 42)))
        return {"glb_b64": base64.b64encode(glb).decode(), "glb_bytes": len(glb)}
    except Exception as exc:  # surfaced to the caller, never faked
        log.exception("SAM 3D job failed")
        return {"error": f"{type(exc).__name__}: {exc}"}


if __name__ == "__main__":
    import runpod

    runpod.serverless.start({"handler": handle})
