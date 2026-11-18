using System.Diagnostics;
using PerfOverlay.Capture;

namespace PerfOverlay.Metrics;

public sealed class FrameStatistics
{
    private const double CurrentWindowMs = 1_000.0;
    private const double RollingWindowMs = 30_000.0;
    private const double FutureTimestampToleranceMs = 250.0;
    private const int MinimumLowFrameSamples = 100;
    private readonly object _gate = new();
    private readonly List<StatisticSample> _samples = [];
    private double? _sourceTimeOffsetMs;
    private bool _hasSeenFrame;

    public void AddFrame(PresentMonFrameSample frame)
    {
        var frameTimeMs = IsValidDuration(frame.FrameIntervalMs) ? frame.FrameIntervalMs : null;
        var simulationIntervalMs = IsValidDuration(frame.MsBetweenSimulationStart) ? frame.MsBetweenSimulationStart : null;
        var latencyMs = IsValidDuration(frame.MsPCLatency) ? frame.MsPCLatency : null;
        var nowMs = GetStopwatchMilliseconds();

        lock (_gate)
        {
            var sourcePresentTimeMs = frame.PresentTimeMs;
            var sourceFrameTimeMs = frame.DisplayTimeMs ?? sourcePresentTimeMs;
            if (sourcePresentTimeMs is double presentTime && _sourceTimeOffsetMs is null)
                _sourceTimeOffsetMs = nowMs - presentTime;

            var frameTimestampMs = MapSourceTime(sourceFrameTimeMs, nowMs);
            var latencyTimestampMs = MapSourceTime(sourcePresentTimeMs, frameTimestampMs);
            _samples.Add(new StatisticSample(frameTimestampMs, latencyTimestampMs, frame with
            {
                FrameIntervalMs = frameTimeMs,
                MsBetweenSimulationStart = simulationIntervalMs,
                MsPCLatency = latencyMs
            }));

            if (frameTimeMs is not null)
                _hasSeenFrame = true;

            Trim(nowMs - RollingWindowMs);
        }
    }

    public FrameMetrics GetSnapshot()
    {
        lock (_gate)
        {
            var nowMs = GetStopwatchMilliseconds();
            Trim(nowMs - RollingWindowMs);

            var currentFrames = _samples
                .Where(x => x.FrameTimestampMs >= nowMs - CurrentWindowMs && x.FrameTimestampMs <= nowMs + FutureTimestampToleranceMs)
                .Where(x => x.Frame.FrameIntervalMs is not null)
                .ToArray();
            double? currentFps = currentFrames.Length == 0
                ? (_hasSeenFrame ? 0 : null)
                : 1000.0 / currentFrames.Average(x => x.Frame.FrameIntervalMs!.Value);

            var currentSimulationIntervals = _samples
                .Where(x => x.FrameTimestampMs >= nowMs - CurrentWindowMs && x.FrameTimestampMs <= nowMs + FutureTimestampToleranceMs)
                .Select(x => x.Frame.MsBetweenSimulationStart)
                .Where(x => x is not null)
                .Select(x => x!.Value)
                .ToArray();
            double? baseFps = currentSimulationIntervals.Length == 0
                ? null
                : 1000.0 / currentSimulationIntervals.Average();

            var currentLatencies = _samples
                .Where(x => x.LatencyTimestampMs >= nowMs - CurrentWindowMs && x.LatencyTimestampMs <= nowMs + FutureTimestampToleranceMs)
                .Where(x => x.Frame.MsPCLatency is not null)
                .ToArray();
            double? averageLatency = currentLatencies.Length == 0
                ? null
                : currentLatencies.Average(x => x.Frame.MsPCLatency!.Value);

            var frameSamples = _samples
                .Where(x => x.FrameTimestampMs >= nowMs - RollingWindowMs)
                .Where(x => x.Frame.FrameIntervalMs is not null)
                .ToArray();
            if (frameSamples.Length < MinimumLowFrameSamples)
                return new FrameMetrics(currentFps, baseFps, null, averageLatency);

            var tailCount = Math.Max(1, (int)Math.Ceiling(frameSamples.Length * 0.01));
            var slowestAverageFps = frameSamples
                .Select(x => 1000.0 / x.Frame.FrameIntervalMs!.Value)
                .OrderBy(x => x)
                .Take(tailCount)
                .Average();

            return new FrameMetrics(currentFps, baseFps, slowestAverageFps, averageLatency);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _sourceTimeOffsetMs = null;
            _hasSeenFrame = false;
        }
    }

    private double MapSourceTime(double? sourceTimeMs, double fallbackMs) =>
        sourceTimeMs is double value && _sourceTimeOffsetMs is double offset
            ? value + offset
            : fallbackMs;

    private void Trim(double oldestAllowedMs) =>
        _samples.RemoveAll(x => x.FrameTimestampMs < oldestAllowedMs && x.LatencyTimestampMs < oldestAllowedMs);

    private static bool IsValidDuration(double? value) =>
        value is double duration && double.IsFinite(duration) && duration > 0 && duration <= 10_000;

    private static double GetStopwatchMilliseconds() =>
        Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    private readonly record struct StatisticSample(
        double FrameTimestampMs,
        double LatencyTimestampMs,
        PresentMonFrameSample Frame);
}
