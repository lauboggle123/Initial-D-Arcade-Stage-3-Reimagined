# Android release plan

_Last updated 2026-10-24. Branch: `android-port`._

## Context
Releases are Windows x64 / D3D11 only. Goal: an Android build that plays smoothly on low/mid devices.
Decisions (user): **sideloaded APK + first-launch data download**, **offline first** (no EOS online, no leaderboard submission in v1), **minimum = Android 8 (API 26), arm64-v8a, Vulkan, 4 GB RAM**.

Test device so far: nubia NX769J (Red Magic 9 Pro), Snapdragon 8 Gen 3 / Adreno 750, 16 GB, Android 16. It is high-end and supports geometry shaders, so low/mid and Mali results still need other hardware.

All approaches are checked against the CLAUDE.md rule: where the original author's comments record avoiding a technique, that technique is not used.

## Status at a glance
| Phase | Scope | Status |
|---|---|---|
| 1 | Native `.so`, Android Unity build, boot on device, touch controls | **Done**, committed `f9303aaa` |
| 2 | Test environment: hold every countdown except the race start | **Done**, committed `84c7d10c` |
| 3 | Shader path without geometry shader; course packs on Android | **Built, A/B-tested on Adreno with the fallback forced (2026-10-24)**; needs a real Mali device |
| 4 | ROM import, lifecycle/saves, custom music import, phone options | **Device-tested 2026-10-24, all pass** (uncommitted; small wording follow-ups below) |
| 4b | Touch controls options: layout editing, steering sensitivity, tilt | **Done, device-tested 2026-10-24** (tilt confirmed by hand) |
| 5 | Performance tiers, resolution, profiling on low/mid phones | Not started |
| 6 | Data installer, APK updates, release packaging, docs | Not started |
| 7 | Determinism gate, then online + leaderboard submission | Not started |

## Phase 1 — done (`f9303aaa`)
- Rule review: checked the overlay, `Idas3Platform` paths and disabled features against the author's comments; no conflicts.
- Native: `Native/unity_plugin.cmake` Android branch builds `libIdas3Unity.so` from the shared `IDAS3_APP_SOURCES` with `IDAS3_PORTABLE_SCENE`, `-ffp-contract=off -fno-fast-math`. Restored the missing `Native/android/scene_matrix.h`. `Build Android Native.cmd` (NDK from Unity; `--driver-mode=g++` works around clang++ being called by its 8.3 short name).
- Unity: `Idas3Platform` (data/course/ROM roots under app storage on Android; feature flags). XInput stubbed on Android; Discord, EOS online, updater and leaderboard upload disabled there (community rankings still readable). `Idas3Build.ConfigureAndroid/BuildAndroid` (IL2CPP, ARM64, Vulkan, landscape, API 26+).
- `Idas3TouchControls`: virtual Xbox-layout pad merged before control bindings. Menu mode D-pad/A/B/VIEW/START; race mode drag-steer, GAS/BRAKE, shift, VIEW, PAUSE. Overlay owns all touches while visible; hides behind IMGUI menus or when a controller is connected. Layout keeps the race HUD (tach, record panel) and chase-camera car clear.
- Verified on device: ROM check, menus and races with touch and controller, 60 FPS on menus/attract.

## Phase 2 — test environment (new)
**Why:** scripted and manual device tests kept failing because the cabinet's hidden countdowns confirmed defaults before input arrived (Game Mode auto-picked Legend; Time Attack ended in TIME IS UP while parked). In the test environment, progression happens only through player input.

**Decisions (user):** hold every countdown except the 3-2-1 race start, **including the in-race TIME limit**; **stop the attract/demo cycling**; turn it on with a **flag file on the device** (`<app files>/test-environment`). Release behaviour is unchanged when the file is absent.

