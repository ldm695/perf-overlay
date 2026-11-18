using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Management;
using PerfOverlay.Metrics;
using PerfOverlay.Interop;

namespace PerfOverlay;

public sealed record MonitorOption(string DeviceName, string DisplayName);

public partial class ConfigurationWindow : Window
{
    public event Action<OverlaySettings>? SettingsApplied;
    public event Action? ExitRequested;

    public ConfigurationWindow(OverlaySettings settings, IReadOnlyList<GpuAdapterMetrics>? gpus = null)
    {
        InitializeComponent();
        var monitors = System.Windows.Forms.Screen.AllScreens
            .Select((screen, index) => new MonitorOption(
                screen.DeviceName,
                $"{GetFriendlyMonitorName(screen.DeviceName)}  DISPLAY {index + 1}  {screen.Bounds.Width}X{screen.Bounds.Height}{(screen.Primary ? "  PRIMARY" : "")}"))
            .ToArray();
        MonitorSelector.ItemsSource = monitors;
        MonitorSelector.SelectedItem = monitors.FirstOrDefault(x => x.DeviceName == settings.MonitorDeviceName)
            ?? monitors.FirstOrDefault(x => System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == x.DeviceName)?.Primary == true)
            ?? monitors.FirstOrDefault();
        OffsetXBox.Text = settings.OffsetX.ToString();
        OffsetYBox.Text = settings.OffsetY.ToString();
        foreach (var button in PositionPresetGrid.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
            button.IsChecked = string.Equals(button.Tag as string, settings.PositionPreset, StringComparison.Ordinal);
        LayoutSelector.SelectedIndex = settings.Vertical ? 1 : 0;
        TextColorSelector.SelectedItem = TextColorSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, settings.TextColorMode.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? TextColorSelector.Items[0];
        ConfigurationHotKeyBox.Text = settings.ConfigurationHotKey;
        OverlayHotKeyBox.Text = settings.OverlayHotKey;
        ShowFpsToggle.IsChecked = settings.ShowFps;
        ShowBaseFpsToggle.IsChecked = settings.ShowBaseFps;
        ShowLowToggle.IsChecked = settings.ShowOnePercentLow;
        ShowGpuToggle.IsChecked = settings.ShowGpu;
        ShowCpuToggle.IsChecked = settings.ShowCpu;
        ShowMemoryToggle.IsChecked = settings.ShowMemory;
        ShowLatencyToggle.IsChecked = settings.ShowLatency;
        UpdateGpuAdapters(gpus);
    }

    public void UpdateGpuAdapters(IReadOnlyList<GpuAdapterMetrics>? gpus)
    {
        var ordered = (gpus ?? Array.Empty<GpuAdapterMetrics>()).OrderBy(gpu => gpu.Kind).ToList();
        if (ordered.All(gpu => gpu.Kind != GpuAdapterKind.Integrated))
            ordered.Insert(0, new GpuAdapterMetrics(string.Empty, GpuAdapterKind.Integrated, null, null));

        GpuInfoPanel.Children.Clear();
        foreach (var gpu in ordered)
        {
            var label = gpu.Kind switch
            {
                GpuAdapterKind.Integrated => "iGPU",
                GpuAdapterKind.External => "eGPU",
                _ => "dGPU"
            };
            GpuInfoPanel.Children.Add(new TextBlock
            {
                Text = $"{label}: {(string.IsNullOrWhiteSpace(gpu.Name) ? "N/A" : gpu.Name)}",
                Margin = new Thickness(0, 0, 0, 2)
            });
        }
    }

    public void ShowHotKeyError(string message)
    {
        ConfigurationHotKeyHint.Text = message.ToUpperInvariant();
        OverlayHotKeyHint.Text = message.ToUpperInvariant();
        HotkeysPage.Visibility = Visibility.Visible;
        MainPage.Visibility = Visibility.Collapsed;
    }

