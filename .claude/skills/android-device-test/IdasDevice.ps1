# Helpers for testing the Android build on a USB-connected phone.
# Usage (PowerShell):  . .\.claude\skills\android-device-test\IdasDevice.ps1
# All functions use the adb bundled with Unity's Android module.

$script:IdasPackage = 'com.initiald.arcadestage3'
$script:IdasFiles = "/sdcard/Android/data/$script:IdasPackage/files"
$script:IdasRepo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path

function Get-IdasAdb {
    $editor = if ($env:IDAS3_UNITY_EDITOR) { $env:IDAS3_UNITY_EDITOR } else { "$env:ProgramFiles\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" }
    $adb = Join-Path (Split-Path $editor) 'Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
    if (-not (Test-Path $adb)) { throw "Unity's adb not found: $adb (install Unity Android Build Support)" }
    $adb
}
function Invoke-IdasAdb { & (Get-IdasAdb) @args 2>&1 | % { "$_" } }

# Throws unless exactly one authorized device is attached.
function Assert-IdasDevice {
    # Only "<serial><TAB><state>" lines; a fresh adb server prints daemon notices first.
    $lines = Invoke-IdasAdb devices | ? { $_ -match "^\S+\t(device|unauthorized|offline|recovery|sideload)" }
    if (-not $lines) { throw 'No Android device. Connect USB and enable USB debugging.' }
    if ($lines -match 'unauthorized') { throw 'Device unauthorized: unlock the phone and accept "Allow USB debugging" (tick Always allow).' }
    if (@($lines).Count -gt 1) { throw 'More than one device attached.' }
    $power = Invoke-IdasAdb shell dumpsys window | Select-String 'isKeyguardShowing=true'
    if ($power) { Write-Warning 'Phone is locked: unlock it before launching (screenshots are black otherwise).' }
}

function Install-IdasApk([string]$Apk = (Join-Path $script:IdasRepo 'Builds\Android\InitialD-arm64.apk')) {
    Assert-IdasDevice
    Invoke-IdasAdb install -r $Apk | Select-Object -Last 1
}

# Pushes staged native data (Builds/AndroidData/IDAS3, created by
# Tools/Stage-GameData.ps1) and optional course packs into app storage.
# Launch the app once after a fresh install first so Android owns the folder.
function Sync-IdasData([string[]]$CoursePacks = @()) {
    Assert-IdasDevice
    $staged = Join-Path $script:IdasRepo 'Builds\AndroidData\IDAS3'
    if (-not (Test-Path $staged)) { throw "Stage data first: Tools\Stage-GameData.ps1 -DestinationRoot Builds\AndroidData\IDAS3 -ManifestPath Builds\AndroidData\game-data-manifest.json -CompressAssets" }
    Invoke-IdasAdb push $staged "$script:IdasFiles/" | Select-Object -Last 1
    foreach ($pack in $CoursePacks) { Invoke-IdasAdb push (Join-Path $script:IdasRepo "RuntimeAssets\$pack") "$script:IdasFiles/" | Select-Object -Last 1 }
}

# Copies the user's own dump straight into the app's rom folder (bypasses the
# in-game importer). Use -ToDownloads to place it where the file picker sees it.
function Push-IdasRom([Parameter(Mandatory)][string]$Path, [switch]$ToDownloads) {
    Assert-IdasDevice
    $target = if ($ToDownloads) { '/sdcard/Download/' + (Split-Path $Path -Leaf) } else { "$script:IdasFiles/rom/gds-0033.chd" }
    Invoke-IdasAdb push $Path $target | Select-Object -Last 1
}

function Start-IdasGame([switch]$ClearLog) {
    Assert-IdasDevice
    Invoke-IdasAdb shell am force-stop $script:IdasPackage | Out-Null
    if ($ClearLog) { Invoke-IdasAdb logcat -c | Out-Null }
    Invoke-IdasAdb shell monkey -p $script:IdasPackage -c android.intent.category.LAUNCHER 1 | Out-Null
}
function Stop-IdasGame { Invoke-IdasAdb shell am force-stop $script:IdasPackage | Out-Null }