**Hold (stop ticking while the flag is on)**, from the timer inventory:
- Selection countdowns that auto-confirm (shared profile word 1176 and its per-screen copies): Maker `original_frontend_transition.cpp:43`, Car `original_car_menu_transition.cpp:32` (car colour reads the same counter, `frontend.cpp:587`), Transmission `original_transmission_menu_transition.cpp:33`, Game Mode `original_mode_menu_transition.cpp:45`, Name entry `original_name_entry.cpp:126`, Tuning course menu `original_tuning_course_menu.cpp:23`, Result-tuning offer `original_tuning_child.cpp:107`, Legend Continue / next-rival / credit `original_legend_return.cpp:24` (`timer()`), Time Attack Continue `original_time_attack_visit.cpp:207`, Online Continue `online_result_screen.h:51`.
- Time Attack lecture (`original_time_attack_visit.cpp:174`) and ranking board (`:193`). Confirm/skip still advance them (`:185`, `:202`), so the player is never stuck.
- Attract/demo sequencing: `Frontend::advanceAttractChild` (`frontend.cpp:503-526`) stops advancing, so the title stays on its current attract screen until START.
- In-race TIME limit: `OriginalRaceRules` stops decreasing `state_.remaining` (`original_race_rules.cpp`, around `:168-179`). Elapsed/lap time keeps counting, and checkpoint extensions and records behave normally; the race just can't end on time.

**Keep running (not changed):**
- The 3-2-1 start (`original_race_start.cpp:9-22`), as requested.
- Presentation timers that never pick a choice: rival/pre-race dialog page turns, loading screen, VS banner, finish banner, battle-result animation, tuning animations, new-record pause, ending credits. The author explicitly avoids requiring input per dialog page: "the pages turn themselves once each one has finished; a per-page button would strand the player on the first one" (`main.cpp:3150-3152`). Dialogs can still be skipped with START.
- Unity safety and online timers: display-settings 15 s revert (`Idas3GameOptions.cs:230`), control-binding capture 15 s cancel (`Idas3ControlBindings.cs:323`), challenger overlay and online release deadlines (online is off on Android).

**How (rule-checked):**
- Hold by **not decrementing**, never by refilling or writing timer values. The author's own hold works this way: a covering lobby keeps Mode's "profile countdown and exit transition … suspended" (`frontend.h:94-95`, `modeCovered`). Verification code notes "no forced timer/result writes" (`main.cpp:3782`). Every phase still ticks; only the counter stays put. The author warns against ticking only parts of a screen ("Never tick the interactive phases here", `frontend.cpp:676-679`), so fades, music and exit transitions run normally.
- This supersedes the `Idas3SceneHoldMenuCountdowns` hook in `stash@{0}`, which refilled counters each frame and could persist a forced value into profile word 1176 (saved when a visit ends, `main.cpp:2319-2320`). Do not reuse it.
- One native switch: a small header (e.g. `Native/src/original_test_environment.h`) exposing `originalCountdownsHeld()`. Each decrement site above becomes `if(!originalCountdownsHeld()) <original decrement>`, leaving the original line otherwise untouched.
- Export `Idas3SceneSetTestEnvironment(int)` in `unity_bridge.h/.cpp`, callable before `Idas3SceneInitialize`; mirror it in `Assets/Scripts/Idas3Native.cs`.
- `Idas3SceneGame.InitializeGame`: if `Path.Combine(Application.persistentDataPath, "test-environment")` exists, enable the switch before native init and log `IDAS3 test environment: countdowns held`. Draw a small persistent "TEST ENVIRONMENT" label (IMGUI, corner) so every screenshot shows the mode.
- Saves: the test environment uses its own save folder `userdata-unity-test`, so held or extended runs never touch real progress or records. On first use it is seeded once from `userdata-unity-scene`, using the same verified copy as the existing prior-save import in `Idas3SceneGame.InitializeGame`. This follows the project's practice of isolated save folders for diagnostics.
- Test skill: add `Set-IdasTestEnvironment [-Off]` to `.claude/skills/android-device-test/IdasDevice.ps1` (creates or removes the flag file), and update `SKILL.md`: scripts should enable it; the countdown and TIME IS UP gotchas apply only when it is off.

