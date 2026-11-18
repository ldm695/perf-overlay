namespace PerfOverlay.Metrics;

public readonly record struct FrameMetrics(
    double? CurrentFps,
    double? BaseFps,
    double? OnePercentLowFps,
    double? AverageLatencyMs);
