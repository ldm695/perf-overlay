namespace PerfOverlay.Metrics;

public enum GpuAdapterKind { Integrated, External, Discrete }

public sealed record GpuAdapterMetrics(string Name, GpuAdapterKind Kind, double? UtilizationPercent, double? VramPercent);

public readonly record struct HardwareMetrics(double? CpuUtilizationPercent, double? MemoryUtilizationPercent, IReadOnlyList<GpuAdapterMetrics> Gpus)
{
    public double? GpuUtilizationPercent => Gpus.Count == 0 ? null : Gpus.Max(gpu => gpu.UtilizationPercent);

    public HardwareMetrics(double? cpuUtilizationPercent, double? gpuUtilizationPercent, double? memoryUtilizationPercent)
        : this(cpuUtilizationPercent, memoryUtilizationPercent,
            gpuUtilizationPercent is double gpu ? [new GpuAdapterMetrics("GPU", GpuAdapterKind.Discrete, gpu, null)] : [])
    {
    }
}
