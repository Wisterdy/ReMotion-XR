"""Automatic important-object identification via the Meta Model API.

Chain (no manual marking, no fixed categories):
  1. Muse Spark (vision) looks at the capture's key frame and names the
     important physical objects, most important first.
  2. SAM 3.1 segments each named concept on that frame (one request per
     concept); the largest mask per concept becomes the cut-out.
  3. Cut-outs are returned in the exact shape segmentation.cut_out
     produces for SAM 2, so reconstruction/packaging/app need no
     special cases. The concept text rides along as the asset label.

App-side help: the vision prompt is told the person is acting with
their hands (grab events in the timeline mark what was held), and
surface/person concepts are filtered out — the scene renders objects.

Enabled only when MODEL_API_KEY is set (Meta Model API dashboard key).
Any failure raises; the caller falls back to the SAM 2 path so a bad
key or outage never fakes a result.
"""

import base64
import json
import logging
import os
import re
from pathlib import Path

import cv2
import numpy as np

from app.config import DATA_DIR

log = logging.getLogger("remotion.meta_identify")

BASE_URL = "https://api.meta.ai/v1"
VISION_MODEL = "muse-spark-1.3"
SAM_MODEL = "sam-3.1"
MAX_CONCEPTS = int(os.environ.get("REMOTION_MAX_CONCEPTS", "4"))
MIN_MASK_PX = 400
MAX_MASK_FRAC = 0.85  # bigger than this is scenery, not an object

# Never scene objects: people (hands/body render separately), body
# parts, clothing, and room surfaces.
DENYLIST = {
    "person", "man", "woman", "human", "hand", "hands", "arm", "arms",
    "clothing", "shirt", "room", "wall", "walls", "floor", "ceiling",
    "desk", "table", "counter", "background", "sky", "window", "door",
}

VISION_PROMPT = (
    "This is a chronological contact sheet from one first-person recording, "
    "read left-to-right then top-to-bottom. Identify the physical objects "
    "the person directly holds, manipulates, moves or uses with their hands "
    "ACROSS THE SEQUENCE. Rank by visible hand interaction, not image size "
    "or background prominence. A fixture seen during a later glance must "
    "not displace objects handled earlier. Include only objects visible in "
    "the images. Exclude body parts, clothing and room surfaces. Reply with "
    "ONLY a JSON array of up to 5 short noun phrases, most relevant first."
)


def context_frame_indices(frame_count: int) -> list[int]:
    """Chronological coverage, independent of activity and video/tracking FPS."""
    if frame_count <= 0:
        return [0]
    return sorted({min(frame_count - 1, int((i + .5) * frame_count / 6))
                   for i in range(6)})


def contact_sheet(frames: list[np.ndarray]) -> np.ndarray:
    """Keep image content and order; never supply fabricated scene context."""
    h, w = frames[0].shape[:2]
    w_out = min(w, 640); h_out = round(h * w_out / w)
    tiles = [cv2.resize(frame, (w_out, h_out)) for frame in frames]
    while len(tiles) % 3:
        tiles.append(np.zeros_like(tiles[0]))
    return np.vstack([np.hstack(tiles[i:i+3]) for i in range(0, len(tiles), 3)])


def meta_configured() -> bool:
    return bool(os.environ.get("MODEL_API_KEY"))


def _slug(text: str) -> str:
    return re.sub(r"_+", "_", re.sub(r"[^a-z0-9]+", "_", text.lower())).strip("_")


def clean_concepts(raw: list) -> list[str]:
    """Vision output -> ordered, deduped, scene-worthy concepts."""
    seen, out = set(), []
    for item in raw or []:
        if not isinstance(item, str):
            continue
        concept = " ".join(item.strip().lower().split())
        if not concept or concept in seen:
            continue
        if any(word in DENYLIST for word in concept.split()):
            continue
        seen.add(concept)
        out.append(concept)
        if len(out) >= MAX_CONCEPTS:
            break
    return out


def _frame_data_url(frame_rgb: np.ndarray) -> str:
    ok, buf = cv2.imencode(".jpg", cv2.cvtColor(frame_rgb, cv2.COLOR_RGB2BGR))
    if not ok:
        raise RuntimeError("could not encode key frame for Meta API")
    return "data:image/jpeg;base64," + base64.b64encode(buf.tobytes()).decode()


