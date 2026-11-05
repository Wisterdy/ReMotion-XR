"""Adaptive key-pose extraction from recorded joint trajectories.

Real triggers (per design): direction change >45 degrees, velocity
pause, joint extreme (peak height / furthest reach), and grab/release
events. Simple actions yield ~2 poses; complex sequences up to ~12.
Output schema is the documented SessionTimeline key-pose contract.
"""

import logging
import math

from app.services.detection import hand_positions

log = logging.getLogger("remotion.keypose")

MAX_POSES = 12


def _speeds(positions, times):
    speeds = [0.0] * len(positions)
    for i in range(1, len(positions)):
        dt = max(times[i] - times[i - 1], 1e-6)
        speeds[i] = math.dist(positions[i], positions[i - 1]) / dt
    return speeds


def _angle(a, b) -> float:
    na, nb = math.sqrt(sum(v * v for v in a)), math.sqrt(sum(v * v for v in b))
    if na < 1e-9 or nb < 1e-9:
        return 0.0
    cos = max(-1.0, min(1.0, sum(x * y for x, y in zip(a, b)) / (na * nb)))
    return math.degrees(math.acos(cos))


async def extract_key_poses(timeline: dict) -> dict:
    frames = timeline.get("frames") or []
    duration = float(timeline.get("duration_seconds") or 0.0)
    action = timeline.get("action_sequence", "recorded_action")
    candidates: dict[float, tuple[str, str]] = {}

    def add(t: float, phase: str, label: str):
        if t >= 0:
            candidates.setdefault(round(float(t), 3), (phase, label))

    if frames:
        add(float(frames[0].get("timestamp", 0.0)), "preparation", "Start")
        add(float(frames[-1].get("timestamp", duration)), "peak", "Finish")
        positions = hand_positions(frames, "right") or hand_positions(frames, "left")
        if len(positions) >= 3:
            times = [float(f.get("timestamp", i * 0.1)) for i, f in enumerate(frames[:len(positions)])]
            speeds = _speeds(positions, times)
            peak_i = max(range(len(positions)), key=lambda i: positions[i][1])
            add(times[peak_i], "peak", "Peak Height")
            far_i = max(range(len(positions)),
                        key=lambda i: math.dist(positions[i], positions[0]))
            add(times[far_i], "peak", "Furthest Reach")
            max_speed = max(speeds) or 1.0
            for i in range(1, len(positions) - 1):
                turn = _angle(
                    tuple(b - a for a, b in zip(positions[i - 1], positions[i])),
                    tuple(b - a for a, b in zip(positions[i], positions[i + 1])))
                if turn > 45.0:
                    add(times[i], "contact", "Direction Change")
                if speeds[i] < 0.1 * max_speed and speeds[i - 1] >= speeds[i]:
                    add(times[i], "preparation", "Pause")
    for event in timeline.get("grab_events") or []:
        label = "Grab Object" if event.get("type") == "grab" else "Release Object"
        add(float(event.get("timestamp", 0.0)),
            "contact" if event.get("type") == "grab" else "peak", label)
    if not candidates:
        add(0.0, "preparation", "Start")
        add(duration, "peak", "Finish")

    ordered = sorted(candidates.items())[:MAX_POSES]
    best_i = next((i for i, (_, (_, label)) in enumerate(ordered)
                   if label == "Peak Height"), len(ordered) - 1)
    poses = [{
        "id": i + 1, "timestamp": t, "segment": action, "phase": phase,
        "label": label, "is_best_pose": i == best_i,
    } for i, (t, (phase, label)) in enumerate(ordered)]
    log.info("keypose: %d pose(s) from %d frame(s)", len(poses), len(frames))
    return {"action_sequence": action, "total_duration_seconds": duration,
            "key_poses": poses}
