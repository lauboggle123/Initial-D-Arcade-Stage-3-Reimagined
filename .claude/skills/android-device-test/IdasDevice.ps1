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
# DXT textures to RGBA32 on the CPU (what GPUs without BC support get);
# road-log (test environment only) logs IDAS3ROAD rows at 10 Hz for Invoke-IdasDrive.
function Set-IdasFlag([Parameter(Mandatory)][ValidateSet('vertex-faces','perf-log','decode-dxt','road-log')][string]$Name, [switch]$Off) {
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
# (userdata-unity-test while the test environment flag exists). Only Travel
# (drag FULL LOCK) is still used; positions come from the logged layout
# (Get-IdasPoint). Missing fields mean the defaults; read fresh on every call.
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

# ---- Virtual gamepad (uinput) and scripted driving ----
# A uinput device with an Xbox One vendor/product id, so Android's Xbox key layout maps
# triggers (ABS_Z/ABS_RZ), hat (d-pad) and buttons. The game treats it as a connected
# controller: the touch overlay hides and the RESPONSE PROFILE's deadzone/smoothing apply.
# Lives as long as the adb shell process; Stop-IdasPad (or closing PowerShell) removes it.
$script:IdasPad = $null
$script:IdasPadState = @{ X = 0; Throttle = 0; Brake = 0; Buttons = @{} }
$script:IdasPadButtons = @{ A = 304; B = 305; X = 307; Y = 308; LB = 310; RB = 311; Back = 314; Start = 315; Guide = 316 }

function Start-IdasPad {
    if ($script:IdasPad -and -not $script:IdasPad.HasExited) { return }
    Assert-IdasDevice
    $info = New-Object Diagnostics.ProcessStartInfo (Get-IdasAdb), 'shell uinput -'
    $info.UseShellExecute = $false; $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true; $info.CreateNoWindow = $true
    $script:IdasPad = [Diagnostics.Process]::Start($info)
    $stick = '{"value":0,"minimum":-32768,"maximum":32767,"fuzz":16,"flat":128,"resolution":0}'
    $trigger = '{"value":0,"minimum":0,"maximum":1023,"fuzz":0,"flat":0,"resolution":0}'
    $hat = '{"value":0,"minimum":-1,"maximum":1,"fuzz":0,"flat":0,"resolution":0}'
    $abs = (@(0,1,3,4) | % { "{`"code`":$_,`"info`":$stick}" }) + (@(2,5) | % { "{`"code`":$_,`"info`":$trigger}" }) + (@(16,17) | % { "{`"code`":$_,`"info`":$hat}" })
    $keys = (@($script:IdasPadButtons.Values) + 317 + 318 | Sort-Object) -join ','
    Send-IdasPadLine ('{"id":1,"command":"register","name":"IDAS3 Virtual Pad","vid":1118,"pid":746,"bus":"usb","configuration":[{"type":"UI_SET_EVBIT","data":[1,3]},{"type":"UI_SET_KEYBIT","data":[' + $keys + ']},{"type":"UI_SET_ABSBIT","data":[0,1,2,3,4,5,16,17]}],"abs_info":[' + ($abs -join ',') + ']}')
    $script:IdasPadState = @{ X = 0; Throttle = 0; Brake = 0; Buttons = @{} }
    Start-Sleep 1.5 # Android and Unity enumerate the device; the overlay hides once the game sees it.
    Clear-IdasSystemPopup
}
# The test phone's game layer (nubia) pops a "<controller> / Start Game" card
# (GamepadDialogView) when a pad connects; it takes focus and swallows pad input
# until it closes itself a few seconds later. Back does not close it.
function Clear-IdasSystemPopup([double]$TimeoutSeconds = 15) {
    $until = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $focus = (Invoke-IdasAdb shell "dumpsys window | grep mCurrentFocus") -join ''
        if ($focus -match [regex]::Escape($script:IdasPackage)) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $until)
    Write-Warning "Focus is not the game: $focus"
}
function Send-IdasPadLine([string]$Line) {
    if (-not $script:IdasPad -or $script:IdasPad.HasExited) { throw 'Virtual pad is not running (Start-IdasPad).' }
    $script:IdasPad.StandardInput.WriteLine($Line); $script:IdasPad.StandardInput.Flush()
}
# Stick -1..1 (positive right), triggers 0..1. Only the given values change.
function Set-IdasPad([Nullable[double]]$Steer, [Nullable[double]]$Throttle, [Nullable[double]]$Brake) {
    $s = $script:IdasPadState
    if ($null -ne $Steer) { $s.X = [int][Math]::Round([Math]::Max(-1, [Math]::Min(1, $Steer)) * 32767) }
    if ($null -ne $Throttle) { $s.Throttle = [int][Math]::Round([Math]::Max(0, [Math]::Min(1, $Throttle)) * 1023) }
    if ($null -ne $Brake) { $s.Brake = [int][Math]::Round([Math]::Max(0, [Math]::Min(1, $Brake)) * 1023) }
    Send-IdasPadLine "{`"id`":1,`"command`":`"inject`",`"events`":[3,0,$($s.X),3,5,$($s.Throttle),3,2,$($s.Brake),0,0,0]}"
}
# Buttons: A B X Y LB RB Back Start Guide (B = upshift, X = downshift in races), or Up Down Left Right (d-pad).
function Invoke-IdasPadButton([Parameter(Mandatory)][string]$Button, [double]$Hold = .15, [double]$Wait = 1) {
    $hats = @{ Up = @(17, -1); Down = @(17, 1); Left = @(16, -1); Right = @(16, 1) }
    if ($hats.ContainsKey($Button)) { $code, $value = $hats[$Button]; $type = 3 }
    elseif ($script:IdasPadButtons.ContainsKey($Button)) { $code = $script:IdasPadButtons[$Button]; $value = 1; $type = 1 }
    else { throw "Unknown pad button $Button" }
    Send-IdasPadLine "{`"id`":1,`"command`":`"inject`",`"events`":[$type,$code,$value,0,0,0]}"
    Start-Sleep -Milliseconds ([int]($Hold * 1000))
    Send-IdasPadLine "{`"id`":1,`"command`":`"inject`",`"events`":[$type,$code,0,0,0,0]}"
    Start-Sleep -Milliseconds ([int]($Wait * 1000))
}
function Stop-IdasPad {
    if (-not $script:IdasPad) { return }
    try { if (-not $script:IdasPad.HasExited) { Set-IdasPad 0 0 0; $script:IdasPad.StandardInput.Close(); if (-not $script:IdasPad.WaitForExit(3000)) { $script:IdasPad.Kill() } } } catch { }
    $script:IdasPad = $null
}

# Latest IDAS3ROAD row (needs the test environment and Set-IdasFlag road-log, then a relaunch).
function ConvertFrom-IdasRoad([string]$Line) {
    $row = @{}; foreach ($m in [regex]::Matches($Line, '(\w+)=(-?[\d.]+)')) { $row[$m.Groups[1].Value] = [double]$m.Groups[2].Value }
    if ($row.ContainsKey('phase')) { [pscustomobject]$row } else { $null }
}

# Drives the current race to the goal with the virtual pad, from the game's own
# course-relative hint (the native course-matrix driver's aim and corner speed).
# Start it during the countdown or while running (pad connected first, e.g. from the
# Weather/Time screen). Throttle/brake follow targetSpeed*SpeedScale; lower SpeedScale
# if the car keeps touching walls (adb adds ~50-100 ms of latency).
# Screenshots every -ScreenshotEvery seconds (0 = none) plus one at the goal; they cost
# frame time, so use 0 for performance runs and read perf-log instead.
# Manual transmission: B (upshift) above -UpshiftRpm, X (downshift) below -DownshiftRpm,
# at most one shift per -ShiftCooldown seconds; automatic saves (flags bit 8) never shift.
# Returns finished/timeUp, run seconds, wall-contact rows, max speed, shifts and the screenshots.
function Invoke-IdasDrive([double]$SpeedScale = 1, [double]$TimeoutSeconds = 600, [double]$ScreenshotEvery = 0,
    [string]$ScreenshotDir = (Join-Path $script:IdasRepo 'Logs\drive'), [double]$Gain = 1,
    [double]$UpshiftRpm = 7000, [double]$DownshiftRpm = 3500, [double]$ShiftCooldown = .8) {
    Start-IdasPad
    $info = New-Object Diagnostics.ProcessStartInfo (Get-IdasAdb), 'logcat -v raw -s Unity:I -T 1'
    $info.UseShellExecute = $false; $info.RedirectStandardOutput = $true; $info.CreateNoWindow = $true
    $log = [Diagnostics.Process]::Start($info)
    $shots = New-Object Collections.Generic.List[string]; $began = [DateTime]::UtcNow; $nextShot = $began.AddSeconds($ScreenshotEvery)
    $shiftKey = 0; $nextShift = $began; $shifts = 0
    $walls = 0; $maxKmh = 0; $last = $null; $finished = $false; $timeUp = $false; $runStart = $null; $runEnd = $null; $lastRow = $began; $pending = $null
    try {
        while (([DateTime]::UtcNow - $began).TotalSeconds -lt $TimeoutSeconds) {
            if (-not $pending) { $pending = $log.StandardOutput.ReadLineAsync() }
            if (-not $pending.Wait(250)) {
                if (([DateTime]::UtcNow - $lastRow).TotalSeconds -gt 15) { throw 'No IDAS3ROAD rows for 15 s: enable Set-IdasFlag road-log in the test environment, relaunch, and start in a race.' }
                continue
            }
            $line = $pending.Result; $pending = $null
            if ($null -eq $line) { throw 'logcat stream ended.' }
            if ($line -notmatch 'IDAS3ROAD') { continue }
            $row = ConvertFrom-IdasRoad $line; if (-not $row) { continue }
            $lastRow = [DateTime]::UtcNow; $last = $row
            if ($row.phase -ge 3) { $finished = $true; $timeUp = ([int]$row.flags -band 2) -ne 0; $runEnd = [DateTime]::UtcNow; break }
            if ($row.phase -lt 2 -or $row.paused -eq 1) { Set-IdasPad 0 0 0; continue }
            if (-not $runStart) { $runStart = [DateTime]::UtcNow }
            $target = $row.target * $SpeedScale
            Set-IdasPad -Steer ($row.steer * $Gain) -Throttle ([Math]::Min(.9, [Math]::Max(0, ($target - $row.speed) * .6))) -Brake ([Math]::Min(.8, [Math]::Max(0, ($row.speed - $target) * .3)))
            if ($shiftKey) { Send-IdasPadLine "{`"id`":1,`"command`":`"inject`",`"events`":[1,$shiftKey,0,0,0,0]}"; $shiftKey = 0 }
            elseif (([int]$row.flags -band 8) -eq 0 -and [DateTime]::UtcNow -ge $nextShift) {
                # Shift is level-based per tick: press now, release on the next row (~0.1 s later).
                if ($row.rpm -gt $UpshiftRpm) { $shiftKey = $script:IdasPadButtons.B }
                elseif ($row.rpm -lt $DownshiftRpm -and $row.gear -gt 1) { $shiftKey = $script:IdasPadButtons.X }
                if ($shiftKey) { Send-IdasPadLine "{`"id`":1,`"command`":`"inject`",`"events`":[1,$shiftKey,1,0,0,0]}"; $shifts++; $nextShift = [DateTime]::UtcNow.AddSeconds($ShiftCooldown) }
            }
            if (([int]$row.flags -band 4) -ne 0) { $walls++ }
            $maxKmh = [Math]::Max($maxKmh, $row.speed * 3.6)
            if ($ScreenshotEvery -gt 0 -and [DateTime]::UtcNow -ge $nextShot) {
                $shots.Add((Save-IdasScreenshot (Join-Path $ScreenshotDir ('drive-{0:0000}m.png' -f $row.prog)))); $nextShot = [DateTime]::UtcNow.AddSeconds($ScreenshotEvery)
            }
        }
    } finally {
        try { if ($shiftKey) { Send-IdasPadLine "{`"id`":1,`"command`":`"inject`",`"events`":[1,$shiftKey,0,0,0,0]}" }; Set-IdasPad 0 0 0 } catch { }
        if (-not $log.HasExited) { $log.Kill() }
    }
    if ($finished) { Start-Sleep 1; $shots.Add((Save-IdasScreenshot (Join-Path $ScreenshotDir 'drive-goal.png'))) }
    [pscustomobject]@{ Finished = $finished -and -not $timeUp; TimeUp = $timeUp
        Seconds = if ($runStart) { [Math]::Round(((($runEnd, [DateTime]::UtcNow) | ? { $_ } | Select-Object -First 1) - $runStart).TotalSeconds, 1) } else { 0 }
        WallRows = $walls; Shifts = $shifts; MaxKmh = [Math]::Round($maxKmh, 1); Progress = if ($last) { "$($last.prog) / $($last.len) m" } else { '' }; Screenshots = $shots }
}
