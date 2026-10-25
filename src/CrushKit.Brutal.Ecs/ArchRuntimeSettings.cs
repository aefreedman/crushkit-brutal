namespace CrushKit.Brutal.Ecs;

/// <summary>Explicit immutable scheduler capacity; the application owns its defaults and total CPU budget.</summary>
public sealed class ArchRuntimeSettings
{
    public int WorkerCount { get; }
    public int MaxExpectedConcurrentJobs { get; }

    public ArchRuntimeSettings(int workerCount, int maxExpectedConcurrentJobs)
    {
        // Bounded allocation budgets, not default thread/capacity choices.
        if (workerCount is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(workerCount));
        if (maxExpectedConcurrentJobs is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(maxExpectedConcurrentJobs));
        WorkerCount = workerCount;
        MaxExpectedConcurrentJobs = maxExpectedConcurrentJobs;
    }
}
