using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PerfOverlay.Capture;
using PerfOverlay.Interop;
using PerfOverlay.Metrics;

namespace PerfOverlay;

public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private readonly FrameStatistics _statistics = new();
    private readonly PresentMonCaptureService _capture = new();
    private readonly CaptureRecoveryModule _captureRecovery;
    private readonly HardwareMetricsService _hardware = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private HardwareMetrics _hardwareSnapshot = new(null, null, Array.Empty<GpuAdapterMetrics>());
    private int? _targetPid;
    private int? _ignoredOverlayPid;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayContextMenu;
    private System.Drawing.Icon? _trayIconImage;
    private ConfigurationWindow? _configuration;
    private GlobalHotKey? _configurationHotKey;
    private GlobalHotKey? _overlayHotKey;
    private OverlaySettings _settings = OverlaySettings.Load();
    private bool? _autoTextUsesWhite;
    private bool _repairPromptOpen;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            if (IsLoaded)
                ApplyPosition();
        };
        _captureRecovery = new CaptureRecoveryModule(_capture);
        _capture.FrameDataReceived += sample => _statistics.AddFrame(sample);
        _capture.CaptureFailed += ShowTrayError;
        _captureRecovery.StartupCleanupOccurred += ShowTrayError;
        _captureRecovery.AutomaticRecoveryOccurred += message => Dispatcher.BeginInvoke(new Action(() =>
        {
            _statistics.Reset();
            ShowTrayError(message);
        }));
        _captureRecovery.RecoveryExhausted += ShowTrayError;
        _captureRecovery.ExternalIssueDetected += issue => Dispatcher.BeginInvoke(new Action(() => PromptExternalCaptureRepair(issue)));
        _hardware.MetricsUpdated += metrics => Dispatcher.BeginInvoke(new Action(() =>
        {
            _hardwareSnapshot = metrics;
            _configuration?.UpdateGpuAdapters(metrics.Gpus);
        }));
        Loaded += OnLoaded;
        Closed += OnClosed;
        _timer.Tick += OnTick;
        _trayIconImage = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
            ?? System.Drawing.SystemIcons.Application;
        _trayContextMenu = new System.Windows.Forms.ContextMenuStrip();
        _trayContextMenu.Items.Add("Open Settings", null, (_, _) => Dispatcher.BeginInvoke(new Action(OpenConfiguration)));
        _trayContextMenu.Items.Add("Exit", null, (_, _) => Close());
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "PerfOverlay",
            Icon = _trayIconImage,
            ContextMenuStrip = _trayContextMenu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == System.Windows.Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(OpenConfiguration));
        };
    }

    private void OpenConfiguration()
    {
        if (_configuration is null)
        {
            _configuration = new ConfigurationWindow(_settings, _hardwareSnapshot.Gpus);
            _configuration.SettingsApplied += settings =>
            {
                if (!TryApplyHotKeys(settings.ConfigurationHotKey, settings.OverlayHotKey, out var hotKeyError))
                {
                    _configuration.ShowHotKeyError("HOTKEY REGISTRATION FAILED");
                    ShowTrayError(hotKeyError);
                    return;
                }

                _settings = settings;
                _settings.Save();
                ApplyLayout();
                ApplyMetricVisibility();
                UpdateLayout();
                ApplyPosition();
                ApplyTextColor();
            };
            _configuration.ExitRequested += Close;
            _configuration.Closed += (_, _) => _configuration = null;
        }
        _configuration.Show();
        _configuration.Activate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() | WsExTransparent | WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        try
        {
            _configurationHotKey = new GlobalHotKey(hwnd, _settings.ConfigurationHotKey, 0x2F10);
            _configurationHotKey.ToggleRequested += ConfigurationHotKeyRequested;
        }
        catch (System.ComponentModel.Win32Exception ex) { ShowTrayError(ex.Message); }
        catch (InvalidOperationException ex) { ShowTrayError(ex.Message); }
        catch (ArgumentException ex) { ShowTrayError(ex.Message); }
        try
        {
            _overlayHotKey = new GlobalHotKey(hwnd, _settings.OverlayHotKey, 0x2F11);
            _overlayHotKey.ToggleRequested += OverlayHotKeyRequested;
        }
        catch (System.ComponentModel.Win32Exception ex) { ShowTrayError(ex.Message); }
        catch (InvalidOperationException ex) { ShowTrayError(ex.Message); }
        catch (ArgumentException ex) { ShowTrayError(ex.Message); }
        ApplyLayout();
        ApplyMetricVisibility();
        UpdateLayout();
        ApplyPosition();
        ApplyTextColor();
        _captureRecovery.Start();
        _hardware.Start();
        try { _capture.Start(); }
        catch (Exception ex) { ShowTrayError(ex.Message); }
        _timer.Start();
    }

    private bool TryApplyHotKeys(string configurationShortcut, string overlayShortcut, out string errorMessage)
    {
        try
        {
            if (_configurationHotKey is null)
            {
                _configurationHotKey = new GlobalHotKey(new WindowInteropHelper(this).Handle, configurationShortcut, 0x2F10);
                _configurationHotKey.ToggleRequested += ConfigurationHotKeyRequested;
            }
            else if (!_configurationHotKey.TrySetShortcut(configurationShortcut, out errorMessage))
                return false;

            if (_overlayHotKey is null)
            {
                _overlayHotKey = new GlobalHotKey(new WindowInteropHelper(this).Handle, overlayShortcut, 0x2F11);
                _overlayHotKey.ToggleRequested += OverlayHotKeyRequested;
            }
            else if (!_overlayHotKey.TrySetShortcut(overlayShortcut, out errorMessage))
            {
                _configurationHotKey.TrySetShortcut(_settings.ConfigurationHotKey, out _);
                return false;
            }

            errorMessage = "";
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        {
            _configurationHotKey?.TrySetShortcut(_settings.ConfigurationHotKey, out _);
            _overlayHotKey?.TrySetShortcut(_settings.OverlayHotKey, out _);
            errorMessage = ex.Message;
            return false;
        }
    }

    private void ConfigurationHotKeyRequested(object? sender, EventArgs e)
    {
        if (_configuration is { IsVisible: true })
            _configuration.Hide();
        else
            OpenConfiguration();
    }

    private void OverlayHotKeyRequested(object? sender, EventArgs e) => ToggleOverlay();

    private void ShowTrayError(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
            _trayIcon?.ShowBalloonTip(5000, "PerfOverlay", message, System.Windows.Forms.ToolTipIcon.Error)));
    }

    private void PromptExternalCaptureRepair(CaptureExternalIssue issue)
    {
        if (_repairPromptOpen)
            return;

        _repairPromptOpen = true;
        var message = issue.Kind switch
        {
            CaptureIssueKind.IntelDcaPmon =>
                $"FPS capture is stalled. A known Intel DCA_PMON ETW session is active.\n\n{issue.Reason}\n\nRestart Intel telemetry now? This requires administrator permission.",
            _ =>
                $"FPS capture is stalled. A stale NVIDIA FrameView ETW session is active.\n\n{issue.Reason}\n\nRestart FrameView now? This requires administrator permission."
        };
        var dialog = new ConfirmationWindow("CAPTURE RECOVERY", message)
        {
            Width = 520,
            Height = 270,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var confirmed = dialog.ShowDialog() == true;
        if (!confirmed)
        {
            _captureRecovery.ResolveExternalIssue(issue, false, "The user declined the external service repair.");
            _repairPromptOpen = false;
            return;
        }

        _ = RepairExternalCaptureAsync(issue);
    }

    private async Task RepairExternalCaptureAsync(CaptureExternalIssue issue)
    {
        try
        {
            var result = await _captureRecovery.RestartExternalServiceAsync(issue);
            if (result == 0)
            {
                _captureRecovery.ResolveExternalIssue(issue, true, $"Restarted service {issue.ServiceName}.");
                ShowTrayError($"Restarted {issue.ServiceName}; FPS capture will reconnect.");
            }
            else if (result == 1223)
            {
                _captureRecovery.ResolveExternalIssue(issue, false, "The administrator permission request was cancelled.");
                ShowTrayError("Administrator permission was cancelled. PerfOverlay will retry its own capture session.");
            }
            else
            {
                _captureRecovery.ResolveExternalIssue(issue, false, $"Service repair returned Windows error {result}.");
                ShowTrayError($"Could not restart {issue.ServiceName} (Windows error {result}). PerfOverlay will retry its own capture session.");
            }
        }
        finally
        {
            _repairPromptOpen = false;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var hwnd = GetForegroundWindow();
        GetWindowThreadProcessId(hwnd, out var foregroundPid);
        var pid = (int)foregroundPid;
        if (pid > 0 && pid != Environment.ProcessId && IsNvidiaOverlayProcess(pid))
        {
            _ignoredOverlayPid = pid;
            if (_targetPid is int targetPid && !IsCaptureTargetAlive(targetPid))
                StopTargetCapture();
        }
        else if (pid <= 0 || pid == Environment.ProcessId)
        {
            _ignoredOverlayPid = null;
            if (_targetPid is not null) StopTargetCapture();
        }
        else if (_targetPid != pid)
        {
            _ignoredOverlayPid = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.HasExited && process.MainWindowHandle != IntPtr.Zero) StartTargetCapture(pid);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        else
        {
            _ignoredOverlayPid = null;
        }

        SetMetrics(_statistics.GetSnapshot(), _hardwareSnapshot);
        if (_settings.TextColorMode == OverlayTextColorMode.Auto)
            UpdateAutomaticTextColor();
    }

    private void StartTargetCapture(int pid)
    {
        _statistics.Reset();
        _targetPid = pid;
        _capture.SetTargetProcessId(pid);
    }

    private void StopTargetCapture()
    {
        _capture.SetTargetProcessId(null);
        _targetPid = null;
        _statistics.Reset();
    }

    private static bool IsNvidiaOverlayProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return string.Equals(process.ProcessName, "NVIDIA Overlay", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsCaptureTargetAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetMetrics(FrameMetrics metrics, HardwareMetrics hardware)
    {
        FpsText.Text = metrics.CurrentFps is double fps ? fps.ToString("0") : "N/A";
        BaseFpsText.Text = metrics.BaseFps is double baseFps ? baseFps.ToString("0") : "N/A";
        BaseFpsText.Visibility = !_settings.ShowBaseFps || metrics.BaseFps is null ? Visibility.Collapsed : Visibility.Visible;
        LowText.Text = metrics.OnePercentLowFps is double low ? low.ToString("0") : "N/A";
        CpuText.Text = hardware.CpuUtilizationPercent is double cpu ? $"{cpu:0}%" : "N/A";
        MemoryText.Text = hardware.MemoryUtilizationPercent is double memory ? $"{memory:0}%" : "N/A";
        LatencyText.Text = metrics.AverageLatencyMs is double latency ? $"{latency:0.0} MS" : "N/A";
        VerticalFpsText.Text = FpsText.Text;
        VerticalLowInlineText.Text = LowText.Text;
        VerticalBaseFpsInlineText.Text = BaseFpsText.Text;
        VerticalBaseFpsInlineText.Visibility = BaseFpsText.Visibility;
        UpdateGpuRows(hardware.Gpus);
        VerticalCpuText.Text = CpuText.Text;
        VerticalMemoryText.Text = MemoryText.Text;
        VerticalLatencyText.Text = LatencyText.Text;
        VerticalBaseFpsInlineText.Visibility = BaseFpsText.Visibility;
        ApplyMetricVisibility();

        if (IsLoaded && IsVisible)
        {
            UpdateLayout();
            ApplyPosition();
        }
    }

    private void UpdateGpuRows(IReadOnlyList<GpuAdapterMetrics> gpus)
    {
        GpuMetricsPanel.Children.Clear();
        VerticalGpuMetricsPanel.Children.Clear();
        var ordered = (gpus ?? Array.Empty<GpuAdapterMetrics>()).OrderBy(gpu => gpu.Kind).ToList();
        if (ordered.All(gpu => gpu.Kind != GpuAdapterKind.Integrated))
            ordered.Insert(0, new GpuAdapterMetrics(string.Empty, GpuAdapterKind.Integrated, null, null));

        for (var i = 0; i < ordered.Count; i++)
        {
            var gpu = ordered[i];
            var type = gpu.Kind switch { GpuAdapterKind.Integrated => "iGPU", GpuAdapterKind.External => "eGPU", _ => "dGPU" };
            var utilization = gpu.UtilizationPercent is double usage ? $"{usage:0}%" : "N/A";
            var vram = gpu.VramPercent is double memory ? $"{memory:0}%" : "N/A";
            if (i > 0)
                GpuMetricsPanel.Children.Add(CreateGpuTextBlock(",", false, new Thickness(2, 0, 2, 0)));
            GpuMetricsPanel.Children.Add(CreateGpuMetricRow(type, utilization, vram));
            VerticalGpuMetricsPanel.Children.Add(CreateGpuMetricRow(type, utilization, vram));
        }
    }

    private StackPanel CreateGpuMetricRow(string label, string utilization, string memory)
    {
        var labelsOnRight = _settings.Vertical && IsRightAlignedPreset();
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = labelsOnRight
                ? System.Windows.HorizontalAlignment.Right
                : System.Windows.HorizontalAlignment.Stretch
        };
        var labelBlock = CreateGpuTextBlock(label, false, new Thickness(0, 0, 8, 0));
        var utilizationBlock = CreateGpuTextBlock(utilization, true, new Thickness(0, 0, 8, 0));
        var memoryBlock = CreateGpuTextBlock(memory, true);
        labelBlock.Margin = labelsOnRight
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(0, 0, 8, 0);
        if (labelsOnRight)
        {
            row.Children.Add(utilizationBlock);
            row.Children.Add(memoryBlock);
            row.Children.Add(labelBlock);
        }
        else
        {
            row.Children.Add(labelBlock);
            row.Children.Add(utilizationBlock);
            row.Children.Add(memoryBlock);
        }
        return row;
    }

    private TextBlock CreateGpuTextBlock(string text, bool bold, Thickness? margin = null)
    {
        var color = _settings.TextColorMode switch
        {
            OverlayTextColorMode.White => Colors.White,
            OverlayTextColorMode.Black => Colors.Black,
            _ => _autoTextUsesWhite == true ? Colors.White : Colors.Black
        };
        return new TextBlock
        {
            Text = text,
            FontSize = 16,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = new SolidColorBrush(color),
            Margin = margin ?? new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void ApplyLayout()
    {
        HorizontalLayout.Visibility = _settings.Vertical ? Visibility.Collapsed : Visibility.Visible;
        VerticalLayout.Visibility = _settings.Vertical ? Visibility.Visible : Visibility.Collapsed;
        var labelsOnRight = _settings.Vertical && IsRightAlignedPreset();
        VerticalLayout.HorizontalAlignment = labelsOnRight
            ? System.Windows.HorizontalAlignment.Right
            : System.Windows.HorizontalAlignment.Center;
        VerticalFpsRow.HorizontalAlignment = labelsOnRight
            ? System.Windows.HorizontalAlignment.Right
            : System.Windows.HorizontalAlignment.Stretch;
        VerticalCpuRow.HorizontalAlignment = VerticalFpsRow.HorizontalAlignment;
        VerticalMemoryRow.HorizontalAlignment = VerticalFpsRow.HorizontalAlignment;
        VerticalLatencyRow.HorizontalAlignment = VerticalFpsRow.HorizontalAlignment;
        SetLabelPosition(VerticalFpsRow, VerticalFpsLabel, labelsOnRight);
        SetLabelPosition(VerticalCpuRow, VerticalCpuLabel, labelsOnRight);
        SetLabelPosition(VerticalMemoryRow, VerticalMemoryLabel, labelsOnRight);
        SetLabelPosition(VerticalLatencyRow, VerticalLatencyLabel, labelsOnRight);
    }

    private void ApplyMetricVisibility()
    {
        var showAnyFps = _settings.ShowFps || _settings.ShowBaseFps || _settings.ShowOnePercentLow;
        FpsGroup.Visibility = showAnyFps ? Visibility.Visible : Visibility.Collapsed;
        FpsText.Visibility = _settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        BaseFpsText.Visibility = _settings.ShowBaseFps && BaseFpsText.Text != "N/A" ? Visibility.Visible : Visibility.Collapsed;
        LowText.Visibility = _settings.ShowOnePercentLow ? Visibility.Visible : Visibility.Collapsed;
        VerticalFpsRow.Visibility = showAnyFps ? Visibility.Visible : Visibility.Collapsed;
        VerticalFpsLabel.Visibility = _settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        VerticalFpsText.Visibility = _settings.ShowFps ? Visibility.Visible : Visibility.Collapsed;
        VerticalBaseFpsInlineText.Visibility = _settings.ShowBaseFps && BaseFpsText.Text != "N/A" ? Visibility.Visible : Visibility.Collapsed;
        VerticalLowInlineText.Visibility = _settings.ShowOnePercentLow ? Visibility.Visible : Visibility.Collapsed;

        GpuMetricsPanel.Visibility = _settings.ShowGpu ? Visibility.Visible : Visibility.Collapsed;
        VerticalGpuMetricsPanel.Visibility = _settings.ShowGpu ? Visibility.Visible : Visibility.Collapsed;
        HorizontalCpuGroup.Visibility = _settings.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
        VerticalCpuRow.Visibility = _settings.ShowCpu ? Visibility.Visible : Visibility.Collapsed;
        HorizontalMemoryGroup.Visibility = _settings.ShowMemory ? Visibility.Visible : Visibility.Collapsed;
        VerticalMemoryRow.Visibility = _settings.ShowMemory ? Visibility.Visible : Visibility.Collapsed;
        HorizontalLatencyGroup.Visibility = _settings.ShowLatency ? Visibility.Visible : Visibility.Collapsed;
        VerticalLatencyRow.Visibility = _settings.ShowLatency ? Visibility.Visible : Visibility.Collapsed;

        var groups = new[] { showAnyFps, _settings.ShowGpu, _settings.ShowCpu, _settings.ShowMemory, _settings.ShowLatency };
        var separators = new[] { HorizontalFpsSeparator, HorizontalGpuSeparator, HorizontalCpuSeparator, HorizontalMemorySeparator };
        for (var i = 0; i < separators.Length; i++)
            separators[i].Visibility = groups[i] && groups.Skip(i + 1).Any(visible => visible)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private bool IsRightAlignedPreset() => _settings.PositionPreset.EndsWith("Right", StringComparison.Ordinal);

    private static void SetLabelPosition(StackPanel row, TextBlock label, bool onRight)
    {
        label.Margin = onRight
            ? new Thickness(8, 0, 0, 0)
            : new Thickness(0, 0, 8, 0);
        row.Children.Remove(label);
        if (onRight)
            row.Children.Add(label);
        else
            row.Children.Insert(0, label);
    }

    private void ApplyTextColor()
    {
        if (_settings.TextColorMode == OverlayTextColorMode.White)
        {
            SetOverlayTextColor(Colors.White);
            return;
        }

        if (_settings.TextColorMode == OverlayTextColorMode.Black)
        {
            SetOverlayTextColor(Colors.Black);
            return;
        }

        UpdateAutomaticTextColor();
    }

    private void UpdateAutomaticTextColor()
    {
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
            return;

        var luminances = new List<double>(20);
        try
        {
            var width = ActualWidth;
            var height = ActualHeight;
            for (var i = 1; i <= 7; i++)
            {
                var x = width * i / 8;
                AddBackgroundLuminance(dc, x, 2, luminances);
                AddBackgroundLuminance(dc, x, height - 2, luminances);
            }

            for (var i = 1; i <= 3; i++)
            {
                var y = height * i / 4;
                AddBackgroundLuminance(dc, 2, y, luminances);
                AddBackgroundLuminance(dc, width - 2, y, luminances);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        if (luminances.Count == 0)
            return;

        luminances.Sort();
        var middle = luminances.Count / 2;
        var medianLuminance = luminances.Count % 2 == 0
            ? (luminances[middle - 1] + luminances[middle]) / 2
            : luminances[middle];

        var useWhite = _autoTextUsesWhite switch
        {
            true => medianLuminance < 0.55,
            false => medianLuminance < 0.45,
            null => medianLuminance < 0.5
        };

        if (_autoTextUsesWhite != useWhite)
        {
            _autoTextUsesWhite = useWhite;
            SetOverlayTextColor(useWhite ? Colors.White : Colors.Black);
        }
    }

    private void AddBackgroundLuminance(IntPtr dc, double x, double y, ICollection<double> luminances)
    {
        var point = PointToScreen(new System.Windows.Point(x, y));
        var colorRef = GetPixel(dc, (int)Math.Round(point.X), (int)Math.Round(point.Y));
        if (colorRef == 0xFFFFFFFF)
            return;

        var red = colorRef & 0xFF;
        var green = (colorRef >> 8) & 0xFF;
        var blue = (colorRef >> 16) & 0xFF;
        luminances.Add((0.2126 * red + 0.7152 * green + 0.0722 * blue) / 255.0);
    }

    private void SetOverlayTextColor(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        ApplyTextColorToVisualTree(MetricsPanel, brush);
    }

    private static void ApplyTextColorToVisualTree(DependencyObject parent, System.Windows.Media.Brush brush)
    {
        if (parent is TextBlock textBlock)
            textBlock.Foreground = brush;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            ApplyTextColorToVisualTree(VisualTreeHelper.GetChild(parent, i), brush);
    }

    private void ToggleOverlay()
    {
        if (Visibility == Visibility.Visible)
            Hide();
        else
        {
            Show();
            Topmost = true;
        }
    }

    private void ApplyPosition()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(x => x.DeviceName == _settings.MonitorDeviceName)
            ?? screens.FirstOrDefault(x => x.Primary)
            ?? screens.FirstOrDefault();
        if (screen is null) return;

        var bounds = screen.Bounds;
        var scale = GetMonitorScale(bounds);
        var hwnd = new WindowInteropHelper(this).Handle;
        var overlayWidth = ActualWidth * scale;
        var overlayHeight = ActualHeight * scale;
        if (GetWindowRect(hwnd, out var currentRect) && currentRect.Right > currentRect.Left && currentRect.Bottom > currentRect.Top)
        {
            overlayWidth = currentRect.Right - currentRect.Left;
            overlayHeight = currentRect.Bottom - currentRect.Top;
        }
        var preset = _settings.PositionPreset;
        var x = preset.EndsWith("Left", StringComparison.Ordinal)
            ? bounds.Left + _settings.OffsetX
            : preset.EndsWith("Right", StringComparison.Ordinal)
                ? bounds.Right - overlayWidth - _settings.OffsetX
                : bounds.Left + (bounds.Width - overlayWidth) / 2 + _settings.OffsetX;
        var y = preset.StartsWith("Bottom", StringComparison.Ordinal)
            ? bounds.Bottom - overlayHeight - _settings.OffsetY
            : preset.StartsWith("Middle", StringComparison.Ordinal)
                ? bounds.Top + (bounds.Height - overlayHeight) / 2 + _settings.OffsetY
                : bounds.Top + _settings.OffsetY;
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static double GetMonitorScale(System.Drawing.Rectangle bounds)
    {
        try
        {
            var monitor = MonitorFromPoint(new NativePoint(bounds.Left + Math.Max(1, bounds.Width / 2), bounds.Top + Math.Max(1, bounds.Height / 2)), 2);
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96d;
        }
        catch { }
        return 1d;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        _captureRecovery.Dispose();
        StopTargetCapture();
        _capture.Stop();
        _capture.Dispose();
        _hardware.Dispose();
        _configurationHotKey?.Dispose();
        _overlayHotKey?.Dispose();
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); }
        _trayContextMenu?.Dispose();
        _trayIconImage?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; public NativePoint(int x, int y) { X = x; Y = y; } }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