    private void CaptureHotKey(System.Windows.Input.KeyEventArgs e, System.Windows.Controls.TextBox box, TextBlock hint)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!GlobalHotKey.TryFormatShortcut(Keyboard.Modifiers, key, out var shortcut))
        {
            hint.Text = "ADD CTRL, ALT, SHIFT OR WIN";
            return;
        }

        box.Text = shortcut;
        hint.Text = "PRESS APPLY TO SAVE";
    }

    private void ConfigurationHotKeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        CaptureHotKey(e, ConfigurationHotKeyBox, ConfigurationHotKeyHint);

    private void OverlayHotKeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        CaptureHotKey(e, OverlayHotKeyBox, OverlayHotKeyHint);

    private void OpenHotkeysPage_Click(object sender, RoutedEventArgs e) => ShowPage(HotkeysPage);
    private void OpenDataPage_Click(object sender, RoutedEventArgs e) => ShowPage(DataPage);
    private void BackToMainPage_Click(object sender, RoutedEventArgs e) => ShowPage(MainPage);

    private void ShowPage(UIElement page)
    {
        MainPage.Visibility = ReferenceEquals(page, MainPage) ? Visibility.Visible : Visibility.Collapsed;
        HotkeysPage.Visibility = ReferenceEquals(page, HotkeysPage) ? Visibility.Visible : Visibility.Collapsed;
        DataPage.Visibility = ReferenceEquals(page, DataPage) ? Visibility.Visible : Visibility.Collapsed;

        var targetHeight = ReferenceEquals(page, MainPage) ? 680 : 840;
        if (Math.Abs(Height - targetHeight) < 0.1)
            return;

        var centerY = Top + (ActualHeight > 0 ? ActualHeight : Height) / 2;
        Height = targetHeight;
        Top = centerY - targetHeight / 2;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorSelector.SelectedItem is not MonitorOption monitor ||
            !int.TryParse(OffsetXBox.Text, out var x) || !int.TryParse(OffsetYBox.Text, out var y))
        {
            System.Windows.MessageBox.Show(this, "Select a display and enter whole-number X/Y coordinates.", "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var textColorMode = TextColorSelector.SelectedItem is ComboBoxItem colorItem &&
            Enum.TryParse<OverlayTextColorMode>(colorItem.Tag as string, true, out var parsedColorMode)
                ? parsedColorMode
                : OverlayTextColorMode.Auto;

        SettingsApplied?.Invoke(new OverlaySettings
        {
            MonitorDeviceName = monitor.DeviceName,
            OffsetX = x,
            OffsetY = y,
            PositionPreset = PositionPresetGrid.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>()
                .FirstOrDefault(button => button.IsChecked == true)?.Tag as string ?? "TopCenter",
            Vertical = LayoutSelector.SelectedIndex == 1,
            TextColorMode = textColorMode,
            ConfigurationHotKey = ConfigurationHotKeyBox.Text,
            OverlayHotKey = OverlayHotKeyBox.Text,
            ShowFps = ShowFpsToggle.IsChecked == true,
            ShowBaseFps = ShowBaseFpsToggle.IsChecked == true,
            ShowOnePercentLow = ShowLowToggle.IsChecked == true,
            ShowGpu = ShowGpuToggle.IsChecked == true,
            ShowCpu = ShowCpuToggle.IsChecked == true,
            ShowMemory = ShowMemoryToggle.IsChecked == true,
            ShowLatency = ShowLatencyToggle.IsChecked == true
        });
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = new ConfirmationWindow("CONFIRM EXIT", "EXIT PerfOverlay?") { Owner = this };
        if (confirmation.ShowDialog() == true)
            ExitRequested?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void PositionPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string preset })
        {
            foreach (var button in PositionPresetGrid.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
                button.IsChecked = ReferenceEquals(button, sender);
            OffsetXBox.Text = "0";
            OffsetYBox.Text = preset.StartsWith("Middle", StringComparison.Ordinal) ? "0" : "24";
        }
    }

    private static string GetFriendlyMonitorName(string deviceName)
    {
        for (uint i = 0; i < 32; i++)
        {
            var adapter = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0) || !string.Equals(adapter.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                continue;

            var monitor = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0))
                return adapter.DeviceString;

            var hardwareId = monitor.DeviceId?.Split('\\').Skip(1).FirstOrDefault() ?? "";
            var friendlyName = GetWmiFriendlyName(hardwareId);
            if (!string.IsNullOrWhiteSpace(friendlyName))
                return $"{friendlyName} ({hardwareId})";

            return string.IsNullOrWhiteSpace(monitor.DeviceString) ? adapter.DeviceString : monitor.DeviceString;
        }
        return deviceName;
    }

    private static string? GetWmiFriendlyName(string hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return null;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var instanceName = item["InstanceName"]?.ToString();
                    if (instanceName?.Contains(hardwareId, StringComparison.OrdinalIgnoreCase) != true) continue;
                    if (item["UserFriendlyName"] is not ushort[] chars) continue;
                    var name = new string(chars.Where(value => value != 0).Select(value => (char)value).ToArray()).Trim();
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }
        catch { }
        return null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
