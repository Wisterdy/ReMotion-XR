"""Package a .remotion scene bundle (format remotion-2; remotion-1 readable).

Zip entries: scene.json (versioned metadata, tracks, .glb asset names,
per-asset capabilities/provenance/hashes, adaptive key poses,
accessibility cards), timeline.json, and one real .glb mesh per
reconstructed/preview asset under assets/. Large binaries are delivered
only via the download endpoint — never committed to git.
"""

import hashlib
import json
import logging
import zipfile
from pathlib import Path

log = logging.getLogger("remotion.packaging")

SCHEMA_VERSION = 2
FORMAT = "remotion-2"


def _accessibility(assets: list[dict]) -> list[dict]:
    cards = []
    for asset in assets:
        is_person = asset.get("kind") == "person"
        cards.append({
            "object_id": asset["track_id"],
            "display_name": asset.get("label", asset["track_id"]),
            "indicator": {"enabled": True, "color": "yellow" if is_person else "blue"},
            "accessibility": {
                "short_description": (
                    "The person in this capture." if is_person
                    else "The object in this capture."),
                "simple_instruction": (
                    "Watch their position relative to your hands." if is_person
                    else "Follow it with your hand through the movement."),
                "audio_file": None,
                "related_key_pose_id": 1,
            },
        })
    return cards


def build_package(session_id: str, assets: list[dict], key_poses: dict,
                  timeline: dict, out_path: Path) -> Path:
    clean_assets, model_files = [], {}
    for asset in assets:
        asset = dict(asset)
        model_bytes = asset.pop("model_bytes", None)
        if model_bytes:
            model_files[asset["model"]] = model_bytes
            asset["model_sha256"] = hashlib.sha256(model_bytes).hexdigest()
            asset["model_bytes_len"] = len(model_bytes)
        asset.setdefault("capabilities",
                         ["reconstructed"] if asset.get("reconstruction_backend") == "sam3d"
                         else ["preview"] if asset.get("model") else [])
        clean_assets.append(asset)
    unavailable = [
        {"track_id": a["track_id"], "kind": a.get("kind"),
         "reason": a["unavailable_reason"]}
        for a in clean_assets if a.get("unavailable_reason")]
    scene = {
        "session_id": session_id,
        "format": FORMAT,
        "schema_version": SCHEMA_VERSION,
        "metadata": {
            "session_id": session_id,
            "schema_version": SCHEMA_VERSION,
            "duration_seconds": float(timeline.get("duration_seconds") or 0.0),
            "timebase": "capture-monotonic-seconds",
            "units": "metres",
            "coordinate_convention": "capture-space; glTF right-handed, "
                                     "converted to Unity left-handed at load",
            "input_timeline_sha256": hashlib.sha256(
                json.dumps(timeline, sort_keys=True).encode()).hexdigest(),
        },
        "assets": clean_assets,
        "unavailable": unavailable,
        "capabilities": {
            "reconstructed_scene": any(
                "reconstructed" in a.get("capabilities", []) for a in clean_assets),
            "preview_scene": any(
                "preview" in a.get("capabilities", []) for a in clean_assets),
        },
        "key_poses": key_poses,
        "accessibility": _accessibility(clean_assets),
    }
    out_path.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = out_path.with_suffix(".tmp")
    with zipfile.ZipFile(tmp_path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.writestr("scene.json", json.dumps(scene, indent=2))
        zf.writestr("timeline.json", json.dumps(timeline, indent=2))
        for name, data in model_files.items():
            zf.writestr(name, data)
    tmp_path.replace(out_path)  # publish atomically, then mark READY
    log.info("packaging wrote %s assets=%s models=%s",
             out_path, len(clean_assets), list(model_files))
    return out_path
