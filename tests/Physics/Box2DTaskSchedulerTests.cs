using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Brutal.Box2DApi;
using CrushKit.Brutal.Physics;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Physics.Tests;

public sealed class Box2DTaskSchedulerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    [InlineData(int.MaxValue)]
    public void RejectsInvalidWorkerCountBeforeStartingThreads(int count) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new Box2DTaskScheduler(count));

    [Fact]
    public unsafe void RangesCoverEveryItemAndUseStableDistinctConcurrentWorkerIndices()
    {
        using var scheduler = new Box2DTaskScheduler(2);
        var visits = new int[100];
        var threads = new ConcurrentDictionary<uint, int>();
        var active = new ConcurrentDictionary<uint, byte>();
        using var bothWorkers = new CountdownEvent(2);
        int firstRanges = 0;
        B2TaskCallback callback = (start, end, worker, context) =>
        {
            Assert.Equal((nint)1234, context);
            Assert.InRange(worker, 0u, 1u);
            Assert.Equal(Environment.CurrentManagedThreadId, threads.GetOrAdd(worker, Environment.CurrentManagedThreadId));
            Assert.True(active.TryAdd(worker, 0));
            try
            {
                if (Interlocked.Increment(ref firstRanges) <= 2)
                {
                    bothWorkers.Signal();
                    Assert.True(bothWorkers.Wait(TimeSpan.FromSeconds(10)));
                }
                for (int i = start; i < end; i++) Interlocked.Increment(ref visits[i]);
            }
            finally { active.TryRemove(worker, out _); }
        };
        nint pointer = Marshal.GetFunctionPointerForDelegate(callback);
        for (int i = 0; i < 20; i++)
        {
            var token = (nint)scheduler.Enqueue(pointer, visits.Length, 1, 1234, 0);
            Assert.NotEqual(0, token);
            scheduler.Finish(token, 0);
            scheduler.ThrowIfFaulted();
        }
        Assert.Equal(2, threads.Count);
        Assert.All(visits, count => Assert.Equal(20, count));
        GC.KeepAlive(callback);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(101, 32)]
    [InlineData(17, 1)]
    [InlineData(1000, 71)]
    public unsafe void TailRangesAndMinimumRangeCoverItemsExactlyOnce(int count, int minRange)
    {
        using var scheduler = new Box2DTaskScheduler(3);
        var visits = new int[count];
        B2TaskCallback callback = (start, end, worker, context) =>
        {
            Assert.InRange(start, 0, count - 1);
            Assert.InRange(end, start + 1, count);
            for (int i = start; i < end; i++) Interlocked.Increment(ref visits[i]);
        };
        nint pointer = Marshal.GetFunctionPointerForDelegate(callback);
        var token = (nint)scheduler.Enqueue(pointer, count, minRange, 0, 0);
        scheduler.Drain();
        scheduler.Finish(token, 0);
        scheduler.ThrowIfFaulted();
        Assert.All(visits, value => Assert.Equal(1, value));
        GC.KeepAlive(callback);
    }

    [Fact]
    public unsafe void WorkerFailureIsCapturedWithoutEscapingCallbackOrStrandingCompletion()
    {
        using var scheduler = new Box2DTaskScheduler(2);
        var failure = new InvalidOperationException("injected managed callback failure");
        B2TaskCallback callback = (start, end, worker, context) => throw failure;
        var token = (nint)scheduler.Enqueue(Marshal.GetFunctionPointerForDelegate(callback), 100, 1, 0, 0);
        scheduler.Finish(token, 0); // Must not throw across the reverse-P/Invoke entry point.
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(scheduler.ThrowIfFaulted));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(scheduler.ThrowIfFaulted));
        GC.KeepAlive(callback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchFailureAfterScheduleDrainsBeforeReturningNull(bool afterFlush)
    {
        var point = afterFlush ? Box2DTaskScheduler.DispatchPoint.AfterFlush : Box2DTaskScheduler.DispatchPoint.AfterSchedule;
        using var injected = new ManualResetEventSlim();
        using var started = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("injected known-handle dispatch failure");
        using var scheduler = new Box2DTaskScheduler(2, at =>
        {
            if (at != point) return;
            injected.Set();
            throw failure;
        });
        int active = 0, completed = 0;
        B2TaskCallback callback = (start, end, worker, context) =>
        {
            Interlocked.Increment(ref active);
            try
            {
                started.Signal();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                Interlocked.Increment(ref completed);
            }
            finally { Interlocked.Decrement(ref active); }
        };
        nint pointer = Marshal.GetFunctionPointerForDelegate(callback);
        static unsafe nint Enqueue(Box2DTaskScheduler owner, nint callback) =>
            (nint)owner.Enqueue(callback, 100, 1, 0, 0);
        var enqueue = Task.Run(() => Enqueue(scheduler, pointer));
        try
        {
            Assert.True(injected.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(enqueue.IsCompleted); // Null must not imply completion while callbacks are active.
            Assert.Equal(2, Volatile.Read(ref active));
            release.Set();
            Assert.Equal(0, await enqueue.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, Volatile.Read(ref active));
            Assert.Equal(2, Volatile.Read(ref completed)); // No duplicate flush/dispatch.
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(scheduler.ThrowIfFaulted));
        }
        finally
        {
            release.Set();
            await enqueue.WaitAsync(TimeSpan.FromSeconds(10));
            GC.KeepAlive(callback);
        }
    }

    [Fact]
    public unsafe void InvalidCallbackInputAndFinishTokenAreCaptured()
    {
        using var invalid = new Box2DTaskScheduler(1);
        Assert.Equal(0, (nint)invalid.Enqueue(0, 1, 1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(invalid.ThrowIfFaulted);
        using var unknown = new Box2DTaskScheduler(1);
        unknown.Finish(42, 0);
        Assert.Throws<InvalidOperationException>(unknown.ThrowIfFaulted);
    }

    [Fact]
    public async Task DisposeDrainsAcceptedWorkBeforeStoppingWorkersAndIsIdempotent()
    {
        var scheduler = new Box2DTaskScheduler(2);
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        B2TaskCallback callback = (start, end, worker, context) =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        try
        {
            unsafe { _ = scheduler.Enqueue(Marshal.GetFunctionPointerForDelegate(callback), 1, 1, 0, 0); }
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var disposal = Task.Run(() => { entered.SetResult(); scheduler.Dispose(); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(50);
            Assert.False(disposal.IsCompleted);
            release.Set();
            await disposal.WaitAsync(TimeSpan.FromSeconds(10));
            scheduler.ThrowIfFaulted();
            scheduler.Dispose();
            var definition = B2WorldDef.Default;
            Assert.Throws<ObjectDisposedException>(() => scheduler.Configure(ref definition));
        }
        finally { release.Set(); scheduler.Dispose(); GC.KeepAlive(callback); }
    }
}
