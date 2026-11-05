import copy
from app.services.object_motion import attach_motion
from app.services.object_motion import camera_geometry
import numpy as np


def fixture():
    timeline={'duration_seconds':10,'frames':[{'timestamp':i/10,'headPosition':{'x':0,'y':1.6,'z':0},
        'headRotation':{'x':0,'y':0,'z':0,'w':1}} for i in range(101)]}
    asset={'label':'Mug','kind':'object','image_width':480,'image_height':270,
           'video_observations':[{'timestamp':i/10,'bbox_xywh':[100+i,100,50,70]} for i in range(101)]}
    return asset,timeline


def test_observed_motion_covers_whole_interval_and_reuses_asset():
    asset,timeline=fixture(); before=copy.deepcopy(timeline)
    attach_motion([asset],timeline)
    assert len(asset['object_poses'])==20
    assert asset['object_poses'][0]['timestamp']==0
    assert asset['object_poses'][-1]['timestamp']==10
    assert asset['object_poses'][-1]['position']['x']>asset['object_poses'][0]['position']['x']
    assert asset['has_metric_placement'] is False
    assert timeline==before  # never invent physical grab events


def test_hand_timeline_alone_never_invents_object_motion():
    asset,timeline=fixture(); asset.pop('video_observations')
    attach_motion([asset],timeline)
    assert 'object_poses' not in asset


def test_untracked_or_invalid_samples_do_not_create_world_positions():
    asset,timeline=fixture(); asset['video_observations']=[{'timestamp':float('nan'),'bbox_xywh':[0,0,4,4]}]
    attach_motion([asset],timeline)
    assert 'object_poses' not in asset


def test_calibration_uses_lens_offset_and_separate_focal_axes():
    asset,timeline=fixture()
    timeline['camera_calibration']={'encoded_width':480,'encoded_height':270,
        'fx':320,'fy':240,'cx':241,'cy':134,
        'lens_position':{'x':-.03,'y':-.02,'z':.06},
        'lens_rotation':{'x':0,'y':0,'z':0,'w':1}}
    origin,rot,fx,fy,cx,cy=camera_geometry(timeline['frames'][0],timeline,480,270)
    assert np.allclose(origin,[-.03,1.58,.06])
    assert (fx,fy,cx,cy)==(320,240,241,134)
    assert rot=={'x':0,'y':0,'z':0,'w':1}


def test_legacy_unity_zero_calibration_is_unavailable():
    _, timeline = fixture()
    expected = camera_geometry(timeline['frames'][0], timeline, 480, 270)
    timeline['camera_calibration'] = {'encoded_width':0,'encoded_height':0,'fx':0,'fy':0}
    actual = camera_geometry(timeline['frames'][0], timeline, 480, 270)
    assert np.allclose(expected[0],actual[0])
    assert expected[1:] == actual[1:]


def test_fingers_can_guide_depth_without_fabricating_a_grab():
    asset,timeline=fixture()
    for frame in timeline['frames']:
        frame['rightTracked']=True
        # Wrist misses the mask; a fingertip is at the observed object.
        frame['rightHandJoints']=[{'x':2,'y':0,'z':.5},{'x':-.12,'y':1.6,'z':.5}]
    attach_motion([asset],timeline)
    assert any(p['source']=='video-mask-hand-depth-estimate' for p in asset['object_poses'])
    assert 'grab_events' not in timeline
