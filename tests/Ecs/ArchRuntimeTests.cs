using Arch.Core;
using CrushKit.Brutal.Ecs;
using Xunit;

namespace Ecs.Tests;

public sealed class ArchRuntimeTests
{
    private static ArchRuntime Create(int workers = 2) => new(new(workers, 64));
    private struct Counter { public int Value; }
    // RunAsync validates synchronously before returning a task.
    private static void Submit(ArchRuntime runtime, params WorldJob[] jobs) => _ = runtime.RunAsync(jobs);

    [Fact]
    public async Task IndependentStagesRunOnDifferentWorkersAndDependentStageSeesBoth()
    {
        using var runtime = Create();
        using var rendezvous = new Barrier(2);
        var threadIds = new int[2];
        int completed = 0;
        await runtime.RunAsync(
            new("Left", _ =>
            {
                threadIds[0] = Environment.CurrentManagedThreadId;
                Assert.True(rendezvous.SignalAndWait(TimeSpan.FromSeconds(5)));
                Interlocked.Increment(ref completed);
            }),
            new("Right", _ =>
            {
                threadIds[1] = Environment.CurrentManagedThreadId;
                Assert.True(rendezvous.SignalAndWait(TimeSpan.FromSeconds(5)));
                Interlocked.Increment(ref completed);
            }),
            new("Join", _ => Assert.Equal(2, completed), 0, 1)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(threadIds[0], threadIds[1]);
    }

    [Fact]
    public async Task SubmissionsSerializeAndCanMutateArchComponents()
    {
        using var runtime = Create();
        Entity entity = default;
        runtime.Initialize(world => entity = world.Create(new Counter()));
        var tasks = Enumerable.Range(0, 100).Select(index => runtime.RunAsync(new WorldJob("Increment", world =>
        {
            ref var counter = ref world.Get<Counter>(entity);
            Assert.Equal(index, counter.Value);
            counter.Value++;
        }))).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        runtime.Initialize(world => Assert.Equal(100, world.Get<Counter>(entity).Value));
    }

    [Fact]
    public async Task FaultSkipsDescendantsButDrainsIndependentBranchAndRuntimeSurvives()
    {
        using var runtime = Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool dependentRan = false, independentDone = false;
        var failure = new InvalidOperationException("injected");
        var graph = runtime.RunAsync(
            new("Failure", _ => throw failure),
            new("Skipped", _ => dependentRan = true, 0),
            new("Independent", _ =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                independentDone = true;
            }),
            new("Skipped join", _ => dependentRan = true, 1, 2));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(graph.IsCompleted);
        release.Set();
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => graph.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(failure, actual);
        Assert.True(independentDone);
        Assert.False(dependentRan);
        await runtime.RunAsync(new WorldJob("Recovered", _ => { }));
    }

    [Fact]
    public async Task DisposeDrainsQueuedGraphsAndRejectsSubmissions()
    {
        var runtime = Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int finished = 0;
        var first = runtime.RunAsync(new WorldJob("First", _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            Interlocked.Increment(ref finished);
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var second = runtime.RunAsync(new WorldJob("Second", _ => Interlocked.Increment(ref finished)));
        var disposing = runtime.DisposeAsync().AsTask();
        Assert.False(disposing.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() => { _ = runtime.RunAsync(new WorldJob("Too late", _ => { })); });
        release.Set();
        await Task.WhenAll(first, second, disposing).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, finished);
        runtime.Dispose();
    }

    [Fact]
    public async Task ConcurrentRuntimesHaveIndependentSchedulersAndDisposal()
    {
        using var left = Create();
        using var right = Create();
        using var rendezvous = new Barrier(2);
        await Task.WhenAll(left.RunAsync(new WorldJob("Left", _ => Assert.True(rendezvous.SignalAndWait(TimeSpan.FromSeconds(5))))),
            right.RunAsync(new WorldJob("Right", _ => Assert.True(rendezvous.SignalAndWait(TimeSpan.FromSeconds(5))))));
        left.Dispose();
        await right.RunAsync(new WorldJob("Still usable", _ => { }));
    }

    [Fact]
    public async Task ReentrantSubmitAndDisposeAreRejectedRatherThanDeadlock()
    {
        using var runtime = Create();
        await runtime.RunAsync(new WorldJob("Reentrant", _ =>
        {
            Assert.Throws<InvalidOperationException>(() => runtime.Dispose());
            Assert.Throws<InvalidOperationException>(() => Submit(runtime));
            Assert.Throws<InvalidOperationException>(() => runtime.Initialize(_ => { }));
        })).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void InvalidGraphsAndSettingsFailBeforeWork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchRuntimeSettings(0, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchRuntimeSettings(65, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchRuntimeSettings(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchRuntimeSettings(2, 65_537));
        using var runtime = Create();
        Assert.Throws<ArgumentException>(() => Submit(runtime, new WorldJob("Self", _ => { }, 0)));
        Assert.Throws<ArgumentException>(() => Submit(runtime, new WorldJob("Negative", _ => { }, -1)));
        Assert.Throws<ArgumentException>(() => Submit(runtime, new WorldJob("Forward", _ => { }, 1), new("Other", _ => { })));
    }

    [Fact]
    public async Task DuplicateDependenciesAreRejectedBeforeAnyStageCanRun()
    {
        using var runtime = Create();
        bool ran = false;
        Assert.Throws<ArgumentException>(() => Submit(runtime,
            new WorldJob("First", _ => ran = true),
            new WorldJob("Duplicate", _ => ran = true, 0, 0)));
        Assert.False(ran);
        await runtime.RunAsync(new WorldJob("Still usable", _ => ran = true));
        Assert.True(ran);
    }

    [Fact]
    public void AsyncVoidCallbacksAreRejectedBeforeExecution()
    {
        Action<World> callback = async _ => await Task.Yield();
        Assert.Throws<ArgumentException>(() => new WorldJob("Async void", callback));
        using var runtime = Create();
        Assert.Throws<ArgumentException>(() => runtime.Initialize(callback));
    }

    [Fact]
    public async Task MultipleFailuresAreAggregatedAfterDraining()
    {
        using var runtime = Create();
        var error = await Assert.ThrowsAsync<AggregateException>(() => runtime.RunAsync(
            new WorldJob("First", _ => throw new InvalidOperationException("first")),
            new WorldJob("Second", _ => throw new ArgumentException("second"))));
        Assert.Equal(2, error.InnerExceptions.Count);
    }

    [Fact]
    public async Task OneWorkerHandlesDependenciesAndEmptyGraphs()
    {
        using var runtime = Create(1);
        await runtime.RunAsync();
        int state = 0;
        await runtime.RunAsync(new("First", _ => state = 1), new("Second", _ => Assert.Equal(1, state), 0));
    }
}
