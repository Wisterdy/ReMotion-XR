# ReMotion XR — Quest App

Unity 6 / Meta Quest 3. Capture + replay only — all heavy AI runs on the
server (see `../Server/`).

## Meta Quest VR Setup

Already configured in this project (via **ReMotion XR -> Configure Quest
Project**, re-runnable any time): XR Plugin Management + OpenXR loader on
Android and Standalone, OpenXR features **Meta Quest Support** and **Hand
Tracking** enabled, Vulkan only, Multithreaded Rendering on, Minimum API
32, IL2CPP ARM64, package `com.remotion.xr`, Input Handling = Both.

The Main scene is built from **Meta XR Building Blocks** (installed by
**ReMotion XR -> Setup Main Scene** when the SDK is present): the
**Camera Rig** block (OVRCameraRig, FloorLevel tracking), **Hand
Tracking** block (OVRHand prefabs on both hand anchors), and the
**Passthrough** block (underlay layer, Insight Passthrough enabled,
center-eye camera background transparent) — so AR/MR passthrough is
playable on device, with the **World Space** Library canvas ~1.2 m in
front of the user. Without the SDK it falls back to
`VRCameraRig_Placeholder`. SDK types are loaded via AssetDatabase +
reflection, so the project compiles before the SDK import.

### Manual step: import Meta XR All-in-One SDK

The Meta SDK is **not** in `Packages/manifest.json` (it is not reliably
installable by manifest URL alone). Import it once, manually:

1. Option A (Asset Store): open the **Unity Asset Store** page for
   "Meta XR All-in-One SDK" -> **Add to My Assets** -> in Unity:
   **Package Manager -> My Assets -> Meta XR All-in-One SDK -> Download
   -> Import**.
2. Option B (Package Manager scoped registry): Package Manager ->
   gear icon -> **Advanced Project Settings** -> add scoped registry
   `OpenUPM` (`https://package.openupm.com`) with scope `com.meta.xr`,
   then install `com.meta.xr.sdk.all`.
3. Run **Meta -> Tools -> Project Setup Tool** and click **Apply All**.
4. (Optional) Define `REMOTION_META_SDK` in Player Settings -> Scripting
   Define Symbols to switch the placeholder rig to the real
   OVRCameraRig path.

### Test in Editor (mouse, UI only)

Press **Play**: the World Space canvas appears in front of the placeholder
camera; Record/Save/Library UI works with the mouse. No headset needed.

### Test on headset (Quest Link)

1. Enable Developer Mode on the Quest; connect with a USB-C Link cable.
2. In Unity: **File -> Build Settings -> Android**, switch platform.
3. Press **Play** with the headset worn — OpenXR + the Standalone XR
   loader stream the scene to the headset via Quest Link.
4. For standalone: **Build And Run** produces the Quest APK
   (`com.remotion.xr`, ARM64 IL2CPP).

## For Judges (no server needed)

1. Sideload the APK via SideQuest or Meta Quest Developer Hub.
2. Launch ReMotion XR — the Library already has the Basketball Crossover demo.
3. Tap **Enter Virtual Scene**. Replay is fully offline.

## Open in Unity

- **Unity version used: 6000.0.51f1 (Unity 6 LTS)**
- **Build target: Android, ARM64, IL2CPP** (Player Settings -> Android)
- **Package name: `com.remotion.xr`**

Manual steps:

1. Unity Hub -> **Add project** -> select this `QuestApp/` folder -> Open
   with Unity 6000.0.51f1.
2. Menu **ReMotion XR -> Setup Main Scene** (creates `Assets/Scenes/Main.unity`
   with LibraryManager, SessionRecorder, AutoProcessor, Canvas, Record and
   Save buttons, and a status label).
3. Press **Play**.
4. Test Record/Save with the local server running
   (`cd Server && uvicorn app.main:app --reload`): Record -> perform ->
   Save -> the session uploads, processes, and lands in the Library.

On first launch `LibraryManager` copies bundled demo scenes and shows
them as READY cards.

Scripts compile without the Meta SDK: SDK namespaces sit behind `#if` guards
with plain Unity fallbacks (`SessionRecorder.SampleHandsStub`).

## Scripts

| File | Role |
|---|---|
| `Assets/Scripts/Core/SessionData.cs` | FrameData, GrabEvent, KeyPose, AccessibilityData, SessionMetadata |
| `Assets/Scripts/Capture/SessionRecorder.cs` | StartRecording / StopAndSave -> `persistentDataPath/Library/{id}/` |
| `Assets/Scripts/Capture/HandPoseRecorder.cs` | Samples hand/head pose stubs at an interval |
| `Assets/Scripts/Core/AutoProcessor.cs` | Upload -> poll status -> download `.remotion` (automatic) |
| `Assets/Scripts/Library/LibraryManager.cs` | Loads Library, demo scenes, Demo Mode toggle |
| `Assets/Scripts/Library/SceneCardUI.cs` | Card UI: Watch / Enter / Retrain / God Mode / Delete / Rename |
| `Assets/Scripts/Replay/ReplayPlayer.cs` | Play/Pause, scrub, 0.25/0.5/1x, key-pose jump, loop, grab parenting |
| `Assets/Scripts/Replay/GodModeController.cs` | Tabletop diorama scale, rotate/zoom |
| `Assets/Scripts/Replay/KeyPoseManager.cs` | Adaptive key poses (direction >45°, contact, pause, extreme, grab) |
| `Assets/Scripts/Accessibility/IndicatorManager.cs` | Spawns/toggles indicators |
| `Assets/Scripts/Accessibility/ObjectIndicator.cs` | Hovering billboard circle; gaze/pinch/tap card + outline |

Demo scenes (offline `.remotion` bundles) go in
`Assets/StreamingAssets/DemoScenes/` — see the README there.

## Settings (server connection)

Open **Settings** in-app and paste your RunPod Serverless URL +
`REMOTION_API_KEY` (stored in `ServerSettings` / PlayerPrefs on-device
only, never hardcoded or committed). `AutoProcessor` submits to the
Serverless `/run` endpoint and polls `/status/{job_id}` — `IN_QUEUE`
means no GPU is free yet; polling continues until READY/FAILED.
`RUNPOD_API_KEY` is never in Quest code or the APK; it exists only in
GitHub Secrets.

## Deleting scenes

Each card offers **Delete Virtual Scene Only**, **Delete Original Video
Only**, and **Delete Entire Scene**. Every delete asks first
(Cancel / Delete — "This cannot be undone."), then deletes locally,
calls the server DELETE when online, refreshes the Library, and shows a
"Deleted" toast. Without the original, Watch Original Video disables;
without the virtual scene, Enter Virtual Scene disables and Rebuild
appears.