# Keeps the screen on while USB is connected (undo: Set-IdasStayAwake -Off).
function Set-IdasStayAwake([switch]$Off) { Invoke-IdasAdb shell svc power stayon $(if ($Off) { 'false' } else { 'usb' }) | Out-Null }

# Test environment: the flag file holds every countdown except the race start
# (menus, visits, attract, in-race time limit) and switches the game to the
# separate save folder userdata-unity-test. Takes effect on the next launch.
function Set-IdasTestEnvironment([switch]$Off) {
    Assert-IdasDevice
    if ($Off) { Invoke-IdasAdb shell "rm -f $script:IdasFiles/test-environment" | Out-Null }
    else { Invoke-IdasAdb shell "touch $script:IdasFiles/test-environment" | Out-Null }
}

# Diagnostic flag files, read at launch: vertex-faces forces the scene shaders'
# no-geometry-stage path (A/B on GPUs that have one); perf-log writes
# files/perf/perf-*.csv and IDAS3PERF logcat rows; decode-dxt decodes course
# DXT textures to RGBA32 on the CPU (what GPUs without BC support get).
function Set-IdasFlag([Parameter(Mandatory)][ValidateSet('vertex-faces','perf-log','decode-dxt')][string]$Name, [switch]$Off) {
    Assert-IdasDevice
    if ($Off) { Invoke-IdasAdb shell "rm -f $script:IdasFiles/$Name" | Out-Null }
    else { Invoke-IdasAdb shell "touch $script:IdasFiles/$Name" | Out-Null }
}

# Screen size in the current (landscape) orientation.
function Get-IdasScreen {
    $size = (Invoke-IdasAdb shell wm size | Select-String 'size: (\d+)x(\d+)' | Select-Object -Last 1).Matches[0]
    $a = [int]$size.Groups[1].Value; $b = [int]$size.Groups[2].Value
    [pscustomobject]@{ Width = [Math]::Max($a, $b); Height = [Math]::Min($a, $b) }
}

# Saved touch settings from game-options.json in the active save folder
# (userdata-unity-test while the test environment flag exists). Missing
# fields mean the defaults. Saved per launch, so read fresh on every call.
function Get-IdasTouchLayout {
    $folder = Invoke-IdasAdb shell "if [ -f $script:IdasFiles/test-environment ]; then echo test; else echo scene; fi"
    $json = (Invoke-IdasAdb shell "cat $script:IdasFiles/userdata-unity-$(([string]$folder).Trim())/game-options.json 2>/dev/null") -join "`n"
    $options = $null; if ($json.Trim().StartsWith('{')) { try { $options = $json | ConvertFrom-Json } catch { } }
    $positions = @($options.touchPositions); $sizes = @($options.touchSizePercent)
    [pscustomobject]@{
        Offset = { param($slot) if ($slot -ge 0 -and $slot -lt $positions.Count -and $positions[$slot]) { [double]$positions[$slot].x, [double]$positions[$slot].y } else { 0.0, 0.0 } }.GetNewClosure()
        Scale = { param($slot) if ($slot -ge 0 -and $slot -lt $sizes.Count -and [int]$sizes[$slot] -ne 0) { [int]$sizes[$slot] / 100.0 } else { 1.0 } }.GetNewClosure()
        Travel = $(if ($options -and $options.touchSteeringTravel) { [double]$options.touchSteeringTravel / 100 } else { .15 })
    }
}

# Centre of a touch-overlay control from the live layout the game logs in the test
# environment ("IDAS3 touch layout race|menu <mode> WxH scale s: Gas=x,y ..."), so
# per-mode layouts, saved edits, safe-area clamping and the narrow-screen fit are all
# included. The overlay logs whenever it rebuilds (first shown, race <-> menu, mode or
# screen change), so the screen in question must have been shown since the log was cleared.
# Menu mode: Up Down Left Right A B View Start. Race mode: Gas Brake ShiftUp ShiftDown View Pause,
# plus Steer (DRAG) or SteerLeft SteerRight (D-PAD).
function Get-IdasPoint([Parameter(Mandatory)][string]$Control, [ValidateSet('Menu','Race')][string]$Mode = 'Menu') {
    $line = Get-IdasLog "IDAS3 touch layout $($Mode.ToLower()) " | Select-Object -Last 1
    if (-not $line) { throw "No $Mode touch layout logged yet: needs the test environment, and the $Mode screen shown since the log was cleared." }
    $m = [regex]::Match($line, " $Control=(\d+),(\d+)")
    if (-not $m.Success) { throw "$Control is not in the current $Mode layout: $line" }
    [pscustomobject]@{ X = [int]$m.Groups[1].Value; Y = [int]$m.Groups[2].Value }
}

