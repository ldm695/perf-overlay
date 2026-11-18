using PerfOverlay.Capture;
using PerfOverlay.Metrics;

namespace PerfOverlay.Tests;

public class FrameStatisticsTests
{
    [Fact]
    public void ReportsFpsFromAverageIntervalInCurrentWindow()
    {
        var statistics = new FrameStatistics();
        for (var i = 0; i < 10; i++)
            statistics.AddFrame(CreateFrame(16.0));

        var snapshot = statistics.GetSnapshot();

        Assert.Equal(1000.0 / 16.0, snapshot.CurrentFps!.Value, 6);
        Assert.Null(snapshot.OnePercentLowFps);
    }

    [Fact]
    public void OnePercentLowRequiresOneHundredSamples()
    {
        var statistics = new FrameStatistics();
        for (var i = 0; i < 99; i++)
            statistics.AddFrame(CreateFrame(16.0));

        Assert.Null(statistics.GetSnapshot().OnePercentLowFps);

        statistics.AddFrame(CreateFrame(16.0));

        Assert.Equal(1000.0 / 16.0, statistics.GetSnapshot().OnePercentLowFps!.Value, 6);
    }

    [Fact]
    public void OnePercentLowUsesSlowestOnePercentOfFrames()
    {
        var statistics = new FrameStatistics();
        for (var i = 0; i < 198; i++)
            statistics.AddFrame(CreateFrame(10.0));
        statistics.AddFrame(CreateFrame(50.0));
        statistics.AddFrame(CreateFrame(100.0));

        Assert.Equal((1000.0 / 100.0 + 1000.0 / 50.0) / 2.0, statistics.GetSnapshot().OnePercentLowFps!.Value, 6);
    }

    [Fact]
    public void PcLatencyDoesNotCreateFrameRate()
    {
        var statistics = new FrameStatistics();
        statistics.AddFrame(CreateFrame(null, latencyMs: 25.0));

        var snapshot = statistics.GetSnapshot();

        Assert.Null(snapshot.CurrentFps);
        Assert.Equal(25.0, snapshot.AverageLatencyMs!.Value, 6);
    }

    [Fact]
    public void ResetClearsFramesAndLatency()
    {
        var statistics = new FrameStatistics();
        statistics.AddFrame(CreateFrame(16.0, latencyMs: 20.0));

        statistics.Reset();
        var cleared = statistics.GetSnapshot();

        Assert.Null(cleared.CurrentFps);
        Assert.Null(cleared.AverageLatencyMs);
    }

    [Fact]
    public void RejectsNonFiniteAndOutOfRangeValues()
    {
        var statistics = new FrameStatistics();
        statistics.AddFrame(CreateFrame(double.NaN, latencyMs: double.PositiveInfinity));
        statistics.AddFrame(CreateFrame(0.0, latencyMs: -1.0));
        statistics.AddFrame(CreateFrame(10_001.0, latencyMs: 10_001.0));

        var snapshot = statistics.GetSnapshot();

        Assert.Null(snapshot.CurrentFps);
        Assert.Null(snapshot.AverageLatencyMs);
    }

    private static PresentMonFrameSample CreateFrame(double? intervalMs, double? latencyMs = null) =>
        new(null, null, intervalMs, null, latencyMs);
}