async def vision_concepts(frame_rgb: np.ndarray) -> list[str]:
    from openai import AsyncOpenAI

    api = AsyncOpenAI(base_url=BASE_URL, api_key=os.environ["MODEL_API_KEY"])
    async with api:
        resp = await api.chat.completions.create(
            model=VISION_MODEL,
            messages=[{
                "role": "user",
                "content": [
                    {"type": "text", "text": VISION_PROMPT},
                    {"type": "image_url",
                     "image_url": {"url": _frame_data_url(frame_rgb)}},
                ],
            }],
        )
    text = (resp.choices[0].message.content or "").strip()
    start, end = text.find("["), text.rfind("]")
    if start < 0 or end <= start:
        raise RuntimeError(f"vision model returned no concept list: {text[:120]!r}")
    return clean_concepts(json.loads(text[start:end + 1]))


async def _stream_events(stream):
    """Adapt SDK stream events for meta_sam_parser (cookbook pattern:
    synthesize output_text.done from content_part.done)."""
    completed = set()
    async for event in stream:
        data = event.model_dump() if hasattr(event, "model_dump") else dict(event)
        lane = (data.get("item_id"), data.get("output_index"), data.get("content_index"))
        if data.get("type") == "response.output_text.done":
            if lane not in completed:
                completed.add(lane)
                yield data
            continue
        yield data
        part = data.get("part")
        if (data.get("type") == "response.content_part.done"
                and lane not in completed
                and isinstance(part, dict)
                and part.get("type") == "output_text"
                and isinstance(part.get("text"), str)):
            completed.add(lane)
            yield {
                "type": "response.output_text.done",
                "item_id": data.get("item_id"),
                "output_index": data.get("output_index"),
                "content_index": data.get("content_index"),
                "text": part["text"],
            }


async def segment_concept(frame_rgb: np.ndarray, concept: str):
    """SAM 3.1 on one frame+concept -> (full-frame mask, bbox_xywh) or None."""
    from contextlib import aclosing

    from meta_sam_parser import (
        decode_mask_to_raster,
        image_segmentation_format,
        parse_responses_stream,
    )
    from openai import AsyncOpenAI

    api = AsyncOpenAI(base_url=BASE_URL, api_key=os.environ["MODEL_API_KEY"])
    inp = [{
        "type": "message", "role": "user",
        "content": [
            {"type": "input_text", "text": concept},
            {"type": "input_image", "image_url": _frame_data_url(frame_rgb)},
        ],
    }]
    async with api:
        stream = await api.responses.create(model=SAM_MODEL, stream=True, input=inp)
        async with stream, aclosing(_stream_events(stream)) as events:
            parsed = parse_responses_stream(events, image_segmentation_format())
            result = await parsed.final_result()
    if result.outcome.status != "completed":
        raise RuntimeError(f"SAM 3.1 incomplete for {concept!r}: {result.outcome.status}")
    height, width = frame_rgb.shape[:2]
    boxes = {r.object_id: r for r in result.records if r.kind == "box"}
    best = None
    for record in result.records:
        if record.kind != "mask":
            continue
        mh, mw = int(record.mask.height), int(record.mask.width)
        raster = np.frombuffer(decode_mask_to_raster(record.mask),
                               dtype=np.uint8).reshape(mh, mw)
        full = np.zeros((height, width), dtype=np.uint8)
        box = boxes.get(record.object_id)
        if box is not None:
            x0, y0 = int(box.left), int(box.top)
            x1, y1 = int(box.right), int(box.bottom)
            tile = cv2.resize(raster, (max(1, x1 - x0), max(1, y1 - y0)),
                              interpolation=cv2.INTER_NEAREST)
            full[y0:y1, x0:x1] = tile
        else:
            full = cv2.resize(raster, (width, height), interpolation=cv2.INTER_NEAREST)
        area = int((full > 0).sum())
        if best is None or area > best[1]:
            best = (full, area)
    if best is None:
        return None
    full, area = best
    if area < MIN_MASK_PX or area > MAX_MASK_FRAC * height * width:
        log.info("meta mask for %r rejected (area=%d)", concept, area)
        return None
    ys, xs = np.nonzero(full)
    bbox = [int(xs.min()), int(ys.min()), int(xs.max() - xs.min()), int(ys.max() - ys.min())]
    return full, bbox, area


def _grab_frame_index(timeline: dict) -> int | None:
    grabs = [g for g in timeline.get("grab_events", []) if g.get("type") == "grab"]
    if not grabs:
        return None
    frames = timeline.get("frames") or []
    if not frames:
        return None
    t0 = float(grabs[0].get("timestamp", 0.0))
    # Timeline frames are ~uniform; map the grab time to a frame index.
    duration = max((f.get("timestamp", 0.0) for f in frames), default=0.0)
    if duration <= 0:
        return None
    frac = min(1.0, max(0.0, t0 / duration))
    return int(frac * (len(frames) - 1))