# A 150 ms press: shorter "input tap" events can begin and end inside one frame.
function Invoke-IdasTap([Parameter(Mandatory)][string]$Control, [ValidateSet('Menu','Race')][string]$Mode = 'Menu', [double]$Wait = 1) {
    $p = Get-IdasPoint $Control $Mode
    Invoke-IdasAdb shell input swipe $p.X $p.Y $p.X $p.Y 150 | Out-Null
    Start-Sleep -Milliseconds ([int]($Wait * 1000))
}
# Holds one finger (only one pointer can be injected; simultaneous gas + steer needs a person).
function Invoke-IdasHold([Parameter(Mandatory)][string]$Control, [double]$Seconds, [ValidateSet('Menu','Race')][string]$Mode = 'Race') {
    $p = Get-IdasPoint $Control $Mode
    Invoke-IdasAdb shell input motionevent DOWN $p.X $p.Y | Out-Null
    Start-Sleep -Milliseconds ([int]($Seconds * 1000))
    Invoke-IdasAdb shell input motionevent UP $p.X $p.Y | Out-Null
}
# Drag-steer: -1 full left .. 1 full right (full lock = the saved FULL LOCK, default 15% of screen height).
function Invoke-IdasSteer([double]$Amount, [double]$Seconds) {
    $s = Get-IdasScreen; $p = Get-IdasPoint Steer Race; $x = [int]($p.X + [Math]::Max(-1, [Math]::Min(1, $Amount)) * (Get-IdasTouchLayout).Travel * $s.Height)
    Invoke-IdasAdb shell input motionevent DOWN $p.X $p.Y | Out-Null
    Invoke-IdasAdb shell input motionevent MOVE $x $p.Y | Out-Null
    Start-Sleep -Milliseconds ([int]($Seconds * 1000))
    Invoke-IdasAdb shell input motionevent UP $x $p.Y | Out-Null
}

# One finger sliding through race controls without lifting, e.g. Gas -> ShiftUp -> Gas
# (the pedal stays held) or ShiftUp -> ShiftDown. Pauses $Dwell s on each control.
function Invoke-IdasSlide([Parameter(Mandatory)][string[]]$Controls, [double]$Dwell = .3, [int]$Steps = 6) {
    $path = @($Controls | % { Get-IdasPoint $_ Race })
    Invoke-IdasAdb shell input motionevent DOWN $path[0].X $path[0].Y | Out-Null
    Start-Sleep -Milliseconds ([int]($Dwell * 1000))
    for ($i = 1; $i -lt $path.Count; ++$i) {
        for ($k = 1; $k -le $Steps; ++$k) {
            $x = [int]($path[$i - 1].X + ($path[$i].X - $path[$i - 1].X) * $k / $Steps); $y = [int]($path[$i - 1].Y + ($path[$i].Y - $path[$i - 1].Y) * $k / $Steps)
            Invoke-IdasAdb shell input motionevent MOVE $x $y | Out-Null
        }
        Start-Sleep -Milliseconds ([int]($Dwell * 1000))
    }
    $last = $path[$path.Count - 1]; Invoke-IdasAdb shell input motionevent UP $last.X $last.Y | Out-Null
}

# PNG via cmd redirection (PowerShell 5.1 pipes would corrupt binary output).
function Save-IdasScreenshot([Parameter(Mandatory)][string]$Path) {
    $adb = Get-IdasAdb; $full = [IO.Path]::GetFullPath($Path)
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    cmd /c "`"$adb`" exec-out screencap -p > `"$full`""
    $full
}

# Unity log lines (tag Unity), optionally filtered by regex.
function Get-IdasLog([string]$Pattern = '.') {
    Invoke-IdasAdb logcat -d -s Unity:I | Select-String $Pattern | % { $_.Line }
}
