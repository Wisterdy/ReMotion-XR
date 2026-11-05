using NUnit.Framework;
using ReMotionXR.Core;

namespace ReMotionXR.Tests
{
    public class JointPinchTests
    {
        [Test] public void ClosedAtStartupDoesNotClick()
        {
            var latch = new JointPinchLatch();
            Assert.AreEqual(JointPinchLatch.Edge.None, latch.Sample(true, .01f));
            Assert.AreEqual(JointPinchLatch.Edge.None, latch.Sample(true, .05f));
            Assert.AreEqual(JointPinchLatch.Edge.Select, latch.Sample(true, .02f));
        }
        [Test] public void PinchJitterAndHoldGiveExactlyOnePressAndRelease()
        {
            var latch = new JointPinchLatch();
            latch.Sample(true, .05f);
            Assert.AreEqual(JointPinchLatch.Edge.Select, latch.Sample(true, .02f));
            foreach (float d in new[] { .01f, .027f, .035f, .02f })
                Assert.AreEqual(JointPinchLatch.Edge.None, latch.Sample(true, d));
            Assert.AreEqual(JointPinchLatch.Edge.Release, latch.Sample(true, .045f));
            Assert.AreEqual(JointPinchLatch.Edge.None, latch.Sample(true, .05f));
        }
        [Test] public void LostTrackingCancelsAndRequiresOpeningAgain()
        {
            var latch = new JointPinchLatch();
            latch.Sample(true, .05f); latch.Sample(true, .01f);
            Assert.AreEqual(JointPinchLatch.Edge.Cancel, latch.Sample(false, 0));
            Assert.AreEqual(JointPinchLatch.Edge.None, latch.Sample(false, 0));
            Assert.AreEqual(JointPinchLatch.Edge.None, latch.Sample(true, .01f));
            latch.Sample(true, .05f);
            Assert.AreEqual(JointPinchLatch.Edge.Select, latch.Sample(true, .01f));
        }
        [Test] public void InvalidGeometryNeverPresses()
        {
            var latch = new JointPinchLatch(); latch.Sample(true, .05f);
            Assert.AreEqual(JointPinchLatch.Edge.None, latch.Sample(true, float.NaN));
            Assert.AreEqual(JointPinchLatch.Edge.None, latch.Sample(true, .01f));
        }
    }
}
