using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.Box2DApi;
using CrushKit.Brutal.Physics;
using Schedulers;
using Xunit;

namespace Physics.Tests;

public sealed class RangeCompletionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static unsafe nint Enqueue(Box2DTaskScheduler scheduler, B2TaskCallback callback) =>
        (nint)scheduler.Enqueue(Marshal.GetFunctionPointerForDelegate(callback), 100, 1, 1234, 0);

    [Fact]
    public void NativeRangesUsePublishedPlainJobsNotParallelMasterFinalization()
    {
        var nativeTask = typeof(Box2DTaskScheduler).GetNestedType("NativeTask", BindingFlags.NonPublic)!;
        var range = nativeTask.GetNestedType("NativeRange", BindingFlags.NonPublic)!;
        Assert.True(typeof(IJob).IsAssignableFrom(range));
        Assert.False(typeof(IJobParallelFor).IsAssignableFrom(range));
        Assert.False(typeof(IJobParallelFor).IsAssignableFrom(nativeTask));
    }

    [Fact]
    public async Task HandlesMayPublishBeforeEnqueueReturnsWithoutLosingCompletion()
    {
        await Task.Run(CompleteBeforePublication).WaitAsync(Bound);
    }

    private static void CompleteBeforePublication()
    {
        Box2DTaskScheduler? scheduler = null;
        int calls = 0;
        // Force published-handle completion BEFORE dispatch publication/Finish subscribes.
        // Reflection is test-only inspection of owned handles, not a production scheduler API.
        using (scheduler = new Box2DTaskScheduler(2, at =>
        {
            if (at != Box2DTaskScheduler.DispatchPoint.AfterFlush) return;
            var tasks = (System.Collections.IDictionary)typeof(Box2DTaskScheduler)
                .GetField("tasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scheduler)!;
            var task = Assert.Single(tasks.Values.Cast<object>());
            var handles = (JobHandle[])task.GetType().GetField("handles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task)!;
            foreach (var handle in handles) handle.Complete();
        }))
        {
            B2TaskCallback callback = (_, _, _, _) => Interlocked.Increment(ref calls);
            nint token = Enqueue(scheduler, callback);
            scheduler.Drain();
            scheduler.Finish(token, 0);
            scheduler.ThrowIfFaulted();
            Assert.Equal(2, calls);
            GC.KeepAlive(callback);
        }
    }

    [Theory]
    [InlineData("finish", 0)]
    [InlineData("finish", 1)]
    [InlineData("drain", 0)]
    [InlineData("drain", 1)]
    [InlineData("dispose", 0)]
    [InlineData("dispose", 1)]
    public async Task EveryRangeManagedReturnMustPrecedeCompletion(string operation, int heldRange)
    {
        using var returned = new CountdownEvent(2);
        using var completing = new ManualResetEventSlim();
        using var release0 = new ManualResetEventSlim();
        using var release1 = new ManualResetEventSlim();
        var releases = new[] { release0, release1 };
        var visits = new int[100];
        var active = new ConcurrentDictionary<uint, byte>();
        using var nativeOverlap = new CountdownEvent(2);
        using var scheduler = new Box2DTaskScheduler(2, null, index =>
        {
            returned.Signal();
            Assert.True(releases[index].Wait(Bound));
        }, () => completing.Set());
        B2TaskCallback callback = (start, end, worker, context) =>
        {
            Assert.Equal((nint)1234, context);
            Assert.InRange(worker, 0u, 1u);
            Assert.True(active.TryAdd(worker, 0));
            try
            {
                nativeOverlap.Signal();
                Assert.True(nativeOverlap.Wait(Bound));
                for (int i = start; i < end; i++) Interlocked.Increment(ref visits[i]);
            }
            finally { Assert.True(active.TryRemove(worker, out _)); }
        };
        Task? waiter = null;
        try
        {
            nint token = Enqueue(scheduler, callback);
            Assert.NotEqual(0, token);
            Assert.True(returned.Wait(Bound)); // BOTH native delegates returned, Execute still active.
            Assert.Empty(active);
            Assert.All(visits, n => Assert.Equal(1, n));
            releases[1 - heldRange].Set();
            waiter = Task.Run(() =>
            {
                if (operation == "finish") scheduler.Finish(token, 0);
                else if (operation == "drain") scheduler.Drain();
                else scheduler.Dispose();
            });
            Assert.True(completing.Wait(Bound));
            Assert.False(waiter.IsCompleted); // Cannot treat native callback exit as job finalization.
            releases[heldRange].Set();
            await waiter.WaitAsync(Bound);
            if (operation == "drain") scheduler.Finish(token, 0);
            scheduler.ThrowIfFaulted();
        }
        finally
        {
            release0.Set(); release1.Set();
            if (waiter != null) await waiter.WaitAsync(Bound);
            GC.KeepAlive(callback);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task DispatchFaultCannotReturnNullBeforeEveryManagedRangeReturns(bool afterFlush, int heldRange)
    {
        using var returned = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        using var completing = new ManualResetEventSlim();
        int calls = 0;
        var failure = new InvalidOperationException("known range handles");
        var point = afterFlush ? Box2DTaskScheduler.DispatchPoint.AfterFlush : Box2DTaskScheduler.DispatchPoint.AfterSchedule;
        using var scheduler = new Box2DTaskScheduler(2, at => { if (at == point) throw failure; }, index =>
        {
            returned.Signal();
            if (index == heldRange) Assert.True(release.Wait(Bound));
        }, () => completing.Set());
        B2TaskCallback callback = (_, _, _, _) => Interlocked.Increment(ref calls);
        var enqueue = Task.Run(() => Enqueue(scheduler, callback));
        try
        {
            Assert.True(returned.Wait(Bound));
            Assert.True(completing.Wait(Bound));
            Assert.False(enqueue.IsCompleted);
            release.Set();
            Assert.Equal(0, await enqueue.WaitAsync(Bound));
            Assert.Equal(2, calls);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(scheduler.ThrowIfFaulted));
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(scheduler.ThrowIfFaulted));
        }
        finally
        {
            release.Set();
            await enqueue.WaitAsync(Bound);
            GC.KeepAlive(callback);
        }
    }

    [Fact]
    public async Task CompletedTokensRemainSafeAcrossPoolReuseAndRepeatedDrain()
    {
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        int hold = 0;
        using var scheduler = new Box2DTaskScheduler(2, null, _ =>
        {
            if (Volatile.Read(ref hold) == 0) return;
            entered.Signal();
            Assert.True(release.Wait(Bound));
        });
        int calls = 0;
        B2TaskCallback callback = (_, _, _, _) => Interlocked.Increment(ref calls);
        try
        {
            // Keep old native tokens alive while forcing scheduler jobs through the pool.
            var old = new List<nint>();
            for (int i = 0; i < 32; i++)
            {
                old.Add(Enqueue(scheduler, callback));
                scheduler.Drain();
                scheduler.Drain();
            }
            Volatile.Write(ref hold, 1);
            nint current = Enqueue(scheduler, callback);
            Assert.True(entered.Wait(Bound));
            var oldFinish = Task.Run(() => { foreach (var token in old) scheduler.Finish(token, 0); });
            await oldFinish.WaitAsync(Bound); // Must NOT wait on recycled handles/current work.
            release.Set();
            scheduler.Finish(current, 0);
            scheduler.Drain();
            scheduler.ThrowIfFaulted();
            Assert.Equal(66, calls);
        }
        finally { release.Set(); GC.KeepAlive(callback); }
    }

    [Fact]
    public async Task FaultPublicationIsStickyButFinishStillDrainsOtherRangePostamble()
    {
        using var returned = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        using var completing = new ManualResetEventSlim();
        var failure = new InvalidOperationException("first range failed");
        using var scheduler = new Box2DTaskScheduler(2, null, index =>
        {
            returned.Signal();
            if (index == 1) Assert.True(release.Wait(Bound));
        }, () => completing.Set());
        B2TaskCallback callback = (start, _, _, _) => { if (start == 0) throw failure; };
        Task? finish = null;
        try
        {
            nint token = Enqueue(scheduler, callback);
            Assert.True(returned.Wait(Bound));
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(scheduler.ThrowIfFaulted));
            finish = Task.Run(() => scheduler.Finish(token, 0));
            Assert.True(completing.Wait(Bound));
            Assert.False(finish.IsCompleted);
            release.Set();
            await finish.WaitAsync(Bound);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(scheduler.ThrowIfFaulted));
        }
        finally
        {
            release.Set();
            if (finish != null) await finish.WaitAsync(Bound);
            GC.KeepAlive(callback);
        }
    }
}