**Verification:**
- Native CTest (new `test_environment_timers_tests`, following the existing `Native/tests/*_tests.cpp` pattern): for each held transition, tick far past its original limit with the switch on and confirm no timeout and no commit; with the switch off, confirm the original timeout still happens at the original tick. Same for `OriginalRaceRules`: `remaining` constant while held, elapsed time advancing, and TimeUp only after the switch is turned off.
- Windows: build `Idas3Unity.dll` and confirm normal (no-flag) behaviour is unchanged, e.g. Game Mode still auto-confirms Legend after its countdown.
- Device (via the skill): with the flag on, wait about 2 minutes on Title, Save Select, Transmission, Game Mode, Name entry and Time Attack Continue, and screenshot that nothing advanced. Park in a Time Attack race for 3 minutes with no TIME IS UP. Confirm the dialogs still page by themselves and the 3-2-1 still runs. Remove the flag and confirm the original timers return. Check that real saves under `userdata-unity-scene` are untouched.
- Clean up the phone: remove the obsolete `test-hold-menu-countdowns` and `ab-geometry-shader` flag files.

## Phase 3 — shader path without geometry shader; course packs (built)
As built (2026-10-24), following "Proposed when resumed" below:
- Scene shaders (`Idas3Scene`, `Idas3SceneDirect`, `Idas3OpaqueAlphaDepth`): the original geometry-stage SubShader is unchanged at `LOD 200`; a second `LOD 100`, target 4.5 SubShader defines `IDAS_VERTEX_FACES`. Unity picks it automatically where target 5.0 is unsupported. `Idas3Platform.SceneVertexFaces` (no geometry shaders, `-idas3-vertex-faces`, or a `vertex-faces` flag file) also forces it via `Shader.maximumLOD` for A/B on Adreno. Before, a GPU without geometry shaders could not start the scene renderer at all ("Original Unity scene shader is unavailable").
- D3 ranges that need the geometry stage (`courseCullMode >= 2` or final-vertex flat colour) get 136-byte vertices on that path only (`Idas3SceneRenderer.WideVertex`): the triangle's world plane `cross(p1-p0,p2-p0), -dot(cross,p0)` and the final vertex's position/normal/colours, identical on all three corners. The vertex stage applies `showroomGeometry`'s winding rule against `eye` and collapses hidden triangles to one point; flat ranges light the final vertex's inputs once. No provoking vertex, no `nointerpolation`, no hardware culling.
- Imported courses use their existing per-vertex planes (`SceneryFacePlanes`) on that path: Stage 8 courses as well as IDZero (tree packing stays IDZero-only); Enna's paired guardrails/gates are un-indexed so each triangle has its own plane. All imported placements use positive scale, so the object-space test equals the geometry stage's world-space test.
- Course textures: `Idas3DxtDecoder` expands each declared DXT1/DXT5 mip to RGBA32 when the GPU lacks BC (`Idas3Platform.DecodeCompressedTextures`; force with `-idas3-decode-dxt` / `decode-dxt` file). Packs are never transcoded. 35 of 626 pack textures store more levels than their header declares; like `LoadRawTextureData`, the declared chain is read from the start. `python Tests/Courses/test_dxt_decoder.py` checks hand-built BC1/BC3 blocks and decodes all 626.
- `Idas3PerformanceLog` restored from `stash@{0}` (now opt-in: `-idas3-perf-log` or a `perf-log` flag file); `IdasDevice.ps1` `Set-IdasFlag vertex-faces|perf-log`.

Pre-existing Android bugs found and fixed on the way (affected Adreno too):
- No imported course (Hakone, Sadamine, Tsubaki, Gunsai, …) ever drew on Android: their materials declare `_ImportedLampCells/_ImportedLampLights`, bound only for IDZero night packs, and Vulkan skips draws with unbound buffers ("Attempting to draw with missing bindings"). The renderer now binds empty global placeholders.
- `Idas3SceneDirect`'s `IDAS_IMPORTED_VERTEX_FACES` variant failed to compile for Vulkan (`GL_ARB_cull_distance` unsupported). Outside D3D11 hidden paired faces now collapse instead of using `SV_CullDistance`; D3D11 is unchanged.

