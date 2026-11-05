"""Bounded observed tool motion; monocular positions are explicitly estimates.

One mesh is reused across at most 20 timestamped observations. Recorded hand
depth may guide a nearby tool; it never creates a grab event or attaches an
unobserved object to a hand. Missing video tracks stay static.
"""
import math
import numpy as np


def vec(value):
    return np.array([value[k] for k in ("x", "y", "z")], dtype=float)


def rotate(q, value):
    xyz = vec(q)
    return value + 2 * np.cross(xyz, np.cross(xyz, value) + float(q["w"]) * value)


def multiply(a, b):
    av, bv = vec(a), vec(b)
    v = float(a['w']) * bv + float(b['w']) * av + np.cross(av, bv)
    return dict(zip(('x', 'y', 'z', 'w'), map(float, [*v, float(a['w'])*float(b['w'])-np.dot(av, bv)])))


def camera_geometry(frame, timeline, width, height):
    q = frame.get('headRotation') or {'x': 0, 'y': 0, 'z': 0, 'w': 1}
    head = vec(frame['headPosition'])
    c = timeline.get('camera_calibration')
    # Unity's JsonUtility may materialize a missing nested field as a zero
    # object in older timelines. Treat that as absent, never divide by zero.
    if isinstance(c, dict) and all(isinstance(c.get(k), (int, float))
            and math.isfinite(c[k]) and c[k] > 0
            for k in ('encoded_width', 'encoded_height', 'fx', 'fy')):
        sx, sy = width / c['encoded_width'], height / c['encoded_height']
        origin = head + rotate(q, vec(c['lens_position']))
        return origin, multiply(q, c['lens_rotation']), c['fx']*sx, c['fy']*sy, c['cx']*sx, c['cy']*sy
    focal = width / (2 * math.tan(math.radians(30)))
    return head, q, focal, focal, width/2, height/2


def attach_motion(assets, timeline):
    frames = timeline.get('frames') or []
    if not frames:
        return assets
    duration = float(timeline.get('duration_seconds') or 0)
    for asset in assets:
        observations = [o for o in asset.get('video_observations', [])
                        if math.isfinite(float(o.get('timestamp', -1)))
                        and 0 <= float(o.get('timestamp', -1)) <= duration]
        observations.sort(key=lambda o: o['timestamp'])
        if len(observations) > 20:
            observations = [observations[round(i * (len(observations) - 1) / 19)] for i in range(20)]
        width, height = asset.get('image_width', 0), asset.get('image_height', 0)
        if len(observations) < 2 or width <= 0 or height <= 0:
            continue
        # Generic fallback only, never an activity/object-name-specific rule.
        size = float(asset.get('width_metres') or .30)
        poses = []
        observed_widths = []
        for observation in observations:
            x, y, w, h = observation['bbox_xywh']
            if w <= 0 or h <= 0:
                continue
            timestamp = observation['timestamp']
            frame = min(frames, key=lambda f: abs(float(f['timestamp']) - timestamp))
            origin, q, fx, fy, cx, cy = camera_geometry(frame, timeline, width, height)
            depth = min(2, max(.2, size * fx / w))
            guided = False
            hand_depths = []
            for side in ('left', 'right'):
                joints = frame.get(side + 'HandJoints') or []
                if not frame.get(side + 'Tracked') or not joints:
                    continue
                inverse = {'x': -q['x'], 'y': -q['y'], 'z': -q['z'], 'w': q['w']}
                for joint in joints:
                    local = rotate(inverse, vec(joint) - origin)
                    if not np.isfinite(local).all() or local[2] <= .1:
                        continue
                    px, py = cx + local[0] * fx / local[2], cy - local[1] * fy / local[2]
                    if x - 12 <= px <= x + w + 12 and y - 12 <= py <= y + h + 12:
                        hand_depths.append(float(local[2]))
            if hand_depths:
                depth = float(np.median(hand_depths))
                guided = True
            if guided:
                observed_widths.append(w * depth / fx)
            local = np.array([(x + w/2 - cx) * depth/fx,
                              (cy - y - h/2) * depth/fy, depth])
            point = origin + rotate(q, local)
            if not np.isfinite(point).all():
                continue
            poses.append({'timestamp': timestamp,
                          'position': dict(zip(('x', 'y', 'z'), map(float, point))),
                          'source': 'video-mask-hand-depth-estimate' if guided else 'video-mask-size-estimate'})
        if len(poses) >= 2:
            asset['object_poses'] = poses
            asset['motion_space'] = 'capture-world-estimated'
            asset['has_metric_placement'] = False
            asset['width_metres'] = float(np.median(observed_widths)) if observed_widths else size
    return assets
