# PerfOverlay

PerfOverlay is a Windows performance overlay that follows the foreground application. It runs as a transparent, click-through overlay and does not use NVIDIA APIs.

## Run

- Windows 10 or 11, x64.
- To run from source, install the .NET 10 SDK and run `dotnet run --project src\PerfOverlay\PerfOverlay.csproj`.
- The self-contained release package is `artifacts\PerfOverlay-win-x64.zip`.

The overlay starts with Windows foreground monitoring active. Left-click the notification-area icon to open settings. Right-click it for **Open Settings** or **Exit**; the tray-menu Exit closes immediately. The settings window's Exit App button asks once before closing. Use `Ctrl+Alt+F4` to open or hide settings and `Ctrl+Shift+F4` to show or hide the overlay; both shortcuts can be changed independently. Hiding the settings window or overlay does not stop monitoring.

Settings include monitor, position, layout, text color, and separate shortcut controls. The **Overlay Data** subpage can show or hide FPS, base FPS, 1% Low, GPU, CPU, memory, and latency independently; hidden metrics continue to be collected. This page also explains the FPS values and lists detected GPU models. The pixel-style settings window uses a shorter main page and expands vertically on its subpages.

## Displayed metrics

- **FPS:** recent displayed-frame cadence from PresentMon `MsBetweenDisplayChange`, falling back to `MsBetweenPresents` when unavailable.
- **Base FPS:** recent application simulation cadence from PresentMon `MsBetweenSimulationStart`; unavailable games show no base-FPS value.
- **1% Low:** arithmetic mean FPS of the slowest 1% of valid frame samples in a 30-second rolling window. It remains N/A until at least 100 samples are available.
- **GPU:** per-adapter utilization and VRAM usage estimates. An iGPU row is always shown; other detected adapters are listed as available.
- **CPU:** total system CPU utilization.
- **RAM:** system physical memory in use.
- **LAT:** average PresentMon `MsPCLatency` over the recent one-second window. This is PC latency, not render latency; unsupported or unavailable data shows N/A.

PresentMon is bundled in `tools\PresentMon.exe`, with its license and third-party notices in the `licenses` folder. If frame capture cannot start, check that the Windows account has the required Performance Log Users permission. Capture failures and automatic recovery are logged at `%LOCALAPPDATA%\PerfOverlay\capture.log` and reported through a notification-area balloon. The app cleans up only its own orphaned collectors and ETW sessions. For known Intel `DCA_PMON` or NVIDIA FrameView session failures, it asks before requesting administrator permission to restart the related service.

## Build and publish

```powershell
dotnet restore PerfOverlay.sln
dotnet build PerfOverlay.sln --configuration Release
dotnet publish src\PerfOverlay\PerfOverlay.csproj --configuration Release --runtime win-x64 --self-contained true --output artifacts\publish\PerfOverlay-win-x64
```

The app sets `PerMonitorV2` DPI awareness in a module initializer before WPF creates windows. The manifest contains only the execution-level declaration, avoiding duplicate DPI configuration. A clean Release build currently reports no warnings.