Results (Red Magic 9 Pro / Adreno 750, test environment, 2480×1116, 4× MSAA):
- Akina Time Attack, 15 s of driving: geometry stage ~60 FPS, GPU 14.0–15.0 ms, render thread 7.6–10.4 ms; vertex faces ~60 FPS, GPU 13.3–14.4 ms, render thread 7.4–9.4 ms, 1,872 of 1,912 ranges wide. (The `stash@{0}` attempt was 52.6 FPS.)
- Image parity, car held on the brake at the start line: within one mode 2 s apart, 216/447 of 586,044 view pixels differ by >8/255 (max 64); geometry stage vs vertex faces 572 (max 65), i.e. at the scene's own frame-to-frame noise.
- Hakone: draws in both modes (GPU ~16 ms geometry stage, ~12–14 ms vertex faces), trees and paired faces one-sided; with DXT decode forced, renders correctly, course load ~21 s.
- Enna (phone): draws on both paths, ~60 FPS, GPU 12.9–13.9 ms either way. Held on the brake at the start, geometry stage vs vertex faces differ in 164 view pixels by >8/255 (max 19), less than two frames of one path 2 s apart (2,580–4,906, max 37–40).
- Windows D3D11 (`Build Unity.cmd`, player run with `-idas3-mode-flow-smoke`): `-idas3-enna-gates-check` passes on both paths (241 / 401 checks; the extra checks are the vertex-face keyword) and its 16 captures are bit-identical between paths. `-idas3-pause-course-check` passes on both (83 checks, 7 captures: Momiji, Myogi SS, Usui SS, Enna, Hakone, Sadamine, Akina); 5 captures are bit-identical, Myogi SS differs in 781 pixels (0.19%, max 27) and Hakone in 7 (max 14), all in dark foliage at the frame edge. Two geometry-stage runs are bit-identical, so these are real path differences, not noise; most likely edge-on paired faces where the two plane tests round differently (not proven).
- Decoded-texture memory (phone, uploaded size from the course log line, all mips): Hakone 13.1 → 70.4 MB, Sadamine 48.4 → 127.0 MB; Unity texture memory 31.5 → 88.8 MB and 67.5 → 146.1 MB. GUNSAI's 4096² DXT1 atlas alone is ≈ 85 MB decoded (calculated, not measured). Process PSS does not include it on this phone.
- Still unverified: a real Mali/no-geometry-shader GPU.
- The rebuilt Windows `Idas3Unity.dll` now exports `Idas3SceneSaveNow` (Phase 4 note).

What was tried first (all in `stash@{0}`, also kept on branch `android-phase3-stash-backup`):
- Vertex-stage replacement for the geometry stage (reads each triangle's vertices from the range mesh's raw vertex buffer; Vulkan SubShaders at target 4.5; D3D11 unchanged).
- `Idas3PerformanceLog` (per-second CSV + logcat: FPS, frame-time spread, CPU main/render, GPU, native time, thermal), A/B test shaders, native `Idas3SceneHoldMenuCountdowns` test hook (superseded by Phase 2).

Findings (Akagi Downhill, same phone):
- Original geometry-shader path: 58.8–59.5 FPS average, 0–5% of driving below 45 FPS.
- Vertex-stage path: 52.6 FPS, 29% below 45 FPS. Dense sections cost about +3.5 ms render-thread CPU and +2 ms GPU per frame. Cause: ~98.5% of ranges need face work, and each draw binds its own buffer (Vulkan descriptor churn) plus double lighting for flat shading. Binding only where needed did not help.
- The game's simulation is ~4–6 ms and never the bottleneck; rendering is (≈1,700–2,000 draws, mirror draws again, 13 ms GPU even on Adreno 750 at 1080p 4× MSAA).

Proposed when resumed:
1. Use the original geometry-shader path wherever the GPU supports it (Adreno). This is the author's own technique and the measured fast path.
2. Fallback only for GPUs without geometry shaders (many Mali), keeping the author's two constraints:
   - Winding is tested in world space before projection, never via Unity front-face state, hardware `Cull` or `GL.invertCulling` (`Idas3SceneCommon.cginc:262-263`, `Idas3SceneRenderer.cs:631-632`). Use the author's existing per-vertex face-plane technique (`IDAS_IMPORTED_VERTEX_FACES`: all three vertices carry the triangle's plane; a hidden face is rejected in the vertex stage), with the plane baked when a range is uploaded instead of read from a bound buffer per draw.
   - The flat colour is the explicitly selected final vertex, never the GPU provoking vertex or `nointerpolation` (`Idas3SceneCommon.cginc:246-248`). Give each vertex of a flat-shaded triangle the final vertex's lighting inputs as extra attributes, so all three compute the identical lit colour.
   - Cost to measure before adopting: wider vertices, upload-time expansion, and double lighting for flat ranges. If it can't approach the geometry-shader numbers, decide whether non-geometry-shader GPUs are supported at all.
   - The `stash@{0}` vertex-stage attempt relies on the provoking vertex, so it must not be resumed as-is; only its logger is reusable.
