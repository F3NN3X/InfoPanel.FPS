# InfoPanel.FPS: native PresentMon API refactor

Status: **plan, no code yet** (written 2026-10-14). Target version: **2.0.0**.

Decision: drop the `PresentMonFps` NuGet package and integrate Intel's PresentMon API 2
(`PresentMonAPI2.dll` talking to `PresentMonService.exe`) directly. The plugin bundles both binaries
from one PresentMon release and runs its own private service instance, so nothing has to be installed.

---

## 1. Why

### 1.1 Why the plugin stalled

At the time, PresentMon capture failed on "modern" games with anti-cheat. The root cause is now known
and fixed upstream:

- **PresentMon issue #573** (filed 27 Jan 2026): once EA AntiCheat is running, `StartTraceW()` (creating
  a new ETW session) fails with `ERROR_ACCESS_DENIED`. `EnableTraceEx2()` on a session that *already
  exists* keeps working. The example was Battlefield 6 with capture started after the game.
  https://github.com/GameTechDev/PresentMon/issues/573
- **The fix** (PR #588, shipped in v2.5.0 / v2.5.1): the service creates its ETW session at startup
  with no providers and only enables the providers when capture starts. The release note says this
  lets PresentMon "gather data from certain titles that otherwise block ETW trace gathering".
  https://github.com/GameTechDev/PresentMon/releases/tag/v2.5.0
- **Why anti-cheat refuses** is not documented. The likely reading is that the anti-cheat blocks the
  creation of new ETW sessions while it runs; that is an inference.

### 1.2 Why the NuGet package can't get us there

`PresentMonFps` (https://github.com/lemutec/PresentMonFps, MIT, owner emako):

- **Small reimplementation.** It does not wrap Intel's code: it opens a TraceEvent session on the
  `Microsoft-Windows-DxgKrnl` provider and counts Present events for one PID.
- **FPS only.** Its result type has `Fps` and `IsCanceled`. FPS is `(count-1)/elapsed` over a
  50-sample ring buffer. There is no frame time, no lows, no GPU/CPU busy and no latency.
- **Displayed and presented frames are not told apart**, so FPS is probably wrong with frame
  generation (DLSS FG, XeFG, AFMF).
- **It creates its ETW session when capture starts** (`TraceEventSession("PresentMon-FpsInspector")`).
  That is exactly the `StartTraceW` pattern EA AntiCheat blocks, so the #573 failure can still happen.
- **Low maintenance.** Version 2.0.6 came out 2026-04-16 after a gap from 2.0.5 (2024-08-13). The
  project is one maintainer with 4 stars.

Everything the plugin computes today on top of it (frame time, 1% low) is derived locally from that
FPS stream, not measured.

### 1.3 What PresentMon 2.x offers

| Version | What it brought |
|---|---|
| 2.2.0 | Click-to-photon latency and lower event latency. |
| 2.3.0 | XeFG / AFMF frame-generation tracking, the `FrameType` metric and XeLL latency. |
| 2.3.1 | The client runs without admin. The service installs `PresentMonAPI2.dll`. App / Presents / Display FPS. |
| 2.4.x | Headless CLI capture, ETL playback, and beta simulation-start and PC-latency metrics. |
| 2.5.0 / 2.5.1 | IPC rewrite, multi-adapter queries, a CPU "System" device, and the **anti-cheat ETW fix**. 2.5.0 was withdrawn (a shared-service conflict downstream); 2.5.1 fixed the API's backward compatibility. |
| 2.6.0 (21 Sep 2026) | About 78% less service CPU, `dump-caps` (metric availability with reasons), D3D12 PSO-compile metrics, newer flip events, and optional CPU temperature via UCI socwatch. |

Release dates come from https://github.com/GameTechDev/PresentMon/releases. The page omits years on
most entries.

**Anti-cheat status:**
- I found no reports of bans or flags for ETW-based capture: PresentMon only *reads* kernel and
  runtime events and injects nothing.
- Nothing documented either way for Easy Anti-Cheat, BattlEye, Vanguard, Ricochet or FACEIT. Treat
  them as **untested**.
- One EA forum thread asks EA to whitelist PresentMon; it was unreadable (HTTP 403).

---

## 2. Architecture

### 2.1 The PresentMon components

| Component | Role | Used here |
|---|---|---|
| `PresentMon.exe` / `presentmon-cli` | ETW console capture to CSV or stdout | No |
| `PresentMonService.exe` | ETW frame analysis plus vendor telemetry (NVAPI, ADL, IGCL, WMI); serves clients over a named pipe and shared memory | **Yes, bundled, private instance** |
| `PresentMonAPI2.dll` | Client API, a C ABI declared in `PresentMonAPI.h` | **Yes, bundled, P/Invoke** |
| `PresentMonAPI2Loader.dll` | Finds the installed `PresentMonAPI2.dll` through the registry | No; we load our own copy |

Source: https://github.com/GameTechDev/PresentMon/blob/main/README-Service.md

### 2.2 Private service instance (no install)

`PresentMonService.exe` accepts options that let a client run its own instance next to (or instead of)
the installed shared service. From `IntelPresentMon/PresentMonService/CliOptions.h`:

| Option | Default | Use here |
|---|---|---|
| `--control-pipe <name>` | `""` (the service's normal pipe) | A private pipe, e.g. `\\.\pipe\infopanel-fps-pm-<pid>` |
| `--control-pipe-allow-au-clients` | off: only the creator (CO) can connect | Leave off; only the plugin connects |
| `--etw-session-name <name>` | `PMService` | `InfoPanelFPS-PM`, so it never collides with an installed service |
| `--shm-name-prefix <prefix>` | `Global\pm_svc_shm` | `Global\infopanel_fps_pm` |
| `--frame-ring-samples <n>` | 1000 | Default |
| `--telemetry-ring-samples <n>` | 1500 | Default |
| `--log-dir <dir>`, `--log-level <lvl>`, `--enable-stdio-log` | off, `error` | Log into the plugin folder at `warn` |
| `--timed-stop <ms>` | -1 | Unused (it's a debugging aid) |

The client side connects with `pmOpenSessionWithPipe(&handle, "<pipe name>")` instead of
`pmOpenSession`.

**Why this works with anti-cheat.** InfoPanel starts at login, before any game, and the plugin
starts its service instance in `Initialize()`. So the ETW session already exists when the game and
its anti-cheat start, which is the same order the upstream #573 fix relies on.

**Privileges.** Creating ETW sessions needs admin or *Performance Log Users* membership. InfoPanel
runs elevated through the `InfoPanel` scheduled task (RunLevel Highest), and the child process
inherits that token.

### 2.3 Bundling and licence

- PresentMon is MIT-licensed (`LICENSE.txt`, "Copyright (C) 2017-2024 Intel Corporation").
  Redistribution is allowed with the licence text. Ship it as `ThirdParty/PresentMon/LICENSE.txt` in
  the plugin zip.
- Intel says: "If a client ships their own copy of PresentMonAPI2.dll, binary compatibility with the
  service will not be guaranteed." That warning is about mixing a bundled DLL with an *installed*
  service. We bundle the service **and** the DLL from the same release and talk only to our own
  instance, so they always match.
- **Pin one version** (start with 2.6.0) and upgrade both binaries together in one commit.

### 2.4 Getting the binaries

Preferred: take them from the official installer instead of building C++.

1. Download the pinned `PresentMon-<ver>-x64.msi` (or the current asset name) from the GitHub
   release.
2. Do an administrative extract with no install: `msiexec /a <msi> /qn TARGETDIR=<dir>`.
3. Copy `PresentMonService.exe`, `PresentMonAPI2.dll`, the service's runtime dependencies (check the
   extract for which DLLs the service needs) and `LICENSE.txt` into
   `InfoPanel.FPS/ThirdParty/PresentMon/`.
4. Record the version and the SHA-256 of the MSI in `ThirdParty/PresentMon/VERSION.txt`.

How to wire it in:
- **CI:** the release workflow does steps 1–3 with a pinned URL and checks the SHA-256, so no
  binaries are committed. Local builds use a script (`scripts/fetch-presentmon.ps1`) that does the
  same and caches the result.
- **Fallback:** build `PresentMonService` and `PresentMonAPI2` from source (VS 2022, x64 Release) if
  the MSI layout changes or a needed DLL isn't in it.

---

## 3. Target plugin design

Keep what works and replace the data source.

| Today | After |
|---|---|
| `WindowDetectionService` (fullscreen window, PID) | **Kept.** Optionally also track the foreground window (`GetForegroundWindow`) for borderless games. |
| `SystemInformationService` (GPU name, resolution, refresh) | **Kept**, for resolution and refresh rate. The GPU name can come from the PresentMon introspection instead. |
| `SensorManagementService` | **Extended** with the new sensors (section 4). |
| `PerformanceMonitoringService` (PresentMonFps, local 1% low) | **Replaced** by the three components below. |
| — | **New** `PresentMonHost`: starts, watches and stops the private `PresentMonService.exe`. |
| — | **New** `PresentMonClient`: P/Invoke layer, session, introspection and queries. |
| — | **New** `PresentMonMetricsService`: polls queries for the tracked PID and maps results to `PerformanceMetrics`. |
| `PresentMonFps` NuGet, `nuget.config`, local `.nupkg` | **Removed.** |

### 3.1 `PresentMonHost` (process lifecycle)

- **Start:** in `Initialize()`, start `ThirdParty/PresentMon/PresentMonService.exe` with the private
  `--control-pipe`, `--etw-session-name` and `--shm-name-prefix`. Use `CreateNoWindow` and send
  stdout/stderr to the plugin log.
- **Tie it to InfoPanel's lifetime:** put the child in a Windows **Job Object** with
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so it dies if InfoPanel crashes.
- **Ready check:** retry `pmOpenSessionWithPipe` with backoff (for example 10 × 200 ms) until it
  succeeds.
- **Restart:** on exit or `PM_STATUS_SESSION_NOT_OPEN`, reopen the session. If that fails, restart
  the process with backoff (2 s, 15 s, 60 s, 5 min, then give up and show status "PresentMon
  unavailable"). This mirrors the installed service's own recovery policy.
- **Stale sessions:** if a previous instance died and left an ETW session named `InfoPanelFPS-PM`,
  stop it first (`ControlTrace` with `EVENT_TRACE_CONTROL_STOP`, or `logman stop InfoPanelFPS-PM
  -ets`).
- **Stop:** in `Close()`, call `pmCloseSession`, then signal the process to exit (CTRL_BREAK or job
  close) and wait up to 2 s.
- **Shared service option:** if config `UseSharedService=true` and `PresentMonSharedService` is
  running, skip the private instance and connect with `pmOpenSession`.

### 3.2 `PresentMonClient` (P/Invoke)

- **Loading:** load `PresentMonAPI2.dll` explicitly from the plugin folder with
  `NativeLibrary.Load(path)`. Never rely on the DLL search path, because InfoPanel's own folder or an
  installed copy could win.
- **Bindings:** declare only the exports we use. The current header has:

  ```c
  PM_STATUS pmOpenSessionWithPipe(PM_SESSION_HANDLE* pHandle, const char* controlPipeName);
  PM_STATUS pmCloseSession(PM_SESSION_HANDLE handle);
  PM_STATUS pmStartTrackingProcess(PM_SESSION_HANDLE handle, uint32_t process_id);
  PM_STATUS pmStopTrackingProcess(PM_SESSION_HANDLE handle, uint32_t process_id);
  PM_STATUS pmGetIntrospectionRoot(PM_SESSION_HANDLE handle, const PM_INTROSPECTION_ROOT** ppRoot);
  PM_STATUS pmFreeIntrospectionRoot(const PM_INTROSPECTION_ROOT* pRoot);
  PM_STATUS pmSetTelemetryPollingPeriod(PM_SESSION_HANDLE handle, uint32_t reserved, uint32_t timeMs);
  PM_STATUS pmSetEtwFlushPeriod(PM_SESSION_HANDLE handle, uint32_t periodMs);
  PM_STATUS pmRegisterDynamicQuery(PM_SESSION_HANDLE s, PM_DYNAMIC_QUERY_HANDLE* pHandle,
                                   PM_QUERY_ELEMENT* pElements, uint64_t numElements,
                                   double windowSizeMs, double metricOffsetMs);
  PM_STATUS pmPollDynamicQuery(PM_DYNAMIC_QUERY_HANDLE handle, uint32_t processId,
                               uint8_t* pBlob, uint32_t* numSwapChains);
  PM_STATUS pmFreeDynamicQuery(PM_DYNAMIC_QUERY_HANDLE handle);
  PM_STATUS pmPollStaticQuery(PM_SESSION_HANDLE s, const PM_QUERY_ELEMENT* pElement,
                              uint32_t processId, uint8_t* pBlob);
  PM_STATUS pmGetApiVersion(PM_VERSION* pVersion);
  ```

- **Version check:** at startup, compare `pmGetApiVersion` against the pinned version and refuse to
  run on a mismatch.
- **Introspection is the source of truth.** Walk `PM_INTROSPECTION_ROOT` once per session to find:
  - which metrics exist, for which device (the frame / "universal" device, each GPU adapter, the
    System/CPU device), and which statistics each supports;
  - the data type of each metric (double, uint64, bool, enum) and the enum strings (present mode,
    frame type, graphics runtime);
  - the device names (the GPU name for the `gpu-name` sensor).

  Build queries only from what introspection reports as available, so the plugin adapts to the GPU
  vendor, driver and PresentMon version.
- **Blob layout:**
  - `pmRegisterDynamicQuery` fills in each `PM_QUERY_ELEMENT`'s `dataOffset` and `dataSize`.
  - Read the poll blob through those offsets (`BinaryPrimitives` or `MemoryMarshal` on a pinned
    `byte[]`).
  - The blob holds one slot per swap chain. Pick the swap chain with the highest presented FPS, or
    the one that matches the game window.
- **Mirror the C structs by hand** (`PM_QUERY_ELEMENT`, the introspection structs, enums) in
  `Native/PresentMonApi.cs`.
  - Check each `[StructLayout(LayoutKind.Sequential)]` against the header's field order and packing.
  - Add a unit test that asserts `Marshal.SizeOf` for each struct against the sizes from a tiny C
    program compiled against the same header.

### 3.3 `PresentMonMetricsService` (queries)

**When a game is detected:** call `pmStartTrackingProcess(pid)`. Call `pmStopTrackingProcess` when it
closes or changes.

**One dynamic query** (window 1000 ms, offset ≈ 50 ms; tune later) for the per-second sensors:
- `DISPLAYED_FPS` (AVG), `PRESENTED_FPS` (AVG), `APPLICATION_FPS` (AVG).
- Frame time: `CPU_FRAME_TIME` (AVG, PERCENTILE_99, PERCENTILE_01) and `DISPLAYED_FRAME_TIME`.
- `GPU_TIME`, `GPU_BUSY`, `GPU_WAIT`, `CPU_BUSY`, `CPU_WAIT` (AVG).
- `DROPPED_FRAMES` (AVG → percent), `ANIMATION_ERROR` (AVG).
- `DISPLAY_LATENCY`, `GPU_LATENCY`, `CLICK_TO_PHOTON_LATENCY`, `ALL_INPUT_TO_PHOTON_LATENCY`,
  `PC_LATENCY`, `INSTRUMENTED_LATENCY` (AVG, NON_ZERO_AVG where it applies).
- `PSO_COMPILE_COUNT`, `PSO_COMPILE_TIME`.
- GPU telemetry for the GPU the game renders on: power, temperature, frequency, utilization, memory
  used / size, fan speed, and the throttle flags.
- CPU telemetry: utilization, power, temperature and frequency.

**The 1% and 0.1% lows:**
- 1% low FPS = 1000 / (99th-percentile frame time).
- 0.1% low: if the API has no 99.9th percentile (none was seen in `PM_STAT`), use a **frame query**
  (`pmRegisterFrameQuery` / `pmConsumeFrames`) to read raw frame times into a rolling buffer and
  compute it locally.
- Decide during implementation whether to compute all lows from the frame stream for consistency.

**Static query** (on tracking start): present mode, graphics runtime (API), sync interval, allows
tearing, and frame type.

**Polling:** `pmPollDynamicQuery` every `UpdateInterval` (1 s, configurable). Call
`pmSetTelemetryPollingPeriod` (for example 250 ms) and `pmSetEtwFlushPeriod` (for example 100 ms)
once after opening the session.

### 3.4 Plugin lifecycle (current `BasePlugin` API)

| Method | Does |
|---|---|
| `Initialize()` | Read config, start `PresentMonHost`, open the session, introspect, register queries. |
| `Load(containers)` | Register the sensors (section 4). Only add sensors whose metric introspection reports, or always add them and show `0` / `-`. Decide by testing InfoPanel's behaviour. |
| `UpdateAsync(ct)` | Window detection → track / untrack the PID → poll → update the sensors. |
| `Close()` | Untrack, free queries, close the session, stop the host. |
| `UpdateInterval` | 1 s by default (from config). |

---

## 4. Sensors

**Compatibility is a hard requirement.** The dashboard's FpsMonitor widget reads
`plugins.fps-plugin.fps.<sensor id>`:
- `src/widgets/FpsMonitor.tsx`, `src/widgets/fps-settings.ts` in InfoPanel.Dashboard;
- the plugin id is `fps-plugin`, and the container `FPS` has the id `fps`.

Keep the plugin id, the container and every existing sensor id. New ids use the names the dashboard
already looks for where one exists.

| Sensor id | Name | Unit | Source | Status |
|---|---|---|---|---|
| `fps` | Frames Per Second | FPS | `DISPLAYED_FPS` AVG (fall back to `PRESENTED_FPS`) | existing, now measured |
| `1% low fps` | 1% Low FPS | FPS | 1000 / `CPU_FRAME_TIME` P99 (or the frame stream) | existing, now measured |
| `current frame time` | Current Frame Time | ms | `DISPLAYED_FRAME_TIME` AVG | existing, now measured |
| `windowtitle` | Currently Capturing | text | window detection | existing |
| `resolution` | Resolution | text | system info | existing |
| `refreshrate` | Refresh Rate | Hz | system info | existing |
| `gpu-name` | GPU | text | introspection GPU device name (fallback WMI) | existing |
| `0.1% low` | 0.1% Low FPS | FPS | frame stream | new; the dashboard reads it |
| `avg-fps`, `min-fps`, `max-fps` | Avg / Min / Max FPS | FPS | `DISPLAYED_FPS` AVG / MIN / MAX over the window | new; the dashboard reads them |
| `presented fps` | Presented FPS | FPS | `PRESENTED_FPS` AVG | new |
| `application fps` | Application FPS | FPS | `APPLICATION_FPS` AVG | new |
| `frame time p99` | Frame Time 99th pct | ms | `CPU_FRAME_TIME` P99 | new |
| `gpu busy` | GPU Busy | ms | `GPU_BUSY` AVG | new; the dashboard reads it |
| `gpu wait` | GPU Wait | ms | `GPU_WAIT` AVG | new |
| `gpu time` | GPU Time | ms | `GPU_TIME` AVG | new |
| `cpu busy` | CPU Busy | ms | `CPU_BUSY` AVG | new; the dashboard reads it |
| `cpu wait` | CPU Wait | ms | `CPU_WAIT` AVG | new |
| `dropped frames` | Dropped Frames | % | `DROPPED_FRAMES` AVG × 100 | new |
| `animation error` | Animation Error | ms | `ANIMATION_ERROR` AVG | new |
| `display latency` | Display Latency | ms | `DISPLAY_LATENCY` AVG | new; the dashboard reads it |
| `gpu latency` | GPU Latency | ms | `GPU_LATENCY` AVG | new |
| `click to photon` | Click-to-Photon Latency | ms | `CLICK_TO_PHOTON_LATENCY` NON_ZERO_AVG | new |
| `input to photon` | Input-to-Photon Latency | ms | `ALL_INPUT_TO_PHOTON_LATENCY` NON_ZERO_AVG | new |
| `pc latency` | PC Latency | ms | `PC_LATENCY` AVG (Reflex titles) | new |
| `graphics-api` | Graphics API | text | static `PRESENT_RUNTIME` | new; the dashboard reads it |
| `present mode` | Present Mode | text | static `PRESENT_MODE` | new |
| `display-mode` | Display Mode | text | derived from present mode (Fullscreen / Borderless / Windowed) | new; the dashboard reads it |
| `frame generation` | Frame Generation | text | `FRAME_TYPE` (none / DLSS-FG / XeFG / AFMF …) | new |
| `sync interval` | Sync Interval | — | static `SYNC_INTERVAL` | new |
| `tearing` | Allows Tearing | text | static `ALLOWS_TEARING` | new |
| `pso compiles` | Shader (PSO) Compiles | count/s | `PSO_COMPILE_COUNT` | new |
| `gpu utilization` | GPU Utilization | % | GPU telemetry | new; the dashboard reads it |
| `gpu power` | GPU Power | W | GPU telemetry | new |
| `gpu temperature` | GPU Temperature | °C | GPU telemetry | new |
| `gpu clock` | GPU Clock | MHz | GPU telemetry | new |
| `gpu memory used` | GPU Memory Used | MB | GPU telemetry | new |
| `gpu fan` | GPU Fan | RPM | GPU telemetry | new |
| `gpu throttling` | GPU Throttling | text | throttle-limit flags (power / temp / current / voltage / utilization) | new |
| `cpu utilization` | CPU Utilization | % | CPU telemetry | new |
| `cpu power` | CPU Power | W | CPU telemetry | new |
| `cpu temperature` | CPU Temperature | °C | CPU telemetry (needs socwatch in 2.6, often unavailable) | new |
| `cpu clock` | CPU Clock | MHz | CPU telemetry | new |
| `status` | PresentMon Status | text | host state: Running / Starting / Unavailable: reason | new |

Notes:
- **Check enum names.** The `PM_METRIC_*` and `PM_STAT_*` names above come from the research. Confirm
  each against `PresentMonAPI.h` for the pinned version.
- **Old aliases.** The dashboard also accepts `1% low` and `frame time` as alternative names. Don't
  add duplicates; the existing ids cover them.
- **Dashboard follow-up.** Once the new sensors exist, the dashboard's FpsMonitor can show more (frame
  generation, dropped frames, latency). Track that in InfoPanel.Dashboard
  `docs/redesign/widget-backlog.md`.

---

## 5. Configuration (`InfoPanel.FPS.dll.ini`)

```ini
[PresentMon]
; Use an installed PresentMon shared service instead of the bundled private instance
UseSharedService=false
UpdateIntervalMs=1000
QueryWindowMs=1000
TelemetryPollMs=250
EtwFlushMs=100
LogLevel=warn

[Detection]
; fullscreen = current behaviour; foreground = any focused window with frames
Mode=fullscreen
; Executables never tracked (launchers, overlays)
Ignore=explorer.exe,Discord.exe,steamwebhelper.exe
```

---

## 6. Repo changes

- **Remove:**
  - the `PresentMonFps` PackageReference;
  - `nuget.config` and `PresentMonFps.2.0.6.nupkg` (local branch `presentmon-nuget-update`; drop that
    branch);
  - the TraceEvent-only code paths.
- **Add:**
  - `InfoPanel.FPS/Native/PresentMonApi.cs` (P/Invoke and structs);
  - `InfoPanel.FPS/Services/PresentMonHost.cs`, `PresentMonClient.cs`, `PresentMonMetricsService.cs`;
  - `InfoPanel.FPS/ThirdParty/PresentMon/` (gitignored except `VERSION.txt` and `LICENSE.txt`);
  - `scripts/fetch-presentmon.ps1`.
- **csproj:** copy `ThirdParty/PresentMon/**` into the output folder. The release zip must contain
  them at `InfoPanel.FPS/ThirdParty/PresentMon/`.
- **Release workflow** (`.github/workflows/release.yml`): add a step before the build that runs
  `scripts/fetch-presentmon.ps1` with the pinned version and SHA-256.
- **Tests:** add a small `InfoPanel.FPS.Tests` project (xUnit):
  - struct sizes and offsets;
  - blob-offset reads against a hand-built buffer;
  - percentile and low-FPS maths for the frame-stream path;
  - sensor id stability (a snapshot of the id list).
- **Docs:** update the README (requirements: InfoPanel elevated; bundled PresentMon version; the
  sensor list), CHANGELOG 2.0.0, and `PluginInfo.ini` description and version.

---

## 7. Phases

1. **Spike (go / no-go):**
   - Extract 2.6.0 from the MSI and run `PresentMonService.exe --control-pipe \\.\pipe\pm-spike
     --etw-session-name PM-Spike --shm-name-prefix Global\pm_spike` from an elevated shell.
   - From a throwaway C# console: `pmOpenSessionWithPipe`, `pmGetApiVersion`, introspection dump,
     `pmStartTrackingProcess(<running game pid>)`, and one dynamic query printing displayed FPS, GPU
     busy and GPU power for 30 s.
   - **Go** if it runs outside the service control manager and returns data.
   - **No-go fallback:** require the installed shared service (`winget install Intel.PresentMon`) and
     use `pmOpenSession`. Everything after the host stays the same.
2. **Native layer:** `PresentMonApi.cs`, `PresentMonClient` and introspection, plus the struct tests.
3. **Host:** `PresentMonHost` with the Job Object, readiness, restart and stale-session cleanup.
4. **Metrics:** queries, the swap-chain choice, lows, mapping into `PerformanceMetrics`, and the
   sensors.
5. **Packaging:** the fetch script, csproj copy, release workflow and licence.
6. **Validation** (section 8), then release 2.0.0.

---

## 8. Validation

- **Desktop:** a windowed D3D11 app, a borderless D3D12 game, an exclusive-fullscreen game, a Vulkan
  game and an OpenGL title. Check the FPS sensors against the PresentMon app overlay or CapFrameX on
  the same run (±1 FPS).
- **Frame generation:** a DLSS-FG or AFMF title. `fps` (displayed) should be about 2× `application
  fps`, and `frame generation` should name the technique.
- **Anti-cheat, the main open question.** For each title record: capture works yes/no, any
  anti-cheat warning, and whether the game was started before or after InfoPanel.

  | Anti-cheat | Example titles |
  |---|---|
  | EA AntiCheat | Battlefield 6 (the #573 case) |
  | Easy Anti-Cheat | Fortnite, Apex Legends |
  | BattlEye | PUBG, Rainbow Six Siege |
  | Vanguard | Valorant |
  | Ricochet | Call of Duty |
  | FACEIT | CS2 on FACEIT |

  The private ETW session name differs from `PMService`. If an anti-cheat allow-lists by session
  name, compare against the installed shared service before concluding.
- **Resilience:**
  - kill `PresentMonService.exe` while a game runs (the plugin recovers);
  - kill InfoPanel (the child dies through the Job Object);
  - restart InfoPanel (a stale ETW session is cleaned up);
  - run with the shared service installed and running (no collision).
- **Overhead:** service CPU and memory while idle and while tracking (2.6.0 claims about 78% less
  CPU). Plugin `UpdateAsync` time under 5 ms.
- **Compatibility:** the dashboard FpsMonitor widget still shows FPS, 1% low, frame time, title,
  resolution, refresh rate and GPU name without changes.

---

## 9. Risks and open questions

| Risk | Mitigation |
|---|---|
| `PresentMonService.exe` may not run outside the service control manager (unverified) | Phase 1 spike; fallback to the shared service |
| The C ABI or struct layout changes between PresentMon versions (2.5.0 was withdrawn) | Pin one version, check the version at startup, test struct sizes, upgrade deliberately |
| No official C# binding; offset bugs in the blob reads | Read only through the `dataOffset` / `dataSize` returned by registration; unit tests |
| Anti-cheat titles untested (EAC, BattlEye, Vanguard, Ricochet, FACEIT) | Validation matrix; document results in the README |
| Private ETW session name differs from the shared service's | Test both; make the session name configurable if needed |
| Telemetry availability varies by vendor and driver (CPU temperature needs socwatch) | Driven by introspection; unavailable metrics stay `-` or are not registered |
| InfoPanel not elevated, so ETW session creation fails | Detect it, set `status` to "Requires elevated InfoPanel", fall back to the shared service if present |
| A larger zip (service plus deps, a few MB) | Acceptable; note it in the README |
| Per-metric privilege needs are undocumented | Test whether any metric needs more than the service already has |

---

## 10. References

- PresentMon repo: https://github.com/GameTechDev/PresentMon
- Releases: https://github.com/GameTechDev/PresentMon/releases (v2.5.0 note:
  https://github.com/GameTechDev/PresentMon/releases/tag/v2.5.0)
- Service and SDK: https://github.com/GameTechDev/PresentMon/blob/main/README-Service.md
- API header: https://github.com/GameTechDev/PresentMon/blob/main/IntelPresentMon/PresentMonAPI2/PresentMonAPI.h
- Service CLI options: https://github.com/GameTechDev/PresentMon/blob/main/IntelPresentMon/PresentMonService/CliOptions.h
- Anti-cheat issue: https://github.com/GameTechDev/PresentMon/issues/573
- 2.6.0 coverage: https://videocardz.com/newz/intel-presentmon-2-6-cuts-cpu-use-by-78-adds-game-experience-overlay
- PresentMonFps: https://github.com/lemutec/PresentMonFps, https://www.nuget.org/packages/PresentMonFps
- Dashboard consumer: InfoPanel.Dashboard `src/widgets/FpsMonitor.tsx`, `src/widgets/fps-settings.ts`
