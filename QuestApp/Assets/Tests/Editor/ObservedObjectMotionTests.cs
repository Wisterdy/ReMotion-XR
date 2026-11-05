using NUnit.Framework;
using UnityEngine;
using ReMotionXR.Core;
using ReMotionXR.Replay;

namespace ReMotionXR.Tests
{
    public class ObservedObjectMotionTests
    {
        [Test] public void ScrubUsesObservedPositionsInAnchoredSpaceAndHoldsGaps()
        {
            var root=new GameObject("Anchor"); var target=new GameObject("Object");
            target.transform.SetParent(root.transform,false);
            root.transform.SetPositionAndRotation(new Vector3(4,1,3),Quaternion.Euler(0,40,0));
            var motion=target.AddComponent<ObservedObjectMotion>();
            try {
                motion.Configure(new[]{
                    new ScenePackage.ObjectPose{timestamp=0,position=Vector3.zero},
                    new ScenePackage.ObjectPose{timestamp=1,position=Vector3.right},
                    new ScenePackage.ObjectPose{timestamp=5,position=Vector3.right*4}},null);
                motion.Apply(.5f); Assert.That(Vector3.Distance(target.transform.localPosition,Vector3.right*.5f),Is.LessThan(.001f));
                motion.Apply(3); Assert.AreEqual(Vector3.right,target.transform.localPosition);
                motion.Apply(0); Assert.AreEqual(Vector3.zero,target.transform.localPosition);
                motion.Apply(5); Assert.AreEqual(Vector3.right*4,target.transform.localPosition);
            } finally {Object.DestroyImmediate(root);}
        }
        [Test] public void LegacyStaticAssetRetainsItsPlacement()
        {
            var go=new GameObject("Static");go.transform.localPosition=Vector3.one;
            try{go.AddComponent<ObservedObjectMotion>().Configure(null,null);go.GetComponent<ObservedObjectMotion>().Apply(4);Assert.AreEqual(Vector3.one,go.transform.localPosition);}
            finally{Object.DestroyImmediate(go);}
        }
    }
}
