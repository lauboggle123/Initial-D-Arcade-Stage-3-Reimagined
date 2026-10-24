---
name: android-device-test
description: Build, install and drive the Android build of Initial D on a USB-connected phone — push game data and ROM, tap the touch overlay through menus, hold gas/steer in races, take screenshots and read Unity logs. Use when asked to test, run, profile or verify anything on an Android device for this project.
---

# Android device testing

Load the helpers first (PowerShell, from the repo root):

```powershell
. .\.claude\skills\android-device-test\IdasDevice.ps1
```

They use the adb inside Unity's Android module (it is not on PATH) and the package `com.initiald.arcadestage3`, whose data lives in `/sdcard/Android/data/com.initiald.arcadestage3/files/` (`IDAS3/`, course packs, `rom/`, `userdata-unity-scene/`).

## Build and install

1. Native plugin: `& ".\Build Android Native.cmd"` (only when `Native/` changed).
2. APK, Unity batch mode (Unity must not have the project open; takes minutes):
   ```powershell
   $u="$env:ProgramFiles\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe"; $root=(Get-Location).Path
   $p=Start-Process $u -ArgumentList @('-projectPath',"`"$root`"",'-batchmode','-quit','-buildTarget','Android','-executeMethod','Idas3Build.BuildAndroid','-logFile',"`"$root\Logs\BuildAndroid.log`"") -WindowStyle Hidden -PassThru; $p.WaitForExit()
   Select-String Logs\BuildAndroid.log -Pattern 'error CS|Shader error|Android build failed|Initial D Android APK'
   ```
3. `Install-IdasApk` (keeps app data). On a fresh install: `Start-IdasGame` once so Android creates the app folder, then `Sync-IdasData` and `Push-IdasRom <path to the user's own gds-0033.chd>`.

The build switches Unity's active target to Android and rewrites ~1,800 texture `.meta` files — never commit those. Game data is not inside the APK.

## Driving the game

- `Assert-IdasDevice` before anything. "unauthorized" or a locked screen needs the user (Unity builds restart adb, which can re-trigger the USB-debugging prompt). `Set-IdasStayAwake` stops the phone dozing during long runs.
- `Start-IdasGame -ClearLog`, then wait for `Get-IdasLog 'scene ready'` (ROM check + init ≈ 10–20 s on first launch) before tapping.
- Menus: `Invoke-IdasTap Start|A|B|View|Up|Down|Left|Right [-Wait s]`. Races: `Invoke-IdasTap Pause|View|ShiftUp|ShiftDown -Mode Race`, `Invoke-IdasHold Gas 5`, `Invoke-IdasSteer 0.5 2`.
- `Invoke-IdasHold` defaults to `-Mode Race` positions; menu holds (VIEW for Game Settings or music) need `-Mode Menu`.
- Steering-driven menus (name entry, Game Mode) can move two steps per tap; screenshot after each move. Name entry: `Down` jumps ~16 characters, END sits two right of the space character.
- Only one finger can be injected (`input motionevent`); writing raw multitouch to `/dev/input` is blocked by SELinux. Gas+steer together must be tested by a person.
- `Save-IdasScreenshot <path>` and read the PNG to confirm each screen before the next input. Taps that arrive during a screen's entry animation are ignored — wait 2–3 s after a transition, then verify.

## Test environment (use it for scripted runs)

`Set-IdasTestEnvironment` (undo with `-Off`), then relaunch. Confirm with `Get-IdasLog 'test environment'`; screenshots show a yellow "TEST ENVIRONMENT" tag at the top centre.
- Held: menu selection countdowns, Time Attack/Legend/online Continue, Time Attack lecture and ranking screens (confirm/skip still advance them), attract cycling (the title stays until START), and the in-race TIME limit (no TIME IS UP).
- Not held: the 3-2-1 race start; dialog pages, loading, banners and result animations still advance on their own (the original author keeps dialog pages automatic). Skip dialogs with START.
- A race only ends when the player reaches the goal: a Legend rival finishing first just latches the loss. To reach the result/Continue screens from a script, use Pause → RETIRE RACE → CONFIRM (counts as a time-out loss).
- Saves go to `files/userdata-unity-test`, seeded once from `userdata-unity-scene`; real progress is never touched. Delete that folder to reseed.

## Game flow facts that break scripts

- Title → START → Save Select → A (open slot) → A (CONTINUE) → Transmission → Game Mode → Course → Route → Weather/Time → (Legend: Rival dialog, skip with START) → race intro.
- Game Mode and Transmission change with **Left/Right** (steering), not Up/Down. Transmission is a toggle and the choice is remembered.
- Without the test environment: a hidden per-screen countdown (profile word 1176) auto-confirms the highlighted default (Game Mode defaults to Legend of the Street) if input is late, and Time Attack ends in "TIME IS UP" within ~60 s if the car is parked or crashed.
- While the overlay is visible it owns all touches: tapping elsewhere does not confirm. IMGUI menus (pause, settings, music) hide the overlay and take direct taps; holding View on the title opens Game Settings.
- A new driver (empty slot) goes Make → Car → Transmission → Customization → Name entry → Game Mode (seen with the AE86 defaults; cars with colour choices may add a colour step).
- Sound Room (race music, + ADD MUSIC) opens only by holding View (`-Mode Menu`, ≥0.65 s) on **Course** select (non-Legend modes) or the Legend Rival screen; the screen shows "HOLD VIEW / MUSIC SELECT" when it is allowed. Route and Weather/Time show the original BGM hint but do not open it.
- Settings close (×) first returns focus to the category list, then closes on a second press; BACK closes directly.
- File picker (ROM, music): it reopens in the last folder used. Its search only covers the current folder and depends on the media index, which may lag behind `adb push`; pushing into the folder the picker already shows is more reliable. `adb shell input text` can be converted by the phone's IME (e.g. Pinyin), so keep typed text short.

## Coordinates

`Get-IdasPoint` derives control centres from `Idas3TouchControls.Layout()` (`u = screen height / 100`) and the live screen size, assuming no display-cutout insets. If `Idas3TouchControls.cs` layout changes, update `Get-IdasPoint` to match.