3. Course packs: **no transcoding** of the original textures. `Tools/Stage-GameData.ps1` stages "no transcoding or asset filtering" and packs only losslessly, and the music importers say "No transcoding". Keep the DXT payloads and handle GPUs without BC support at load (lossless decompression), addressing memory through loading and residency rather than recompression: unload courses not in use, and optional user-selected mip limits as a quality setting that never alters the files.

## Phase 4 — ROM import, lifecycle, music, options (written, awaiting device test)
- ROM import: ROM screen action **SELECT ROM FILE** → Android document picker (`Assets/Plugins/Android/Idas3FilePicker.java`, headless framework Fragment, copies on a worker thread) → progress bar → renames to the validator's names (`.chd` → `gds-0033.chd`; cue + 3 bins → expected names) → existing hash check. C# wrapper `Idas3AndroidFiles.cs`; `com.unity.modules.androidjni` added to `Packages/manifest.json`.
- Lifecycle: `OnApplicationPause(true)` opens the pause menu in an offline race (native pause) and calls new native `Idas3SceneSaveNow` (same `saveSettings()` as shutdown). `Application.lowMemory` releases unused assets.
- Custom music: Add music opens the picker for audio files, copies into `Custom Music`, imports each.
- Options: Discord, wheel force feedback, mute-when-unfocused show "N/A ON ANDROID" and ignore changes; Updates tab explains APK updates; "saved on this device".
- Deferred to Phase 4b (user decision): touch steering sensitivity / tilt options.
- Rule review: no conflicts found. The "a local original dump is never read or copied" comment in `Idas3Build.cs` concerns builds and release packaging; the importer copies only the file the player picks into the app's own `rom` folder at runtime, and nothing is ever bundled.
- Note: the Windows `Idas3Unity.dll` was rebuilt with Phase 3 (2026-10-24) and now exports `Idas3SceneSaveNow`.

Test (device, in the Phase 2 test environment where timing matters): import ROM from Download via the picker; race, press Home mid-race and return (pause menu shown); force-close and relaunch (selection/progress kept); import a music track; check options rows.

Results (2026-10-24, Red Magic 9 Pro, fresh install, test environment on):
- ROM import: SELECT ROM FILE → picker → `gds-0033.chd` copied to `files/rom` → "verified CHD" → title. Pass.
- Home mid-race (Akina Time Attack) and return: same process, PAUSED menu over the race; `settings.txt`/`native_selection.txt` written at the Home press, only under `userdata-unity-test`. Pass.
- Force-close and relaunch: driver kept in slot 01 (AE86, play time, date). Pass.
- Music: Sound Room (hold VIEW on Course select) → + ADD MUSIC → picker (audio filter; non-audio greyed) → WAV copied to `Custom Music`, imported, "Music added.", listed with duration. Cancelling the picker returns cleanly. Pass.
- Options: desktop-only rows show N/A ON ANDROID and refuse changes; Updates tab shows the APK note with the button disabled; footer "saved on this device". Pass.
- Flag off: no TEST ENVIRONMENT tag; real saves in `userdata-unity-scene` (fresh, 0/5), test driver stayed in `userdata-unity-test`. Real-save relaunch persistence not separately exercised (same code path as the test folder).

