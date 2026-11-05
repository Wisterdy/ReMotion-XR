# ReMotion XR

**Capture real moments. Replay them in mixed reality.**

ReMotion XR is a Meta Quest 3 prototype that records passthrough video together
with tracked hand movements, then uses a cloud backend to reconstruct objects
for a virtual replay.

## What we accomplished

- Video recording and playback directly on Quest 3.
- Hand tracking, pinch controls, and animated blue replay hands.
- A five-column Library for recordings and virtual scenes.
- Genuine textured object models generated with SAM 3D Objects and delivered
  back to the headset for selected captures.
- Virtual replay controls for playing, pausing, scrubbing, and stepping through
  recorded moments.

Generated objects are reused alongside the recorded hand timeline. The approach
is intended for different activities without hardcoded sports or cooking rules.

## The larger goal

The original goal is to turn a recorded activity into an explorable replay of
both objects and people. For human movement, the first target is **12 observed
body poses** across a video, played automatically or reviewed one pose at a time.
This could support reviewing sports technique and other physical activities.

**Human-body reconstruction and pose replay are not implemented yet.** The
current prototype demonstrates recorded hands and object reconstruction.

## Current limitations

New scene generation is not yet reliable for every recording. Object selection,
shape, scale, and placement still need improvement. Positions are estimated;
accurate scene depth and physical hand-object contact are not yet demonstrated.
A failed cloud generation does not mean the saved original video is lost.

## Using the prototype

1. Open **Capture**, record an activity, and choose **Stop & Save**.
2. Open **Library** to watch the saved video.
3. For an entry with a completed scene, choose **Enter Virtual Scene**.

New reconstruction requires a configured backend. Downloaded scenes can be
replayed locally. A fresh installation does not include personal captures.

## Project

- `QuestApp/`: Unity application, Meta interaction, capture, and replay.
- `Server/`: Python/FastAPI backend and GPU reconstruction workers.
- Technologies: Unity, Meta Quest SDKs, SAM 3D Objects, and RunPod.

Configure your own service addresses and credentials. Private recordings,
credentials, deployment records, and internal development notes are not part
of this submission.

## License

Project code is provided under the [MIT License](LICENSE). Third-party SDKs,
models, and assets remain subject to their respective licenses.
