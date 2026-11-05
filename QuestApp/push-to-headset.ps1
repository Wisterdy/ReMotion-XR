<#
.SYNOPSIS
  Pushes ReMotion XR to the connected Quest headset automatically:
  installs Builds\ReMotionXR.apk (reinstall/upgrade), launches the app,
  and verifies the process is running. Run from anywhere:

    powershell -ExecutionPolicy Bypass -File QuestApp\push-to-headset.ps1

  Add -Build to rebuild the APK in Unity batchmode first.
#>
param([switch]$Build)

$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot
$apk = Join-Path $projectDir "Builds\ReMotionXR.apk"
$package = "com.remotion.xr"

# Locate adb inside the Unity Android SDK (fallback: adb on PATH).
$adb = $null
$unityEditors = "C:\Program Files\Unity\Hub\Editor"
if (Test-Path $unityEditors) {
    $adb = Get-ChildItem "$unityEditors\*\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $adb) { $adb = (Get-Command adb -ErrorAction Stop).Source }

# Optional rebuild (batchmode; uses the on-disk project state).
if ($Build) {
    $unity = Get-ChildItem "$unityEditors\*\Editor\Unity.exe" | Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    Write-Host "Building APK with $unity ..."
    & $unity -batchmode -nographics -buildTarget Android -projectPath $projectDir `
        -executeMethod ReMotionXR.Editor.ReMotionBuild.BuildApkBatchmode `
        -logFile (Join-Path $projectDir "build-apk.log")
    if ($LASTEXITCODE -ne 0) { throw "Unity build failed (exit $LASTEXITCODE). See build-apk.log" }
}
if (-not (Test-Path $apk)) { throw "APK not found: $apk (build first, or run with -Build)" }

# Headset must be connected and authorized.
$devices = (& $adb devices) | Select-String -Pattern "^\S+\s+device$"
if (-not $devices) { throw "No authorized Quest headset found. Connect USB, put the headset on, and allow USB debugging." }
Write-Host "Headset: $($devices[0].Line.Trim())"

& $adb install -r $apk
if ($LASTEXITCODE -ne 0) { throw "adb install failed (exit $LASTEXITCODE)" }
Write-Host "Installed $package"

& $adb shell am force-stop $package | Out-Null
& $adb shell monkey -p $package -c android.intent.category.LAUNCHER 1 | Out-Null
Start-Sleep -Seconds 6
$pidOutput = & $adb shell pidof $package
$runningPid = if ($pidOutput) { $pidOutput.Trim() } else { "" }
if (-not $runningPid) { throw "App installed but not running after launch. Check: adb logcat -s Unity" }
Write-Host "ReMotion XR is running on the headset (pid $runningPid). Put the headset on and test."
