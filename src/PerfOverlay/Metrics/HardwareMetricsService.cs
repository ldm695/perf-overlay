using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PerfOverlay.Metrics;

public sealed class HardwareMetricsService : IDisposable
{
    private static readonly Regex EngineInstancePattern = new(
        @"^pid_\d+_(?<adapter>luid_0x[0-9a-f]+_0x[0-9a-f]+_phys_\d+)_(?<engine>eng_\d+)_engtype_.+$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly CancellationTokenSource _cancellation = new();
    private Task? _pollTask;
    private ulong? _lastIdle;
    private ulong? _lastKernel;
    private ulong? _lastUser;

    public event Action<HardwareMetrics>? MetricsUpdated;

    public void Start()
    {
        _pollTask ??= Task.Run(PollLoopAsync);
    }

    private async Task PollLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            var cpu = ReadCpuUtilization();
            var memory = ReadMemoryUtilization();
            var gpus = ReadGpuAdapters();
            MetricsUpdated?.Invoke(new HardwareMetrics(cpu, memory, gpus));
        }
        while (await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false));
    }

    private double? ReadCpuUtilization()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        var idleTicks = ToUInt64(idle);
        var kernelTicks = ToUInt64(kernel);
        var userTicks = ToUInt64(user);
        double? result = null;

        if (_lastIdle is ulong previousIdle && _lastKernel is ulong previousKernel && _lastUser is ulong previousUser)
        {
            var idleDelta = idleTicks - previousIdle;
            var totalDelta = (kernelTicks - previousKernel) + (userTicks - previousUser);
            if (totalDelta > 0 && idleDelta <= totalDelta)
                result = 100.0 * (totalDelta - idleDelta) / totalDelta;
        }

        _lastIdle = idleTicks;
        _lastKernel = kernelTicks;
        _lastUser = userTicks;
        return result;
    }

    private static double? ReadGpuUtilization()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\CIMV2",
                "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            using var results = searcher.Get();
            var engineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var name = item["Name"] as string;
                    if (name is null)
                        continue;

                    var match = EngineInstancePattern.Match(name);
                    if (!match.Success || !double.TryParse(item["UtilizationPercentage"]?.ToString(), out var value) ||
                        !double.IsFinite(value) || value <= 0)
                        continue;

                    var engineKey = $"{match.Groups["adapter"].Value}_{match.Groups["engine"].Value}";
                    engineTotals[engineKey] = engineTotals.GetValueOrDefault(engineKey) + value;
                }
            }

            return engineTotals.Count == 0 ? 0 : Math.Clamp(engineTotals.Values.Max(), 0, 100);
        }
        catch (ManagementException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IReadOnlyList<GpuAdapterMetrics> ReadGpuAdapters()
    {
        try
        {
            var utilizationByAdapter = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            using (var rows = searcher.Get())
            foreach (ManagementObject row in rows)
            using (row)
            {
                var name = row["Name"] as string;
                var match = name is null ? Match.Empty : EngineInstancePattern.Match(name);
                if (!match.Success || !double.TryParse(row["UtilizationPercentage"]?.ToString(), out var value)) continue;
                var luid = match.Groups["adapter"].Value.Split("_phys_", StringSplitOptions.None)[0];
                var engine = match.Groups["engine"].Value;
                if (!utilizationByAdapter.TryGetValue(luid, out var engines)) utilizationByAdapter[luid] = engines = new(StringComparer.OrdinalIgnoreCase);
                engines[engine] = engines.GetValueOrDefault(engine) + value;
            }

            var dedicatedByAdapter = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            var sharedByAdapter = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            using (var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, DedicatedUsage, SharedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory"))
            using (var rows = searcher.Get())
            foreach (ManagementObject row in rows)
            using (row)
                if (row["Name"] is string name)
                {
                    var luidKey = Regex.Match(name, @"luid_0x[0-9a-f]+_0x[0-9a-f]+", RegexOptions.IgnoreCase).Value;
                    if (luidKey.Length > 0)
                    {
                        if (ulong.TryParse(row["DedicatedUsage"]?.ToString(), out var dedicated)) dedicatedByAdapter[luidKey] = dedicatedByAdapter.GetValueOrDefault(luidKey) + dedicated;
                        if (ulong.TryParse(row["SharedUsage"]?.ToString(), out var shared)) sharedByAdapter[luidKey] = sharedByAdapter.GetValueOrDefault(luidKey) + shared;
                    }
                }

            var result = new List<GpuAdapterMetrics>();
            foreach (var key in dedicatedByAdapter.Keys
                .Concat(sharedByAdapter.Keys)
                .Concat(utilizationByAdapter.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var id = ParseLuid(key);
                if (id is null) continue;
                var adapter = DxCoreAdapterEnumerator.GetByLuid(id.Value);
                if (adapter is null || !adapter.IsHardware) continue;
                var kind = adapter.IsIntegrated ? GpuAdapterKind.Integrated : IsExternalTopology(adapter.VendorId, adapter.DeviceId) ? GpuAdapterKind.External : GpuAdapterKind.Discrete;
                var name = GetAdapterName(adapter.VendorId, adapter.DeviceId) ?? $"{adapter.VendorId:X4}:{adapter.DeviceId:X4}";
                var used = adapter.IsIntegrated ? sharedByAdapter.GetValueOrDefault(key) : dedicatedByAdapter.GetValueOrDefault(key);
                var capacity = adapter.IsIntegrated ? adapter.SharedMemoryBytes : adapter.DedicatedMemoryBytes;
                var vram = capacity > 0 ? 100d * used / capacity : (double?)null;
                var gpu = utilizationByAdapter.TryGetValue(key, out var engines) && engines.Count > 0 ? Math.Clamp(engines.Values.Max(), 0, 100) : 0;
                result.Add(new GpuAdapterMetrics(name, kind, gpu, vram));
            }
            return result.OrderBy(gpu => gpu.Kind).ToArray();
        }
        catch (ManagementException) { return Array.Empty<GpuAdapterMetrics>(); }
        catch (COMException) { return Array.Empty<GpuAdapterMetrics>(); }
    }

    private static DxCoreAdapterEnumerator.Luid? ParseLuid(string value)
    {
        var match = Regex.Match(value, @"luid_0x(?<high>[0-9a-f]+)_0x(?<low>[0-9a-f]+)", RegexOptions.IgnoreCase);
        return match.Success && uint.TryParse(match.Groups["low"].Value, System.Globalization.NumberStyles.HexNumber, null, out var low) &&
            int.TryParse(match.Groups["high"].Value, System.Globalization.NumberStyles.HexNumber, null, out var high)
            ? new DxCoreAdapterEnumerator.Luid { LowPart = low, HighPart = high }
            : null;
    }

    private static string? GetAdapterName(uint vendorId, uint deviceId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Name, PNPDeviceID FROM Win32_VideoController");
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            using (row)
                if (row["PNPDeviceID"] is string id && id.Contains($"VEN_{vendorId:X4}&DEV_{deviceId:X4}", StringComparison.OrdinalIgnoreCase))
                    return row["Name"] as string;
        }
        catch (ManagementException) { }
        return null;
    }

    private static bool IsExternalTopology(uint vendorId, uint deviceId)
    {
        try
        {
            var needle = $"VEN_{vendorId:X4}&DEV_{deviceId:X4}";
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT PNPDeviceID FROM Win32_VideoController");
            using var rows = searcher.Get();
            string? instanceId = null;
            foreach (ManagementObject row in rows)
            {
                using (row)
                    if (row["PNPDeviceID"] is string id && id.Contains(needle, StringComparison.OrdinalIgnoreCase)) { instanceId = id; break; }
            }
            if (instanceId is null || CM_Locate_DevNodeW(out var node, instanceId, 0) != 0) return false;
            var size = 0u;
            _ = CM_Get_DevNode_Registry_PropertyW(node, 0x24, out _, IntPtr.Zero, ref size, 0);
            if (size == 0) return false;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (CM_Get_DevNode_Registry_PropertyW(node, 0x24, out _, buffer, ref size, 0) != 0) return false;
                var paths = (Marshal.PtrToStringUni(buffer, (int)size / sizeof(char)) ?? string.Empty).Replace('\0', '|');
                if (!Regex.IsMatch(paths, @"ACPI\(RP\d+\).*ACPI\(PXSX\)", RegexOptions.IgnoreCase))
                    return false;

                // A downstream PCIe path alone can also describe an internal GPU. Require
                // Windows' device tree to mark the adapter as removable/ejectable.
                var removalPolicy = ReadConfigManagerDword(node, 0x20); // CM_DRP_REMOVAL_POLICY
                var capabilities = ReadConfigManagerDword(node, 0x10); // CM_DRP_CAPABILITIES
                const uint removableCapabilities = 0x04 | 0x08 | 0x80; // removable/eject/surprise removal
                return removalPolicy == 2 || capabilities is uint value && (value & removableCapabilities) != 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return false; }
    }

    private static uint? ReadConfigManagerDword(uint devInst, uint property)
    {
        var size = 0u;
        _ = CM_Get_DevNode_Registry_PropertyW(devInst, property, out var type, IntPtr.Zero, ref size, 0);
        if (size < sizeof(uint) || type != 4) // REG_DWORD
            return null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (CM_Get_DevNode_Registry_PropertyW(devInst, property, out type, buffer, ref size, 0) != 0 || type != 4)
                return null;
            return unchecked((uint)Marshal.ReadInt32(buffer));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint propertyType, IntPtr buffer, ref uint bufferSize, uint flags);

    private static double? ReadMemoryUtilization()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0
            ? 100d * (status.TotalPhysical - status.AvailablePhysical) / status.TotalPhysical
            : null;
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _pollTask?.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _cancellation.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
