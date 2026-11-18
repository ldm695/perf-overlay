# PerfOverlay release plan

## Product scope

Provide a small, read-only Windows overlay for foreground games and graphics applications. The overlay can be hidden without stopping collection. It does not call NVIDIA APIs, change driver settings, inject into games, or attempt to reproduce NVIDIA-specific latency or frame-generation processing.

## Current implementation

- WPF transparent overlay with notification-area settings, monitor and position controls, horizontal/vertical layouts, and automatic/white/black text color. The pixel settings window has separate shortcut and overlay-data subpages; its main page is shorter and expands vertically on subpages.
- Independent global shortcuts control the settings window (`Ctrl+Alt+F4` by default) and overlay visibility (`Ctrl+Shift+F4` by default). Each can be reassigned in settings.
- Overlay-data toggles control the visibility of FPS, base FPS, 1% Low, GPU, CPU, memory, and latency without stopping their collection. FPS definitions and detected GPU model names are shown on that subpage.
- PerMonitorV2 DPI awareness is set by an early module initializer before WPF creates windows. The app manifest contains no duplicate DPI declaration, and Release builds are warning-free.
- PresentMon v2 captures system-wide frame events; the app filters samples to the current foreground process.
- FPS and 1% Low use displayed cadence where PresentMon exposes it, with Present interval fallback. Base FPS uses application simulation timing when provided.
- 1% Low is the arithmetic mean of the lowest 1% per-frame FPS in a rolling 30-second window; fewer than 100 valid samples produce N/A.
- PC latency is the recent one-second average of `MsPCLatency`. It is distinct from render latency and remains N/A when unavailable.
- CPU, GPU, and physical memory estimates come from Windows system counters and APIs.
- Capture recovery cleans up orphaned PerfOverlay-owned PresentMon processes/ETW sessions on startup, then watches collector health. It limits automatic collector restarts to three per five minutes with a 30-second cooldown and avoids interpreting a foreground process that has never produced frame records as a capture failure.
- Known Intel `DCA_PMON` and NVIDIA `FrameViewService` session failures are detected from ETW session inventory. The app requests confirmation before restarting the associated service and then uses Windows UAC; it does not call NVIDIA APIs.

## Release checklist

- [x] Keep only production capture and metrics code in the app; remove unused ETW prototypes, probes, and debug-only frame exports.
- [x] Keep the solution buildable, including the frame-statistics test project. The solution was compiled; the test suite was not run.
- [x] Publish a self-contained `win-x64` package with PresentMon, a user README, and all license notices.
- [x] Embed the project ICO in Release; verify the executable's extracted 32x32 image matches the current icon source.
- [x] Verify the package layout and refresh the release archive after the final source edits. The folder has six files; the archive contains the same six entries and no PDB.
- [x] Preserve historical investigation material locally under `artifacts\diagnostics`; exclude machine-specific captures from source control and the user-facing release.

## Known limitations

- Capture depends on Windows ETW permissions and the health of the system PresentMon session.
- GPU usage is a Windows busiest-engine estimate and may differ from vendor telemetry.
- Base FPS, PC latency, and displayed cadence depend on data the target application and graphics stack expose.
- 1% Low is a live estimate, not a benchmark summary; it can differ from other tools because their sample classification and windows may differ.
- A standard desktop overlay may not appear in every exclusive-fullscreen or anti-cheat protected application.
