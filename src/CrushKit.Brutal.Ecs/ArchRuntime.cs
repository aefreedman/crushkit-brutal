using System.Collections.Concurrent;
using Arch.Core;
using Schedulers;

namespace CrushKit.Brutal.Ecs;

/// <summary>Owns one Arch world and a dependency-job scheduler. Submissions are serialized;
/// independent stages within a submission run on workers. Does not own Arch's static query scheduler.</summary>
public sealed class ArchRuntime : IDisposable, IAsyncDisposable
{
    private static readonly object WorldLifetimeGate = new();
    [ThreadStatic] private static ArchRuntime? executing;
    private readonly object gate = new();
    private readonly BlockingCollection<Graph> submissions = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread coordinator;
    private bool stopping;

    public ArchRuntimeSettings Settings { get; }

    public ArchRuntime(ArchRuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        coordinator = new Thread(Coordinate) { IsBackground = true, Name = "CrushKit.Ecs.Coordinator" };
        coordinator.Start();
        try { ready.Task.GetAwaiter().GetResult(); }
        catch
        {
            coordinator.Join();
            submissions.Dispose();
            throw;
        }
    }

    /// <summary>Runs exclusive setup through the same submission queue. Never retain the world/reference
    /// outside a callback. Initialization is also serialized across kit runtimes for Arch's global registries.</summary>
    public void Initialize(Action<World> initialize)
    {
        WorldJob.ValidateCallback(initialize);
        RunAsync(new WorldJob("Initialize", world =>
        {
            lock (WorldLifetimeGate) initialize(world);
        })).GetAwaiter().GetResult();
    }

    /// <summary>Queues a copied graph in invocation order. Failure skips dependent stages, drains every
    /// independent branch and faults the task; later submissions can still run. No cancellation mid-graph.</summary>
    public Task RunAsync(params WorldJob[] jobs)
    {
        RejectReentrancy();
        ArgumentNullException.ThrowIfNull(jobs);
        var copy = (WorldJob[])jobs.Clone();
        for (int index = 0; index < copy.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(copy[index]);
            var unique = new HashSet<int>();
            foreach (int dependency in copy[index].DependencyIndexes)
            {
                if (dependency < 0 || dependency >= index)
                    throw new ArgumentException("Dependencies must reference an earlier stage in this graph.", nameof(jobs));
                if (!unique.Add(dependency))
                    throw new ArgumentException("A stage cannot repeat a dependency index.", nameof(jobs));
            }
        }
        var graph = new Graph(copy);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            submissions.Add(graph);
        }
        return graph.Completion.Task;
    }

    public void Dispose() => Stop().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => new(Stop());

    private Task Stop()
    {
        RejectReentrancy();
        lock (gate)
        {
            if (!stopping)
            {
                stopping = true;
                submissions.CompleteAdding();
            }
        }
        return stopped.Task;
    }

    private void RejectReentrancy()
    {
        if (ReferenceEquals(executing, this))
            throw new InvalidOperationException("Do not submit work or dispose a runtime from one of its own jobs.");
    }

    private void Coordinate()
    {
        World? world = null;
        JobScheduler? scheduler = null;
        Exception? shutdownError = null;
        try
        {
            lock (WorldLifetimeGate) world = World.Create();
            // Schedule/Flush must run on this construction thread, not on a ThreadPool continuation.
            scheduler = new JobScheduler(new JobScheduler.Config
            {
                ThreadCount = Settings.WorkerCount,
                ThreadPrefixName = "CrushKit.Ecs.Worker",
                MaxExpectedConcurrentJobs = Settings.MaxExpectedConcurrentJobs,
                StrictAllocationMode = false
            });
            ready.SetResult();
            foreach (var graph in submissions.GetConsumingEnumerable())
            {
                try
                {
                    ExecuteGraph(world, scheduler, graph.Jobs);
                    graph.Completion.SetResult();
                }
                catch (Exception error) { graph.Completion.SetException(error); }
            }
        }
        catch (Exception error)
        {
            shutdownError = error;
            ready.TrySetException(error);
            // If coordinator setup/dispatch fails, no accepted submission can be left awaiting forever.
            lock (gate)
            {
                stopping = true;
                submissions.CompleteAdding();
                while (submissions.TryTake(out var pending)) pending.Completion.TrySetException(error);
            }
        }
        finally
        {
            try { if (world is not null) lock (WorldLifetimeGate) World.Destroy(world); }
            catch (Exception error) { shutdownError = Combine(shutdownError, error); }
            try { scheduler?.Dispose(); }
            catch (Exception error) { shutdownError = Combine(shutdownError, error); }
            submissions.Dispose();
            if (shutdownError is null) stopped.TrySetResult();
            else stopped.TrySetException(shutdownError);
        }
    }

    private void ExecuteGraph(World world, JobScheduler scheduler, WorldJob[] jobs)
    {
        var handles = new JobHandle[jobs.Length];
        var states = new int[jobs.Length];
        var errors = new Exception?[jobs.Length];
        try
        {
            for (int index = 0; index < jobs.Length; index++)
            {
                var dependencies = jobs[index].DependencyIndexes;
                JobHandle? dependency = null;
                if (dependencies.Length == 1) dependency = handles[dependencies[0]];
                else if (dependencies.Length > 1)
                {
                    var inputs = new JobHandle[dependencies.Length];
                    for (int n = 0; n < inputs.Length; n++) inputs[n] = handles[dependencies[n]];
                    dependency = scheduler.CombineDependencies(inputs);
                }
                handles[index] = scheduler.Schedule(new GuardedJob(this, world, jobs, index, states, errors), dependency);
            }
            // GuardedJob captures ordinary application failures. Infrastructure failure is different:
            // Schedule/CombineDependencies can partially accept work before throwing; a partial Flush
            // cannot safely be retried. No public scheduler API can establish quiescence in that case.
            scheduler.Flush();
            JobHandle.CompleteAll(handles.AsSpan());
        }
        catch (Exception error)
        {
            Environment.FailFast("ECS scheduler infrastructure failed; unknown jobs may still reference the world. Refusing unsafe cleanup.", error);
            throw; // Unreachable; documents that infrastructure failure never becomes an ordinary graph fault.
        }
        var failures = errors.Where(error => error is not null).Cast<Exception>().ToList();
        if (failures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("World jobs failed after all branches drained.", failures);
    }

    private static Exception Combine(Exception? first, Exception next) =>
        first is null ? next : new AggregateException(first, next);

    private sealed class Graph(WorldJob[] jobs)
    {
        internal readonly WorldJob[] Jobs = jobs;
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GuardedJob(ArchRuntime owner, World world, WorldJob[] jobs, int index,
        int[] states, Exception?[] errors) : IJob
    {
        public void Execute()
        {
            foreach (int dependency in jobs[index].DependencyIndexes)
                if (Volatile.Read(ref states[dependency]) != 1)
                {
                    Volatile.Write(ref states[index], 3); // Failed/skipped ancestor: do not observe partial state.
                    return;
                }
            var previous = executing;
            executing = owner;
            try
            {
                jobs[index].Execute(world);
                Volatile.Write(ref states[index], 1);
            }
            catch (Exception error)
            {
                errors[index] = error;
                Volatile.Write(ref states[index], 2);
            }
            finally { executing = previous; }
        }
    }
}
