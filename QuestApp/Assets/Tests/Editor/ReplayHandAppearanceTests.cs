using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR.Hands;
using ReMotionXR.Replay;
using ReMotionXR.Core;
using ReMotionXR.Capture;
using Object=UnityEngine.Object;

namespace ReMotionXR.Tests
{
    public class ReplayHandAppearanceTests
    {
        [Test]
        public void RecordedHands_MetaMeshAxesFollowFingers_AndPositionsStayExact()
        {
            string p=Path.GetFullPath(Path.Combine(Application.dataPath,"../../docs/evidence/visual-fix-scene.remotion"));
            if(!File.Exists(p))Assert.Ignore("Real capture fixture unavailable");
            Assert.IsTrue(ScenePackage.TryParseTimeline(File.ReadAllBytes(p),out var timeline,out string error),error);
            var frame=timeline.frames[timeline.frames.Count/2];
            foreach(string side in new[]{"Left","Right"})
            {
                var root=new GameObject("PoseSpace");root.transform.SetPositionAndRotation(new Vector3(2,1,-1),Quaternion.Euler(0,63,0));
                var hand=Object.Instantiate(Resources.Load<GameObject>("Hands/OpenXR"+side+"Hand"),root.transform);
                try
                {
                    var driver=hand.AddComponent<HandModelDriver>();driver.Bind(hand);
                    var joints=side=="Left"?frame.leftHandJoints:frame.rightHandJoints;
                    var rotations=side=="Left"?frame.leftHandRotations:frame.rightHandRotations;
                    Assert.IsTrue(driver.Pose(joints,rotations,root.transform));
                    foreach(var bone in hand.GetComponentsInChildren<Transform>())
                    {
                        if(!bone.name.StartsWith("XRHand_"))continue;
                        if(!System.Enum.TryParse(bone.name.Substring(7),out XRHandJointID id))continue;
                        int i=HandPoseRecorder.JointIndexOf(id); if(i<0||i>=joints.Length)continue;
                        Assert.Less(Vector3.Distance(bone.position,root.transform.TransformPoint(joints[i])),.0001f);
                        if(bone.name.EndsWith("Proximal")&&!bone.name.Contains("Thumb"))
                        {
                            Vector3 child=root.transform.TransformPoint(joints[i+1])-bone.position;
                            Assert.Greater(Vector3.Dot(-bone.forward,child.normalized),.98f,
                                "The Meta FBX's finger axis must aim along the captured finger instead of twisting its skin");
                        }
                    }
                    var material=Resources.Load<Material>("ReplayBlueHand");Assert.IsNotNull(material);
                    Assert.AreEqual("Oculus Sample/Alpha Hand Outline (SBB)",material.shader.name);
                    Assert.Greater(material.GetColor("_ColorBottom").b,material.GetColor("_ColorBottom").r);
                }
                finally{Object.DestroyImmediate(root);}
            }
        }
    }
}
