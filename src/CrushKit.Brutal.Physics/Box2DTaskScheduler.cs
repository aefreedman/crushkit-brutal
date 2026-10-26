using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Brutal.Box2DApi;
using Schedulers;

namespace CrushKit.Brutal.Physics;

/// <summary>
/// Connects Box2D's native parallel-for callbacks to ZeroAllocJobScheduler workers.
/// The caller owns native worlds and must stop stepping/destroy them before disposing this bridge.
/// </summary>
/// <remarks>
/// Does not make concurrent world access safe. Never read/write a world while it steps, or write
/// to the same world concurrently. Configure a definition before creating its world, retain this
/// bridge for that world's lifetime, and call ThrowIfFaulted after every step. A fault poisons the
/// simulation: stop using that world. No performance or allocation-free guarantee is made.
/// </remarks>
public sealed class Box2DTaskScheduler : IDisposable
{
    private readonly object gate = new();
    private readonly BlockingCollection<NativeTask> requests = new();
    private readonly Dictionary<nint, NativeTask> tasks = new();
    private readonly TaskCompletionSource startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly B2EnqueueTaskCallback enqueue;
    private readonly B2FinishTaskCallback finish;
    private readonly Thread coordinator;
    private readonly ThreadLocal<uint> workerIndex;
    private ExceptionDispatchInfo? fault;
    private int nextWorkerIndex = -1;
    private long nextTaskId;
    private bool disposed;
    private readonly Action<DispatchPoint>? dispatchHook;
    private readonly Action<int>? rangeReturnedHook;
    private readonly Action? completingHook;
    internal enum DispatchPoint { AfterSchedule, AfterFlush }

    public int WorkerCount { get; }

    internal long ScheduledTaskCount { get { lock (gate) return nextTaskId; } }
    internal int ObservedWorkerCount => Volatile.Read(ref nextWorkerIndex) + 1;

    /// <summary>Creates an explicit number of physics worker threads and one scheduling coordinator.</summary>
    public Box2DTaskScheduler(int workerCount) : this(workerCount, null) { }

    // Internal fault hook runs only at known dispatch boundaries, never inside scheduler internals.
    internal unsafe Box2DTaskScheduler(int workerCount, Action<DispatchPoint>? dispatchHook,
        Action<int>? rangeReturnedHook = null, Action? completingHook = null)
    {
        this.dispatchHook = dispatchHook;
        this.rangeReturnedHook = rangeReturnedHook;
        this.completingHook = completingHook;
        // Box2D 3.x uses fixed-size per-worker storage; keep indices within its 64-worker limit.
        if (workerCount is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(workerCount));
        WorkerCount = workerCount;
        enqueue = Enqueue;
        finish = Finish;
        workerIndex = new(() => checked((uint)Interlocked.Increment(ref nextWorkerIndex)));
        coordinator = new Thread(Coordinate) { IsBackground = true, Name = "Box2D.Schedule" };
        try
        {
            coordinator.Start();
            startup.Task.GetAwaiter().GetResult();
        }
        catch
        {
            if (coordinator.IsAlive) coordinator.Join();
            workerIndex.Dispose();
            requests.Dispose();
            throw;
        }
    }

