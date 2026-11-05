using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReMotionXR.Core
{
    /// <summary>One sampled frame: head pose + left/right hand joints.</summary>
    [Serializable]
    public class FrameData
    {
        public float timestamp;
        public Vector3 headPosition;
        public Quaternion headRotation;
        public bool leftTracked; public bool rightTracked; public bool headTracked;
        public Vector3[] leftHandJoints = Array.Empty<Vector3>();
        public Vector3[] rightHandJoints = Array.Empty<Vector3>();
        // Full joint orientations from the same tracking poses that
        // drive the live blue hand prefab. Sessions recorded before
        // this field existed parse with empty arrays; replay then
        // falls back to position-aimed bones.
        public Quaternion[] leftHandRotations = Array.Empty<Quaternion>();
        public Quaternion[] rightHandRotations = Array.Empty<Quaternion>();
    }

    /// <summary>Grab or release of an object by a hand (drives replay parenting).</summary>
    [Serializable]
    public class GrabEvent
    {
        public float timestamp;
        public string hand = "right"; // "left" | "right"
        public string objectId = "";
        public string type = "grab"; // "grab" | "release"
    }

    /// <summary>Adaptive key pose. Simple actions ~2; complex sequences 8-15.</summary>
    [Serializable]
    public class KeyPose
    {
        public int id;
        public float timestamp;
        public string segment = "";
        public string phase = ""; // preparation | contact | peak
        public string label = "";
        public bool isBestPose;
    }

    /// <summary>Accessibility card data for an indicated object/person.</summary>
    [Serializable]
    public class AccessibilityData
    {
        public string objectId = "";
        public string displayName = "";
        public string indicatorColor = "blue"; // blue=Object, yellow=Person, green=Target
        public string shortDescription = "";
        public string simpleInstruction = "";
        public string audioFile = "";
        public int relatedKeyPoseId;
    }

    /// <summary>Session-level metadata saved next to timeline.json.</summary>
    [Serializable]
    public class SessionMetadata
    {
        public string sessionId = "";
        public string displayName = "Untitled Session";
        public string actionSequence = "unknown";
        public float durationSeconds;
        public bool originalVideoInvalid;
        public string status = "Processing"; // Processing | Ready | Failed
        // SAM pod job: "" (legacy/demo = ready) | Processing | Ready | Unavailable
        public string samStatus = "";
        public int samProgress;
        // Canonical gateway identity (persisted across restarts).
        public string serverSessionId = "";
        public string jobId = "";
        public int attempt;
        public bool SamReady => string.IsNullOrEmpty(samStatus) || samStatus == "Ready";
        public List<KeyPose> keyPoses = new List<KeyPose>();
    }

    /// <summary>Camera calibration in encoded-image pixels; this is not a depth measurement.</summary>
    [Serializable]
    public class CameraCalibration
    {
        public string source = "Meta PassthroughCameraAccess.Intrinsics";
        public int encoded_width, encoded_height;
        public float fx, fy, cx, cy;
        public Vector3 lens_position;
        public Quaternion lens_rotation;
        public bool metric_depth_recorded;
    }

    /// <summary>Full timeline written to timeline.json and uploaded to the server.</summary>
    [Serializable]
    public class SessionTimeline
    {
        public string action_sequence = "unknown";
        public float duration_seconds;
        public CameraCalibration camera_calibration;
        public List<FrameData> frames = new List<FrameData>();
        public List<GrabEvent> grab_events = new List<GrabEvent>();
    }
}
