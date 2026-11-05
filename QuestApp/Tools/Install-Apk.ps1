<#
.SYNOPSIS
  Guarded ReMotion APK install. Inspects the APK's actual Android
  manifest identity (aapt badging) before installing; the file name is
  never trusted. Refuses cross-kind installs:
    -Kind Diagnostic refuses applicationId com.remotion.xr.
    -Kind Normal refuses anything but com.remotion.xr, and refuses a
     sibling build manifest whose scene0 is not Main.
  Data-preserving only: uses `adb install -r`. Never uninstalls,
  never clears data. Device must be named explicitly with -Serial.
.EXAMPLE
  ./Install-Apk.ps1 -Kind Normal -ApkPath ../Builds/ReMotionXR.apk -Serial YOUR_DEVICE_SERIAL
  ./Install-Apk.ps1 -Kind Diagnostic -ApkPath ../Builds/PinchDiagnostic.apk -Serial YOUR_DEVICE_SERIAL
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Normal', 'Diagnostic')][string]$Kind,
    [Parameter(Mandatory = $true)][string]$ApkPath,
    [Parameter(Mandatory = $true)][string]$Serial,
    [string]$AndroidSdkPath = 'C:/Program Files/Unity/Hub/Editor/6000.0.51f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK'
)

$ErrorActionPreference = 'Stop'
$NormalId = 'com.remotion.xr'
$DiagnosticId = 'com.remotion.xr.pinchdiagnostic'

if (-not (Test-Path $ApkPath)) { throw "APK not found: $ApkPath" }
$aapt = Join-Path $AndroidSdkPath 'build-tools/34.0.0/aapt.exe'
$adb = Join-Path $AndroidSdkPath 'platform-tools/adb.exe'
if (-not (Test-Path $aapt)) { throw "aapt not found: $aapt" }
if (-not (Test-Path $adb)) { throw "adb not found: $adb" }

$badging = & $aapt dump badging $ApkPath | Select-Object -First 1
if ($badging -notmatch "name='([^']+)' versionCode='([^']+)' versionName='([^']+)'") {
    throw "Could not parse APK badging: $badging"
}
$packageId = $Matches[1]; $versionCode = $Matches[2]; $versionName = $Matches[3]
Write-Host "APK identity: package=$packageId versionCode=$versionCode versionName=$versionName kind=$Kind device=$Serial"

if ($Kind -eq 'Diagnostic' -and $packageId -eq $NormalId) {
    throw "REFUSED: diagnostic install would overwrite the normal app ($packageId)."
}
if ($Kind -eq 'Diagnostic' -and $packageId -ne $DiagnosticId) {
    throw "REFUSED: diagnostic APK must be $DiagnosticId, got $packageId."
}
if ($Kind -eq 'Normal' -and $packageId -ne $NormalId) {
    throw "REFUSED: normal install requires $NormalId, got $packageId."
}

# Build-manifest record (written by both builders next to the APK).
$manifestPath = "$ApkPath.build-manifest.json"
if (Test-Path $manifestPath) {
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.applicationId -ne $packageId) {
        throw "REFUSED: build manifest applicationId ($($manifest.applicationId)) != APK package ($packageId)."
    }
    if ($Kind -eq 'Normal' -and $manifest.scene0 -notmatch 'Main\.unity$') {
        throw "REFUSED: normal build manifest launches $($manifest.scene0), not Main."
    }
    if ($Kind -eq 'Diagnostic' -and $manifest.scene0 -notmatch 'PinchDiagnostic\.unity$') {
        throw "REFUSED: diagnostic build manifest scene0 is $($manifest.scene0)."
    }
    Write-Host "Build manifest OK: scene0=$($manifest.scene0) sha256=$($manifest.sha256)"
}
elseif ($Kind -eq 'Diagnostic') {
    throw "REFUSED: diagnostic install requires a build manifest ($manifestPath missing)."
}
else {
    Write-Warning "No build manifest next to APK; relying on aapt identity only."
}

& $adb -s $Serial install -r $ApkPath
if ($LASTEXITCODE -ne 0) { throw "adb install failed (exit $LASTEXITCODE). Data untouched; no uninstall attempted." }
Write-Host "Installed $packageId on $Serial (data-preserving -r)."
