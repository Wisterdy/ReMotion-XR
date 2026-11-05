param(
    [string]$Adb = 'C:\Program Files\Unity\Hub\Editor\6000.0.51f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe',
    [Parameter(Mandatory = $true)]
    [string]$Serial
)
$ErrorActionPreference = 'Stop'
$normalPackage = 'com.remotion.xr'
$diagnosticPackage = 'com.remotion.xr.pinchdiagnostic'

function Invoke-QuestAdb {
    param([string[]]$Arguments)
    $result = & $Adb -s $Serial @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw ('ADB failed: ' + ($result -join "`n"))
    }
    return $result
}

if (-not (Test-Path -LiteralPath $Adb)) { throw 'ADB executable not found.' }
$state = Invoke-QuestAdb -Arguments @('get-state')
if (($state -join '').Trim() -ne 'device') { throw 'Selected Quest is not ready.' }

$installed = Invoke-QuestAdb -Arguments @('shell', 'pm', 'path', $normalPackage)
if (-not ($installed | Where-Object { "$_" -match '^package:' })) {
    throw 'Normal ReMotion is not installed. Stop; verify the normal artifact before recovery installation.'
}

$resolved = Invoke-QuestAdb -Arguments @(
    'shell', 'cmd', 'package', 'resolve-activity', '--brief',
    '-a', 'android.intent.action.MAIN',
    '-c', 'android.intent.category.LAUNCHER', $normalPackage
)
$component = $resolved | ForEach-Object { "$_".Trim() } |
    Where-Object { $_ -match '^com\.remotion\.xr/[A-Za-z0-9_.$]+$' } |
    Select-Object -Last 1
if ([string]::IsNullOrWhiteSpace($component)) {
    throw ('Could not resolve the NORMAL app launcher: ' + ($resolved -join ' '))
}

Invoke-QuestAdb -Arguments @('shell', 'am', 'force-stop', $diagnosticPackage) | Out-Null
$launch = Invoke-QuestAdb -Arguments @(
    'shell', 'am', 'start', '-W',
    '-a', 'android.intent.action.MAIN',
    '-c', 'android.intent.category.LAUNCHER', '-n', $component
)
$launchText = $launch -join "`n"
Write-Output $launchText
if ($launchText -match '(?im)^\s*Error:|Status:\s*(?!ok\b)\w+') {
    throw 'Android did not report a successful normal-app launch.'
}
Write-Output 'Requested normal ReMotion. Verify focus and the Main menu; do not relaunch the diagnostic.'
Invoke-QuestAdb -Arguments @('shell', 'dumpsys', 'window') |
    Select-String 'mFocusedApp=.*com\.remotion\.'
Invoke-QuestAdb -Arguments @('shell', 'dumpsys', 'activity', 'activities') |
    Select-String 'mResumedActivity|topResumedActivity'