    /// <summary>Overwrites only the task-system fields. Do not configure a definition already in use.</summary>
    public unsafe void Configure(ref B2WorldDef definition)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ThrowIfFaulted();
            definition.WorkerCount = WorkerCount;
            definition.UserTaskContext = null; // Delegates close over this bridge; no unmanaged GCHandle.
            definition.EnqueueTaskCallback = enqueue;
            definition.FinishTaskCallback = finish;
        }
    }

    /// <summary>Waits for accepted work. Does not advance a world or release Box2D's task tokens.</summary>
    public void Drain()
    {
        NativeTask[] pending;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pending = tasks.Values.ToArray();
        }
        foreach (var task in pending) task.Complete();
        ThrowIfFaulted();
    }

    /// <summary>Rethrows the first captured callback/job failure. Faults are sticky.</summary>
    public void ThrowIfFaulted() => Volatile.Read(ref fault)?.Throw();

    private void Capture(Exception error) => Interlocked.CompareExchange(ref fault, ExceptionDispatchInfo.Capture(error), null);

    private void Coordinate()
    {
        JobScheduler? scheduler = null;
        try
        {
            var config = new JobScheduler.Config
            {
                ThreadCount = WorkerCount,
                ThreadPrefixName = "Box2D.Worker",
                StrictAllocationMode = false
            };
            scheduler = new JobScheduler(in config);
            startup.SetResult();
            foreach (var task in requests.GetConsumingEnumerable())
            {
                try { task.Schedule(scheduler); }
                catch (Exception error)
                {
                    InfrastructureFailure("Schedule may have partially accepted native work before all range handles were returned.", error);
                    throw;
                }
                bool flushed = false;
                try
                {
                    dispatchHook?.Invoke(DispatchPoint.AfterSchedule);
                    FlushOrFail(scheduler);
                    flushed = true;
                    dispatchHook?.Invoke(DispatchPoint.AfterFlush);
                    task.Dispatched.SetResult();
                }
                catch (Exception error)
                {
                    Capture(error);
                    // A hook failure leaves every range handle known. Publish the failure only
                    // after all handles drain: null tells Box2D that no work is active.
                    // FlushOrFail never returns after an actual Flush failure, so this is never a retry.
                    if (!flushed) FlushOrFail(scheduler);
                    task.CompleteHandles();
                    task.Dispatched.SetException(error);
                }
            }
            // Upstream Dispose cancels/clears queues (it does not join workers), so drain
            // every accepted handle first. No worker can access native owners after this barrier.
            NativeTask[] pending;
            lock (gate) pending = tasks.Values.ToArray();
            foreach (var task in pending) task.Complete();
        }
        catch (Exception error)
        {
            if (startup.Task.IsCompletedSuccessfully)
                InfrastructureFailure("Physics coordinator failed with accepted work of unknown completion state.", error);
            Capture(error);
            startup.TrySetException(error);
        }
        finally
        {
            try { scheduler?.Dispose(); }
            catch (Exception error) { Capture(error); }
        }
    }

    private static void FlushOrFail(JobScheduler scheduler)
    {
        try { scheduler.Flush(); }
        catch (Exception error)
        {
            // Flush enqueues one item at a time and only then clears its pending list. Retrying
            // after failure could enqueue the same native task twice; waiting could strand a tail.
            InfrastructureFailure("Physics Flush may have partially dispatched native work; it cannot safely be retried.", error);
        }
    }

    private static void CompleteOrFail(JobHandle handle)
    {
        try { handle.Complete(); }
        catch (Exception error)
        {
            InfrastructureFailure("Physics task completion failed; native task quiescence cannot be established.", error);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void InfrastructureFailure(string message, Exception error) =>
        Environment.FailFast(message, error);

    // Internal visibility lets tests call the exact reverse-P/Invoke entry points without a native world.
    internal unsafe void* Enqueue(nint taskPointer, int itemCount, int minRange, nint taskContext, nint userContext)
    {
        nint token = 0;
        NativeTask? accepted = null;
        try
        {
            if (itemCount == 0) return null;
            if (taskPointer == 0 || itemCount < 0 || minRange <= 0)
                throw new ArgumentOutOfRangeException(nameof(itemCount), "Invalid native task range or callback.");
            var callback = Marshal.GetDelegateForFunctionPointer<B2TaskCallback>(taskPointer);
            var task = new NativeTask(this, callback, itemCount, minRange, taskContext);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                token = checked((nint)(++nextTaskId));
                tasks.Add(token, task);
                requests.Add(task);
                accepted = task;
            }
            // Only scheduling is marshalled; execution is asynchronous on the scheduler workers.
            task.Dispatched.Task.GetAwaiter().GetResult();
            return (void*)token;
        }
        catch (Exception error)
        {
            Capture(error);
            accepted?.Complete(); // No accepted native callback may outlive a null completion result.
            if (token != 0) lock (gate) tasks.Remove(token);
            return null;
        }
    }

    internal void Finish(nint token, nint userContext)
    {
        try
        {
            NativeTask task;
            lock (gate)
                if (!tasks.TryGetValue(token, out task!))
                    throw new InvalidOperationException("Unknown or already finished Box2D task.");
            task.Complete();
            lock (gate) tasks.Remove(token);
        }
        catch (Exception error) { Capture(error); }
    }

    /// <summary>
    /// After configured worlds have stopped stepping, drains accepted handles then requests worker shutdown.
    /// Idempotent; call ThrowIfFaulted separately to report errors without hiding world cleanup.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            requests.CompleteAdding();
        }
        coordinator.Join();
        lock (gate) tasks.Clear();
        workerIndex.Dispose();
        requests.Dispose();
        GC.KeepAlive(enqueue);
        GC.KeepAlive(finish);
    }

    private sealed class NativeTask
    {
        private readonly Box2DTaskScheduler owner;
        private readonly B2TaskCallback callback;
        private readonly int itemCount;
        private readonly int rangeSize;
        private readonly nint context;
        private readonly object completionGate = new();
        private readonly NativeRange[] ranges;
        private readonly JobHandle[] handles;
        private bool completed;
        internal readonly TaskCompletionSource Dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RangeCount { get; }

        internal NativeTask(Box2DTaskScheduler owner, B2TaskCallback callback, int itemCount, int minRange, nint context)
        {
            this.owner = owner;
            this.callback = callback;
            this.itemCount = itemCount;
            this.context = context;
            rangeSize = (int)Math.Max(minRange, ((long)itemCount + owner.WorkerCount - 1) / owner.WorkerCount);
            RangeCount = (int)(((long)itemCount + rangeSize - 1) / rangeSize);
            // Bounded by WorkerCount, never itemCount. No new partitioning or scheduler.
            ranges = new NativeRange[RangeCount];
            handles = new JobHandle[RangeCount];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = new NativeRange(this, i);
        }

        internal void Schedule(JobScheduler scheduler)
        {
            // Plain IJob avoids 1.1.2's parallel-master subscriber-poll finalization stage.
            // Any exception here leaves partial acceptance unknown: caller must fail fast.
            for (int i = 0; i < ranges.Length; i++) handles[i] = scheduler.Schedule(ranges[i]);
        }

        private void Execute(int index)
        {
            try
            {
                uint worker = owner.workerIndex.Value;
                if (worker >= owner.WorkerCount) throw new InvalidOperationException("Scheduler exceeded its configured worker count.");
                int start = checked(index * rangeSize);
                int end = (int)Math.Min(itemCount, (long)start + rangeSize);
                callback(start, end, worker, context);
            }
            catch (Exception error) { owner.Capture(error); }
            // Tests can hold the managed postamble AFTER native return. This is not completion:
            // native token release must still wait for Execute return and handle publication.
            try { owner.rangeReturnedHook?.Invoke(index); }
            catch (Exception error) { owner.Capture(error); }
        }

        private sealed class NativeRange(NativeTask task, int index) : IJob
        {
            public void Execute() => task.Execute(index);
        }

        internal void Complete()
        {
            try { Dispatched.Task.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                // Dispatch faults are published only after a known handle has drained. An
                // interrupted wait is not evidence of quiescence and cannot be treated that way.
                if (!Dispatched.Task.IsFaulted)
                    InfrastructureFailure("Physics dispatch wait failed before completion was established.", error);
                owner.Capture(error);
                return;
            }
            CompleteHandles();
        }

        internal void CompleteHandles()
        {
            lock (completionGate)
            {
                if (completed) return;
                owner.completingHook?.Invoke();
                foreach (var handle in handles) CompleteOrFail(handle);
                // Drain/Finish/Dispose may revisit this task after scheduler pool reuse. Never
                // reinterpret retained handle storage as fresh work or re-wait a recycled job.
                completed = true;
            }
        }
    }
}