def choose_main_track(records, width: int, height: int, include_observations=False):
    """Pick a video track's best (object_id, mask, bbox, area, frames).

    Pure helper over parser records: group masks by object, sum area
    across frames, keep the dominant object's largest single mask.
    """
    from meta_sam_parser import decode_mask_to_raster

    boxes = {}
    for r in records:
        if r.kind == "box":
            frame_idx = r.frame.frame_index if r.frame is not None else -1
            boxes[(r.object_id, frame_idx)] = r
    by_object: dict[str, list] = {}
    for r in records:
        if r.kind == "mask":
            by_object.setdefault(r.object_id, []).append(r)
    best = None
    for object_id, masks in by_object.items():
        total = 0
        best_mask = None
        observations = []
        best_frame_index = 0
        for r in masks:
            mh, mw = int(r.mask.height), int(r.mask.width)
            raster = np.frombuffer(decode_mask_to_raster(r.mask),
                                   dtype=np.uint8).reshape(mh, mw)
            frame_idx = r.frame.frame_index if r.frame is not None else -1
            box = boxes.get((object_id, frame_idx))
            full = np.zeros((height, width), dtype=np.uint8)
            if box is not None:
                x0, y0, x1, y1 = int(box.left), int(box.top), int(box.right), int(box.bottom)
                tile = cv2.resize(raster, (max(1, x1 - x0), max(1, y1 - y0)),
                                  interpolation=cv2.INTER_NEAREST)
                full[y0:y1, x0:x1] = tile
            else:
                full = cv2.resize(raster, (width, height), interpolation=cv2.INTER_NEAREST)
            area = int((full > 0).sum())
            total += area
            if area > 0:
                ys, xs = np.nonzero(full)
                observations.append({"frame_index": frame_idx, "bbox_xywh":
                    [int(xs.min()), int(ys.min()), int(xs.max()-xs.min()+1), int(ys.max()-ys.min()+1)]})
            if best_mask is None or area > best_mask[2]:
                ys, xs = np.nonzero(full)
                bbox = [int(xs.min()), int(ys.min()),
                        int(xs.max() - xs.min()), int(ys.max() - ys.min())] if len(xs) else [0, 0, 0, 0]
                best_mask = (full, bbox, area)
                best_frame_index = frame_idx
        frames_seen = len({r.frame.frame_index for r in masks if r.frame is not None})
        if best_mask is not None and (best is None or total > best[3]):
            best = (object_id, best_mask[0], best_mask[1], total, frames_seen, observations, best_frame_index)
    if best is None:
        return None
    object_id, mask, bbox, _total, frames_seen, observations, best_frame_index = best
    result = (object_id, mask, bbox, int((mask > 0).sum()), frames_seen)
    return result + (observations, best_frame_index) if include_observations else result


async def _upload_video(video_path: Path) -> str:
    from openai import AsyncOpenAI

    api = AsyncOpenAI(base_url=BASE_URL, api_key=os.environ["MODEL_API_KEY"])
    async with api:
        uploaded = await api.files.create(
            file=(video_path.name, video_path.read_bytes(), "video/mp4"),
            purpose="user_data")
    if not isinstance(uploaded.id, str) or not uploaded.id.startswith("file-"):
        raise RuntimeError("Meta Files API returned an invalid file id")
    return uploaded.id


async def segment_concept_video(file_id: str, concept: str,
                                width: int, height: int):
    """SAM 3.1 video mode: track the concept through the whole clip."""
    from contextlib import aclosing

    from meta_sam_parser import parse_responses_stream, video_segmentation_format
    from openai import AsyncOpenAI

    api = AsyncOpenAI(base_url=BASE_URL, api_key=os.environ["MODEL_API_KEY"])
    inp = [{
        "type": "message", "role": "user",
        "content": [
            {"type": "input_text", "text": concept},
            {"type": "input_video", "file_id": file_id},
        ],
    }]
    async with api:
        stream = await api.responses.create(model=SAM_MODEL, stream=True, input=inp)
        async with stream, aclosing(_stream_events(stream)) as events:
            parsed = parse_responses_stream(events, video_segmentation_format())
            async with parsed:
                result = await parsed.final_result()
    if result.outcome.status != "completed":
        raise RuntimeError(f"SAM 3.1 video incomplete for {concept!r}: {result.outcome.status}")
    chosen = choose_main_track(result.records, width, height, include_observations=True)
    if chosen is None:
        return None
    _oid, mask, bbox, area, frames_seen, observations, best_index = chosen
    if area < MIN_MASK_PX or area > MAX_MASK_FRAC * width * height:
        log.info("meta video mask for %r rejected (area=%d)", concept, area)
        return None
    return mask, bbox, area, frames_seen, observations, best_index


