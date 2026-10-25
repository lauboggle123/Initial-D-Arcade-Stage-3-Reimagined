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
- Menus: `Invoke-IdasTap Start|A|B|View|Up|Down|Left|Right [-Wait s]`. Races: `Invoke-IdasTap Pause|View|ShiftUp|ShiftDown -Mode Race`, `Invoke-IdasHold Gas 5`, `Invoke-IdasSteer 0.5 2`. With TOUCH STEERING = D-PAD: `Invoke-IdasHold SteerLeft 1` (no drag zone then). One-finger slides: `Invoke-IdasSlide Gas,ShiftUp,Gas` (gas stays held, one upshift), `Invoke-IdasSlide ShiftUp,ShiftDown`, `Invoke-IdasSlide SteerLeft,SteerRight`.
- `Invoke-IdasHold` defaults to `-Mode Race` positions; menu holds (VIEW for Game Settings or music) need `-Mode Menu`.
- Steering-driven menus (name entry, Game Mode) can move two steps per tap; screenshot after each move. Name entry: `Down` jumps ~16 characters, END sits two right of the space character.
- Editing `game-options.json` with adb: stop the game and wait a few seconds first. The app writes its in-memory options as it goes down, so a file copied in right after `Stop-IdasGame` can be overwritten (seen 2026-10-25). Check the file's mtime afterwards, and never delete the only backup until the app has relaunched with it.
- Only one finger can be injected (`input motionevent`); writing raw multitouch to `/dev/input` is blocked by SELinux. Gas+steer together must be tested by a person.
- `Save-IdasScreenshot <path>` and read the PNG to confirm each screen before the next input. Taps that arrive during a screen's entry animation are ignored — wait 2–3 s after a transition, then verify.

## Test environment (use it for scripted runs)

`Set-IdasTestEnvironment` (undo with `-Off`), then relaunch. Confirm with `Get-IdasLog 'test environment'`; screenshots show a yellow "TEST ENVIRONMENT" tag at the top centre.
- Held: menu selection countdowns, Time Attack/Legend/online Continue, Time Attack lecture and ranking screens (confirm/skip still advance them), attract cycling (the title stays until START), and the in-race TIME limit (no TIME IS UP).
- Not held: the 3-2-1 race start; dialog pages, loading, banners and result animations still advance on their own (the original author keeps dialog pages automatic). Skip dialogs with START.
- A race only ends when the player reaches the goal: a Legend rival finishing first just latches the loss. To reach the result/Continue screens from a script, use Pause → RETIRE RACE → CONFIRM (counts as a time-out loss).
- Saves go to `files/userdata-unity-test`, seeded once from `userdata-unity-scene`; real progress is never touched. Delete that folder to reseed.

## Diagnostic flags (read at launch)

- `Set-IdasFlag vertex-faces` forces the scene shaders' no-geometry-stage path (what Mali GPUs get); `Set-IdasFlag perf-log` writes `files/perf/perf-*.csv` and `IDAS3PERF` logcat rows (fps, CPU/GPU ms, `race-start`/`stage=` markers useful for waiting). `Set-IdasFlag decode-dxt` forces CPU decoding of course DXT textures (what GPUs without BC get). Undo with `-Off`; all flags are read at launch, so relaunch after changing one.
- Confirm the active path from the log: `Get-IdasLog 'scene shaders'` (geometry stage vs vertex faces), `Get-IdasLog 'textures:'` (per-course texture count, MB as uploaded, decoded or not, and Unity texture memory — use it for memory comparisons; process PSS does not include texture memory on the Adreno test phone).
- A/B image comparison between paths: hold BRAKE at the race start (the car stays put), screenshot, relaunch with the other flag and repeat. Always also take two screenshots ~2 s apart within one path: the scene has frame-to-frame noise (hundreds to thousands of pixels > 8/255), so a cross-path difference only means something when it exceeds that.
- Imported courses: Hakone is 6 × Right from Akina on Course select and adds a time-of-day screen (4 × A to the race).
- Leave the flags off when done, and record the phone's flag state in `docs/android-port-plan.md` Housekeeping.

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

`Get-IdasPoint` reads the control centres the overlay logs in the **test environment** each time it rebuilds a layout (`Get-IdasLog 'touch layout'`: `IDAS3 touch layout race|menu <DRAG|TILT|D-PAD> WxH scale s: Gas=x,y …`), so per-mode race layouts, saved edits, safe-area clamping and the narrow-screen fit are included. The layout must have been shown since the log was last cleared (a race for `-Mode Race`); without the test environment nothing is logged and the helpers throw. Each TOUCH STEERING mode has its own race layout: TILT's default puts BRAKE bottom left.
- Other displays: `Invoke-IdasAdb shell wm size 1200x1600` (4:3), `1300x1300`, `1080x2520` (21:9) resize the running game; `wm size reset` afterwards. Display-cutout emulation (`cmd overlay enable …cutout.emulation.*`) is refused on the test phone (SecurityException).
- The live steering deadzone/smoothing are logged too (`Get-IdasLog 'steering response'`): touch mode values while no controller is connected, the response profile's otherwise.