Follow-ups found in testing (fixed on Android only; Windows text unchanged):
- ROM screen message "Place gds-0033.chd in the rom folder." → Android says to tap SELECT ROM FILE (mapped in `Idas3RomGate`; the shared validator is untouched).
- Pause summary DISPLAY row no longer shows the desktop window mode ("BORDERLESS").
- Steering & Wheel STRENGTH was a live slider: its callback wrote `wheelFeedbackStrength` past `Adjust`'s platform guard. It is now an N/A row on Android.
- Online tab and ADD MUSIC entry say "device" instead of "computer"/"PC"; the Custom Music README written on Android does too.
- Not a bug: the settings × first returns focus to the category list (`Back()`), then closes on a second press; same on Windows.
- Display & Graphics tab: DISPLAY MODE and VERTICAL SYNC are N/A on Android (always full screen; both VSync branches pace by `targetFrameRate`), and the "Turn off VSync" hint is desktop-only. RESOLUTION stays: it lowers the render resolution and is what the BALANCED/LOW presets use.
- FRAME LIMIT = UNLIMITED ran at 31 FPS on Android: `Idas3FramePacing` set `targetFrameRate=-1`, which mobile players treat as 30. Mobile now uses the display refresh rate (measured 61 FPS on the attract screen).

## Phase 4b — touch controls options (built)
**Why separate:** Phase 4 is written and only waiting for its device test; adding scope would delay that test and the commit. The touch overlay is independent of Phase 4's files and of the Phase 3 render path, so 4b can start once Phase 4 is committed.

Scope:
- Layout editing: move, resize and set opacity per control (race and menu layouts separately), plus reset to default. Today the layout is fixed in `Idas3TouchControls.Layout()` (`Idas3TouchControls.cs:117-154`), scaled from screen height and the safe area, and nothing is persisted.
- Steering sensitivity (drag travel, currently `steeringTravel`) and optional tilt steering, deferred here from Phase 4.

Constraints:
- Store the layout as screen-relative values in the Unity options (`Idas3GameOptions`), never in native saves, so save formats stay unchanged. Re-clamp to the safe area when the screen size or safe area changes.
- Keep the race HUD (tach, record panel) and chase-camera car clear, as the default layout does (Phase 1); the editor refuses or warns on placements that cover them.
- The edit mode must work with the overlay owning all touches while visible, and stay hidden when a controller is connected, as now.
- Device testing: `IdasDevice.ps1` computes named taps from the default layout. Either reset to the default layout in the test environment or have the helpers read the saved layout; update `SKILL.md` accordingly.

Test (device): edit and save a race layout, relaunch and confirm it is kept; rotate/resize and confirm controls stay inside the safe area; reset restores the default; scripted taps still hit their controls. Repeat on a second phone with a different aspect ratio once one is available (Phase 5 hardware).

As built:
- Steering & Wheel tab on Android: the wheel force feedback rows are replaced by TOUCH STEERING (DRAG / TILT PHONE), FULL LOCK (drag 6–30% of screen height, default 15; tilt 10–60°, default 30) and EDIT TOUCH LAYOUT.
- `Idas3TouchControls` editor (pause menu hidden meanwhile): RACE / MENU layouts, drag to move, SIZE ± (50–200%), FADE ± (20–100%), RESET, RESET ALL, CANCEL, SAVE. Yellow boxes mark the default record panel, tachometer and car; warnings for covering them and for overlapping controls. Saves through `Idas3GameOptions.ApplyTouchLayout` (own transaction, like the HUD editor).
- Overlapping controls: a touch goes to the nearest centre (was first in list order, which let a moved A swallow VIEW).
- Tilt: roll in the screen plane, `atan2(g.x, -g.y)`, light smoothing; the drag zone is off and the track shows TILT TO STEER. Unity reports `Input.acceleration` in the current screen's axes (measured: level landscape g≈(0,-0.85,-0.52); clockwise → +x; tilting the screen back only trades y for z). The first version assumed portrait device axes and read level as a 90° turn (near full right lock).
- `IdasDevice.ps1` `Get-IdasPoint`/`Invoke-IdasSteer` read the saved layout and full lock from the active save folder (edge clamping not mirrored).

Results (2026-10-24, test environment): new rows and defaults shown; drag moved A; SIZE+ ×2 / FADE− saved as 120% / 80%; layout kept after relaunch; scripted `Invoke-IdasTap A` hit the moved button; overlap and HUD-cover warnings shown; RESET ALL + SAVE restored defaults; TILT PHONE / 35° applied and the race shows TILT TO STEER. Tilt direction and feel confirmed by hand after the axis fix (adb cannot drive the accelerometer). Only LandscapeLeft was measured; the screen-axis formula should hold for LandscapeRight.

