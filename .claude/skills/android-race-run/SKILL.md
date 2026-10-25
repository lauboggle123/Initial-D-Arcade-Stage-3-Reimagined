---
name: android-race-run
description: Drive a full race from start to goal with a scripted driver: trained headless on PC first (train_drive_policy), then on the USB-connected Android phone through a virtual gamepad with screenshots and the perf-log race summary (avg FPS, 1% low). Use when asked to finish a race, do a full or fast run on a course, train or resume training the test driver, or check race performance on the phone (Phase 6 pass bar; the driver itself is Phase 5).
---

# Full race runs: PC training, then the phone

## Gate (user, 2026-10-25)
- The driver must race like a human, not crawl: **Akina downhill under 3:10 and Akagi downhill under 2:28.** Train on PC first; go to Android only once both targets are reached.
- Pad runs first; touch-control runs only after a course runs fine on the pad.
- Automated runs never upload times: the test environment blocks Community Times queueing and uploads (`Idas3SceneGame.AutomatedRuns`).
- Keep a replay of the best run so the user can review it (`replayFile=`, below).

## Where things are
- **Status, checkpoints, logs, resume command:** `Logs/drive-training/README.md` (git-ignored, local only). Settings files `akina-dh-current.txt` / `akagi-dh-current.txt`; `reconstruct.py` rebuilds settings from a stopped `sections` log. Summary in `docs/android-port-plan.md`, Phase 5 (this driver); the performance work it serves is Phase 6.
- **Policy (shared by PC and phone):** `TestDrivePolicy` / `TestDriveParams` in `Native/src/main.cpp`, right after `struct App`. Speed profile from centreline curvature plus a backward braking pass; pure-pursuit steering aiming to the inside of bends; 64 learned per-section speed offsets (`section`) and racing-line offsets (`line`); manual shifting from initiald-arcade.com/shift.php (user-provided: 12-gauge upgraded engine 8-9-10k rpm, normal 5.5-6-6.5k, last gear by per-course speed).
- **Trainer:** `Native/tools/train_drive_policy.cpp` → `Native/bin/train_drive_policy.exe` (CMake target `train_drive_policy` in `Native/build-unity-d`; set up MSVC like `Build Native.cmd`, then `cmake --build Native/build-unity-d --target train_drive_policy`). Its root argument is `Native` (the folder holding `data/`).
- **Car and human references** (the user's PC saves, read-only), under `%USERPROFILE%/AppData/LocalLow/Chris/Initial D Unity/userdata-unity-scene/`:
  - `saves/slot_4/driver_profiles_v1`: full-tuned AE86 (upgrade 63), used for training.
  - `time_attack_records_v1.csv`: their records. Akina DH 2'57"62 (slot 4), Akagi DH 2'34"31 (slot 3, stock car).
  - `original_race_v3_k_df_fwd_dry_car0_mt.csv` (Akina DH) and `..._h_hd_...` (Akagi DH): their per-tick ghost runs (speed, yaw, position, gear).
- **Replays:** written into the PC Replay Library (`.../userdata-unity-scene/replays/testdriver_*.idreplay`).

## PC training
Courses: 0 Myogi, 1 Usui, 2 Akagi, 3 Akina, 4 Happogahara, 5 Irohazaka, 6 Shomaru, 7 Tsuchisaka, 8 Akina Snow. `reverse 0` is downhill on Akina, Akagi, Irohazaka and Snow (`train_drive_policy Native slopes` prints elevations). A run takes about 1 s.
```
P="$USERPROFILE/AppData/LocalLow/Chris/Initial D Unity/userdata-unity-scene/saves/slot_4/driver_profiles_v1"
./Native/bin/train_drive_policy.exe Native eval 3 0 "profileDir=$P" automatic=0 $(cat Logs/drive-training/akina-dh-current.txt) "replayFile=<path>.idreplay"
./Native/bin/train_drive_policy.exe Native sections 3 0 4 "profileDir=$P" automatic=0 $(cat Logs/drive-training/akina-dh-current.txt)
./Native/bin/train_drive_policy.exe Native train 3 0 400 1 "profileDir=$P" automatic=0 ...   # random search of the global settings
```
- Inputs go through the phone's pad path (stick/triggers → RESPONSE PROFILE Previous, smoothing 0.3, deadzone 0 → `driver()`), updated every 2 ticks and applied 6 ticks late (adb latency).
- `sections` scores the **worst of delays 4/6/9** plus wall ticks/600, so results carry over to the phone's jittery latency. Single runs are chaotic: rounding one parameter can move the time by about 1 s.
- `eval` writes `%TEMP%/idas3-drive-trace.csv` (progress, speed, target, steer, pedals, wall, gear, rpm every 30 ticks), for comparison with the human ghost by distance.
- Long runs: start in the background and watch the log (`Get-Content -Wait`, or a Monitor on `^pass|after pass|best at|replay`). To pause, stop the process and rebuild the settings with `reconstruct.py`; the log prints full settings only after each pass.
- Not yet in the policy: anything learned from the human ghost; corner-entry braking beyond the speed profile.

## Phone runs (after the PC gate)
**Not yet wired to the trained policy.** The phone's `IDAS3ROAD` rows still carry the old course-matrix hint (`Idas3SceneGetRoadState` in `unity_bridge.cpp`), and `Invoke-IdasDrive` still shifts at fixed 7000/3500 rpm. Before a timed phone run:
1. Export the `TestDrivePolicy` outputs (steer, throttle, brake, shift) in the road state, with the trained settings for that course compiled in.
2. Make `Invoke-IdasDrive` forward them, and raise the log rate to 20–30 Hz.
3. Rebuild both plugins and the APK.
4. Match the car: the phone's test save drives its own AE86 GT-APEX (low tune). Use the trained car's tune or retrain for it.

Load the device helpers first and follow the `android-device-test` rules (test environment, screenshot before each next input, game flow facts):
```powershell
. .\.claude\skills\android-device-test\IdasDevice.ps1
```

### How phone runs work
- **Inputs:** a virtual Xbox-style gamepad (`Start-IdasPad`: Android's `uinput` tool through `adb shell uinput -`). It can hold gas and steer at once, which one injected finger cannot. The game treats it as a connected controller: the touch overlay hides and the RESPONSE PROFILE's deadzone/smoothing apply.
- **Road rows:** the game logs `IDAS3ROAD` rows at 10 Hz (test environment plus `road-log` flag): progress, length, lateral offset, half width, heading to road, steering hint, target speed, speed, gear, rpm, race phase. Read-only; never fed into the simulation.
- **Screenshots:** `-ScreenshotEvery <s>` saves `drive-<metres>m.png` plus `drive-goal.png`. They cost frame time: keep them sparse (30 s) or off for a measured run.
- **Performance:** with the `perf-log` flag the game logs `IDAS3PERF race-summary … avg_fps=… one_percent_low_fps=… frames_over_33ms=…` at the end of the race, plus per-second rows in `files/perf/perf-*.csv` (thermal status included).
- **Limits:** original courses only (imported courses such as Hakone report no road; the drive stops after 15 s without rows). Only the player's car is driven: no overtaking logic.

### Procedure
1. One-time setup (each needs a relaunch): `Set-IdasTestEnvironment`, `Set-IdasFlag road-log`, and for performance `Set-IdasFlag perf-log`. `Start-IdasGame -ClearLog`, then wait for `Get-IdasLog 'scene ready'`.
2. **Navigate the menus by touch** (`Invoke-IdasTap`, screenshot after each step) up to the last screen before the race. Do not connect the pad earlier:
   - The test phone's game layer shows a "<controller> / Start Game" card (`GamepadDialogView`) when the pad connects. It takes focus and swallows input for a few seconds; `Start-IdasPad` waits until the game has focus again.
   - Stray pad presses in menus are costly: on 2026-10-25 they walked into a new-driver flow and saved an empty "NO NAME" slot in the test save (deleted afterwards via DELETE SAVE).
   - Time Attack path from the title (test driver in slot 01): Start → A (slot) → A (CONTINUE) → A (transmission) → Right (Time Attack) → A → A (course) → A (route) → DRY/WET → Time of Day. Wait at least 3 s per screen; taps during entry animations are dropped.
3. **Run everything with the pad in one PowerShell call**: the pad exists only while that process runs. A full course takes minutes, so use `run_in_background` and a long timeout:
   ```powershell
   Start-IdasPad; Invoke-IdasPadButton A -Wait 3; Invoke-IdasPadButton A -Wait 1   # DRY, then DAY
   $r = Invoke-IdasDrive -TimeoutSeconds 1300 -ScreenshotEvery 30 -ScreenshotDir Logs\drive; $r | Format-List
   Get-IdasLog 'race-summary' | Select-Object -Last 1
   Stop-IdasPad
   ```
   `Invoke-IdasDrive` returns `Finished`, `TimeUp`, `Seconds`, `WallRows`, `Shifts`, `MaxKmh`, `Progress`, `Screenshots`.
4. Check the result: the screenshots (including `drive-goal.png`), `Finished` true, and the `race-summary` line. Pass bar (`docs/android-port-plan.md`, Phase 6): the tier's FPS must **hold for the whole run**, start to goal.
5. Afterwards: `Set-IdasFlag road-log -Off` / `perf-log -Off` if the next work doesn't need them, and record the phone's flag state in the plan's Housekeeping.

### Reference phone run (2026-10-25, old slow hint)
Akina downhill, Time Attack, DRY/DAY, test save AE86 MT, screenshots every 30 s: **finished** in 558 s of driving (7,888 m), max 76 km/h, wall contact in 1,457 of about 5,600 rows. perf-log: `avg_fps=60.1 one_percent_low_fps=40.8 max_ms=364.2 frames_over_33ms=31` over 566 s. Repeat with `-ScreenshotEvery 0` before reading the 1% low as the game's own.

## Touch controls
Touch runs cannot hold gas and steer together with one injected finger, so they need a different input path (not built yet).
