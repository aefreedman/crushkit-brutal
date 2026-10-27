using CrushKit.Brutal.ImGui;
using Xunit;

namespace CrushKit.Tests.ImGui;

public sealed class FrameTimeHistoryTests
{
    [Fact]
    public void CapacityAndObservationValidationAreExplicit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameTimeHistory(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameTimeHistory(65537));
        var history = new FrameTimeHistory(3);
        Assert.Equal(default, history.Statistics);
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Observe(-1, 1));
        foreach (double value in new[] { -1, double.NaN, double.PositiveInfinity, double.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => history.Observe(0, value));
        Assert.Equal(0, history.Count);
        Assert.True(history.Observe(0, 0));
        Assert.Equal(new FrameTimeStatistics(1, 0, 0, 0), history.Statistics);
    }

    [Fact]
    public void DuplicateOlderAndUiRetriesDoNotChangeStatisticsAndClearStartsNewEpoch()
    {
        var history = new FrameTimeHistory(3);
        Assert.True(history.Observe(10, 12));
        var statistics = history.Statistics;
        Assert.False(history.Observe(10, 99));
        Assert.False(history.Observe(9, 88));
        for (int i = 0; i < 5; i++) Assert.Equal(statistics, history.Statistics);
        Assert.Equal(1, history.Count);
        history.Clear();
        Assert.Equal(default, history.Statistics);
        Assert.True(history.Observe(0, 7));
        Assert.Equal(new FrameTimeStatistics(1, 7, 7, 7), history.Statistics);
    }

    [Fact]
    public void SaturationOverwritesOldestAndCopiesChronologicalOwnedValues()
    {
        var history = new FrameTimeHistory(3);
        for (int i = 0; i < 5; i++) history.Observe(i, i + 1);
        Assert.Equal(3, history.Count);
        Assert.Equal(new FrameTimeStatistics(3, 3, 4, 5), history.Statistics);
        var copy = new float[3];
        Assert.Equal(3, history.CopyEnvelope(copy));
        Assert.Equal(new float[] { 3, 4, 5 }, copy);
        history.Observe(5, 99);
        Assert.Equal(new float[] { 3, 4, 5 }, copy); // No borrowed ring storage.
        Assert.Equal(new FrameTimeStatistics(3, 4, 36, 99), history.Statistics);
    }

    [Fact]
    public void DenseEnvelopePreservesSpikesAndTheirWithinBucketOrder()
    {
        var history = new FrameTimeHistory(8);
        foreach (var (value, i) in new float[] { 1, 80, 3, 4, 5, 6, 100, 2 }.Select((v, i) => (v, i)))
            history.Observe(i, value);
        var envelope = new float[4];
        Assert.Equal(4, history.CopyEnvelope(envelope));
        Assert.Equal(new float[] { 1, 80, 100, 2 }, envelope);
        Assert.Throws<ArgumentException>(() => history.CopyEnvelope(new float[1]));
        var flat = new FrameTimeHistory(5);
        for (int i = 0; i < 5; i++) flat.Observe(i, 16);
        Assert.Equal(4, flat.CopyEnvelope(envelope));
        Assert.All(envelope, v => Assert.Equal(16, v));
    }

    [Fact]
    public void RepeatedObservationsStatisticsAndCallerOwnedEnvelopesHaveBoundedSteadyStateStorage()
    {
        var history = new FrameTimeHistory(32);
        var scratch = new float[16];
        for (int i = 0; i < 100; i++) { history.Observe(i, i); _ = history.Statistics; history.CopyEnvelope(scratch); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 100; i < 1100; i++) { history.Observe(i, i); _ = history.Statistics; history.CopyEnvelope(scratch); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 512); // Small runtime latitude; no per-observation arrays/publications.
        Assert.Equal(32, history.Count);
    }

    [Fact]
    public void EmptyAndSingleSampleEnvelopeNeedNoSpecialSentinelData()
    {
        var history = new FrameTimeHistory(1);
        Assert.Equal(0, history.CopyEnvelope(Span<float>.Empty));
        history.Observe(0, 2);
        var copy = new float[1];
        Assert.Equal(1, history.CopyEnvelope(copy));
        Assert.Equal(2, copy[0]);
        history.Observe(1, 4);
        Assert.Equal(new FrameTimeStatistics(1, 4, 4, 4), history.Statistics);
    }
}
