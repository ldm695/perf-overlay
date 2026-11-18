using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace PerfOverlay;

public sealed record OverlaySettings
{
    public string MonitorDeviceName { get; init; } = "";
    public int OffsetX { get; init; } = 0;
    public int OffsetY { get; init; } = 24;
    public string PositionPreset { get; init; } = "TopCenter";
    public bool Vertical { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter<OverlayTextColorMode>))]
    public OverlayTextColorMode TextColorMode { get; init; } = OverlayTextColorMode.Auto;
    public string ConfigurationHotKey { get; init; } = "CTRL+ALT+F4";
    public string OverlayHotKey { get; init; } = "CTRL+SHIFT+F4";
    public bool ShowFps { get; init; } = true;
    public bool ShowBaseFps { get; init; } = true;
    public bool ShowOnePercentLow { get; init; } = true;
    public bool ShowGpu { get; init; } = true;
    public bool ShowCpu { get; init; } = true;
    public bool ShowMemory { get; init; } = true;
    public bool ShowLatency { get; init; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PerfOverlay", "settings.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public enum OverlayTextColorMode
{
    Auto,
    White,
    Black
}