## Phase 5 — performance for low/mid devices (not started)
- Device tiers (auto-detected, user override): Low = 30 FPS, ~60% render scale, no MSAA, mirror off; Mid = 60 FPS target, ~75% scale, MSAA 2×, mirror at reduced rate; High = native, MSAA 4×.
- Render scale via scaled render target with UI at native resolution; dynamic resolution from frame time to absorb thermal throttling. Android ignores vSync count: drive `Application.targetFrameRate`. On the 120 Hz test phone, UNLIMITED (display refresh rate) still gives ~60 FPS on a light screen: likely Unity reports the window's 60 Hz mode (not yet checked), so higher rates probably need a preferred display mode / frame-rate request.
- Reduce draw cost (the measured bottleneck) without changing source order: the renderer keeps "exact source list ordering, then exact submission ordering" and coplanar surfaces resolve by submission order (`Idas3SceneRenderer.cs:287`, `:646`), so merging or regrouping ranges is out. Allowed levers: cheaper per-draw state changes, a reduced-rate or optional mirror view, render scale.
- Memory: no texture transcoding (see Phase 3). Use course unloading, optional mip limits as a setting, and on-demand audio banks. On GPUs without BC, Phase 3's decoded RGBA32 course textures are 4–8× larger than the packs (GUNSAI's 4096² atlas ≈ 85 MB); a mip limit there should skip the top decoded levels.
- MSAA: imported foliage relies on MSAA to resolve derivative-scaled coverage (`Idas3SceneCommon.cginc`, "instead of binary leaf flicker"). Turning MSAA off on Low reintroduces leaf flicker; treat it as a visible trade-off, not a free saving.
- Profile on at least one low and one mid phone (ideally Mali), using the Phase 2 test environment for long runs. Pass bar: Low holds 30 FPS (1% low ≥ 25) for 10 minutes without thermal collapse; Mid holds 60 on Akina/Akagi.

## Phase 6 — distribution (not started)
- First-launch data installer: download data/course zips from GitHub Releases into app storage, SHA-256 verified, resumable, free-space check (~9 GB during install).
- APK updates through the Android package installer; data patches applied in-app.
- `Tools/Build-ReleasePackage.py` / `Build-UpdatePatch.py` `--platform android`; move `StreamingAssets/SoundRoom` (282 MB) out of the APK; signing keystore from environment.
- Docs: BUILDING.md (Android toolchain), README (player install).

## Phase 7 — online (not started)
- Determinism gate: identical per-frame state hashes on x64 and arm64 for a scripted input run.
- Then EOS Android SDK, online battles, leaderboard submission with a platform-specific build string.

## Testing tools
- Project skill `.claude/skills/android-device-test` (`IdasDevice.ps1`): install, push data/ROM, named overlay taps/holds/steering computed from screen size, screenshots, Unity logs. Records game-flow gotchas (Game Mode/Transmission use left/right; hidden menu countdown auto-picks Legend; Time Attack timer; single injectable finger). Phase 2 adds `Set-IdasTestEnvironment`.

## Housekeeping
- Pushed to `origin/android-port` (2026-10-24): Phases 1–2 (`f9303aaa`, `84c7d10c`), plan `3867b03f`, Phase 4 `8650edae`, display/frame-limit follow-up `da6c601f`, Phase 4b `3af0b0d2`, tilt fix `2a861fd3`. Uncommitted: `CLAUDE.md`, `.claude/skills/release/`.
- `stash@{0}` (first Phase 3 attempt) exists only in the original working copy, also as local branch `android-phase3-stash-backup`; neither is on the remote. Its logger is now in the tree; the rest is superseded.
- ~1,800 modified texture `.meta` files from the Android target switch: never commit.
- Phone state (2026-10-24): app reinstalled fresh; base data + all 9 course packs pushed; ROM imported via the picker; test environment flag **on**; "stay awake on USB" enabled (`adb shell svc power stayon false` to undo). `userdata-unity-test` holds test driver "A" in slot 01 and TOUCH STEERING = TILT PHONE (35°): set it back to DRAG before scripted steering. `userdata-unity-scene` is a fresh real save (drag). `Custom Music` holds `idas3-test-tone.wav`.
