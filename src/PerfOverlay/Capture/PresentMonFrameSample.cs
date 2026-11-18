namespace PerfOverlay.Capture;

public sealed record PresentMonFrameSample(
    double? PresentTimeMs,
    double? DisplayTimeMs,
    double? FrameIntervalMs,
    double? MsBetweenSimulationStart,
    double? MsPCLatency);
