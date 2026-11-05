using NUnit.Framework;
using UnityEngine;
using ReMotionXR.Capture;
using ReMotionXR.Core;

namespace ReMotionXR.Tests
{
    public class CameraCalibrationTests
    {
        [Test] public void EncodedCropMatchesConnectedQuestAndRoundTrips()
        {
            var lens = new Pose(new Vector3(-.03216552f,-.01836131f,.06291799f),
                new Quaternion(.0956608f,.00231759f,-.00349369f,.99540514f));
            var c = QuestCameraCapture.EncodeCalibration(new Vector2(1173.06555f,1173.06555f),
                new Vector2(867.9085f,863.33148f),new Vector2Int(1728,1728),
                new Vector2Int(1280,960),lens,480,270);
            Assert.That(c.fx, Is.EqualTo(325.85154f).Within(.001f));
            Assert.That(c.fy, Is.EqualTo(244.38866f).Within(.001f));
            Assert.That(c.cx, Is.EqualTo(241.0857f).Within(.001f));
            Assert.That(c.cy, Is.EqualTo(135.13927f).Within(.001f));
            var t = JsonUtility.FromJson<SessionTimeline>(JsonUtility.ToJson(new SessionTimeline { camera_calibration=c }));
            Assert.AreEqual(lens.position,t.camera_calibration.lens_position);
            Assert.IsFalse(t.camera_calibration.metric_depth_recorded);
        }
        [Test] public void UnavailableCameraDimensionsDoNotProduceInventedCalibration()
        {
            Assert.IsNull(QuestCameraCapture.EncodeCalibration(Vector2.one,Vector2.one,
                Vector2Int.zero,new Vector2Int(1280,960),Pose.identity,480,270));
            var old = JsonUtility.FromJson<SessionTimeline>("{\"frames\":[]}").camera_calibration;
            Assert.IsTrue(old == null || old.encoded_width == 0);
        }
    }
}