async def identify_tracks(video_path: Path | None, timeline: dict,
                          session_id: str | None, base_tracks: list[dict],
                          best_frame_index: int, extract_frame) -> list[dict]:
    """Full Meta path -> cut-out entries shaped like the SAM 2 results.

    Per video, automatically: concepts are ranked from chronological
    samples spanning the clip, then each concept is
    tracked through the whole video by SAM 3.1 video mode; the image
    mode is the per-concept fallback. Raises on any hard failure so
    the caller can fall back instead of faking it.
    """
    if video_path is None or not Path(video_path).exists():
        raise RuntimeError("Meta identification needs the uploaded video")
    cap = cv2.VideoCapture(str(video_path))
    fps = float(cap.get(cv2.CAP_PROP_FPS) or 0)
    frame_count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
    cap.release()
    context = [(i, extract_frame(video_path, i)) for i in context_frame_indices(frame_count)]
    context = [(i, frame) for i, frame in context if frame is not None]
    if not context:
        raise RuntimeError("could not extract chronological capture context")
    best_frame_index, frame_rgb = context[len(context) // 2]
    if frame_rgb is None:
        raise RuntimeError("could not extract the key frame for Meta identification")
    height, width = frame_rgb.shape[:2]

    # Assess the whole sampled action in one vision request. Peak hand speed
    # can be a final look-away/UI movement and is not evidence of importance.
    concepts = (await vision_concepts(contact_sheet([frame for _, frame in context])))[:MAX_CONCEPTS]
    log.info("meta concepts session=%s: %s", session_id, concepts)
    if not concepts:
        raise RuntimeError("vision model named no scene objects")

    file_id = None
    try:
        file_id = await _upload_video(Path(video_path))
    except Exception:
        log.exception("Meta video upload failed; using image mode per concept")

    base_object = next((t for t in base_tracks if t.get("kind") == "object"), {})
    mask_dir = DATA_DIR / "masks"
    mask_dir.mkdir(parents=True, exist_ok=True)
    results = []
    for concept in concepts:
        found = None
        visible_frames = None
        observations = []
        model_frame_index = best_frame_index
        if file_id is not None:
            try:
                found = await segment_concept_video(file_id, concept, width, height)
            except Exception:
                log.exception("Meta video segment failed for %r; image fallback", concept)
                found = None
            if found is not None:
                mask, bbox, area, visible_frames, observations, candidate_index = found
                if 0 <= candidate_index < frame_count:
                    model_frame_index = candidate_index
                else:
                    # A mask without an aligned source frame cannot drive a real model.
                    found = None
                    observations = []
                if found is not None: found = (mask, bbox, area)
        if found is None:
            # A concept visible earlier may be absent from the middle image.
            # Retry only the six already-sampled real frames, never invent a
            # mask or motion track when video segmentation did not retain it.
            observations = []
            visible_frames = None
            ordered = [context[len(context)//2]] + [item for item in context
                       if item[0] != context[len(context)//2][0]]
            for image_index, image_rgb in ordered:
                found = await segment_concept(image_rgb, concept)
                if found is not None:
                    model_frame_index = image_index
                    log.info("meta image fallback concept=%r frame=%d session=%s",
                             concept, image_index, session_id)
                    break
        if found is None:
            log.warning("meta omitted concept=%r: no usable video or context-image mask session=%s",
                        concept, session_id)
            continue
        mask, bbox, area = found
        track_id = f"{_slug(concept)}_{len(results) + 1:02d}"
        mask_path = mask_dir / f"{session_id or 'session'}_{track_id}.png"
        cv2.imwrite(str(mask_path), mask * 255)
        entry = {
            **base_object,
            "track_id": track_id,
            "kind": "object",
            "label": concept.title(),
            "concept": concept,
            "best_frame_index": model_frame_index,
            "best_frame_timestamp": model_frame_index / fps if fps > 0 else 0,
            "image_width": width, "image_height": height,
            "video_observations": [dict(o, timestamp=o["frame_index"] / fps)
                for o in observations if fps > 0 and 0 <= o["frame_index"] < frame_count],
            "cutout_ref": str(mask_path),
            "mask_area_px": area,
            "mask_bbox_xywh": bbox,
            "sam_backend": "sam3",
        }
        if visible_frames is not None:
            entry["visible_frames"] = visible_frames
        results.append(entry)
        log.info("meta cut-out concept=%r area=%d visible_frames=%s session=%s",
                 concept, area, visible_frames, session_id)
    if not results:
        raise RuntimeError("SAM 3.1 produced no usable object masks")
    return results
