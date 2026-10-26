# Box2D physics jobs

`CrushKit.Brutal.Physics` connects the published BRUTAL Box2D binding to the published ZeroAllocJobScheduler. It does not replace collision detection, integration or the Box2D solver, and does not require Arch, Vulkan or game-specific settings.

## Ownership and use

```csharp
using Brutal.Box2DApi;
using CrushKit.Brutal.Physics;

using var jobs = new Box2DTaskScheduler(workerCount); // Explicit integer, 1..64.
var definition = B2WorldDef.Default;
jobs.Configure(ref definition);
var world = Box2DWorldLifetime.CreateWorld(in definition);
try
{
    // Create native bodies/shapes using the published binding.
    world.Step(stepSeconds, substeps);
    jobs.ThrowIfFaulted();
    // Consume/copy contact events before changing bodies or starting another step.
}
finally
{
    Box2DWorldLifetime.DestroyWorld(world);
} // Dispose the jobs only after the world and its last step have finished.
```

The host supplies all world tuning and decides when to step. `Configure` changes only worker count, enqueue/finish callbacks and task user context. Keep the bridge strongly reachable for the entire lifetime of every configured native world; copying a world definition does not transfer ownership of the bridge. Do not overwrite its callback fields afterward. Use one bridge per independently stepping world; sharing one pool between simultaneously stepping worlds is not established by these tests.

`Box2DWorldLifetime.CreateWorld` and `DestroyWorld` share a process-local gate for Box2D's global world slots. All creation/destruction paths, including failure cleanup, must use the helpers to receive that protection. The gate does not protect arbitrary direct binding calls, stepping, bodies, queries or duplicate destruction. Destroy each world exactly once.

A world need not remain on its construction thread. The host must serialize its operations: never read or modify it during its step, and never concurrently write to it. Schedule independent work against owned snapshots instead. Finish outstanding work before native destruction. This adapter does not impose a main-thread game loop or prohibit asynchronous application orchestration.

## Scheduling and shutdown

Each bridge starts the requested fixed number of scheduler workers plus one lightweight coordination thread. The coordinator owns ZeroAllocJobScheduler because its published `Schedule` and `Flush` APIs require their construction thread. Native enqueue callbacks synchronously marshal scheduling requests to that coordinator; actual native task ranges execute asynchronously on scheduler workers. Native finish callbacks wait for every range's scheduler job handle. There is no custom general-purpose job scheduler.

Ranges cover `[0, itemCount)` exactly once and respect native `minRange` as a partitioning hint. Each existing range is a plain `IJob`, scheduled on the coordinator with one `Flush` after all ranges are accepted. A thread-local mapping assigns stable native worker indices in `[0, WorkerCount)` to physical scheduler threads. Live native callback delegates are rooted fields. Per-task callback delegates, context, immutable range wrappers and handles remain rooted in an owned task map until finish. Opaque increasing integer tokens avoid dereferencing stale `GCHandle` pointers.

The hot path is per native enqueue, not per ball: partitioning is unchanged and creates at most `WorkerCount` wrappers and handles (two bounded arrays per accepted task). Scheduling and completion scale with that range count, not world population. There is no new pool, polling loop, event, inline-worker path or general scheduler. Completion is recorded once under a private task gate only after **all** handles complete; later `Drain`/finish/disposal visits do not wait again on recycled scheduler jobs.

`Drain()` waits for accepted work and reports faults; it does not run a world step, release native task tokens or prevent future enqueues. Call it at an externally coordinated barrier, not concurrently with world stepping or disposal. `Dispose()` must likewise run only after configured worlds have stopped stepping and other calls have completed. It closes dispatch, drains all accepted handles, requests upstream scheduler shutdown, joins the coordinator and releases tokens/delegates. Upstream `Dispose` cancels workers and clears queues; it does **not** join worker threads. Coordinator join is not worker join. After the all-handle barrier, every range's managed `Execute` has returned: no remaining scheduler postamble can invoke that range or access native pointers, task context, delegates, bridge worker-index storage or capture another callback fault. Scheduler-owned references root wrappers/tasks while finalizing/pooling; worker-loop shutdown may finish later without native-owner access. Disposal is idempotent. It does not throw stored job faults, so cleanup is not hidden by a prior failure; use `ThrowIfFaulted()` explicitly.

Ordinary application exceptions from job bodies, enqueue or finish are captured inside the bridge, not thrown through unmanaged callbacks or out of scheduler workers. `ThrowIfFaulted()` rethrows the first failure with its captured stack; faults are sticky. A fault means the world must no longer be simulated: a partially failed physics task cannot be rolled back. This is error containment, not a promise that native execution can recover from arbitrary corrupt pointers, native faults, allocation exhaustion or invalid concurrent world access. Do not call lifecycle methods from a physics callback.

Scheduler infrastructure failure is distinct and **process-fatal**. Published ZeroAllocJobScheduler 1.1.2 DLL IL shows that scheduling can accept work before returning its handle; the bridge's loop can likewise partially accept ranges before all handles are known. `Flush()` copies each queued item to its master queue before clearing the pending list, so a failure partway through cannot safely be retried. `JobHandle.Complete()` only subscribes/waits; it does not flush pending work. There is no public rollback or global quiescence API. If `Schedule` or `Flush` itself throws, or handle completion fails so quiescence is unknown, the bridge calls `Environment.FailFast` instead of returning null and falsely telling Box2D that native task work is finished. This is not used for ordinary managed callback failures.

An injected application-side dispatch failure at a known boundary after **all** range handles exist is containable: flush once if not yet flushed, wait for every known handle, then fault dispatch and return null. The native task stays rooted until that barrier. Regression tests inject failures before and after the single flush, hold both native callbacks and separately each managed postamble, and verify enqueue cannot return null until every range has exited exactly once.

### Completion-publication evidence

The exact consumed 1.1.2 `Schedulers.dll` (SHA256 `3C750E5DF775F6F5AA628E168248BEE71644696FB0FC0360CAD65E9547178231`) has a distinct parallel-master finalization stage: `Job.Execute` unsubscribes parallel work, polls volatile `_parallelSubscribers` with `SpinWait.SpinOnce`, then calls `IJobParallelFor.Finish`, sets `_isComplete` under `_jobLock` and signals subscribed waiters. The native delegate has already returned before this stage. Moving a signal to parallel `Finish` would not bypass it. Plain `IJob.Execute` skips that parallel stage and uses the same synchronized handle publication/wait protocol; the bridge reuses that existing capability rather than introducing a weaker native-callback-only barrier.

`JobHandle.Complete` checks the handle version and completion under the job lock before subscribing and waiting on a `ManualResetEvent`. Publication uses that same lock; this audit did not demonstrate a lost wakeup. Pooling increments the version, clears work references and resets the event. Old handles cannot subscribe to a new version; the bridge additionally makes its all-handle completion idempotent. Native callback quiescence, managed range return, handle publication, token release and worker-thread exit are different boundaries. Only the all-handle completion boundary permits token release here.

Earlier two-range measurements localized long waits after the last native delegate return. The IL proves an un-signaled subscriber-poll stage exists in that interval, not that a particular OS sleep, race or descheduling caused those observations. This change removes that stage from bridge dispatch; no portable latency/speedup claim follows without a new equivalent package-level comparison. Deterministic tests force wait-before-completion, completion-before-repeat-wait, both range exit orders, exact coverage, exclusive indices, sticky faults and drain-before-release without microbenchmark thresholds.

## Published API evidence and limits

Exact versions are locked in the module and test projects, sourced from `https://api.nuget.org/v3/index.json`.

| Capability | Published package/version | Verified API | Reuse / limitation |
| --- | --- | --- | --- |
| Native worker configuration | Ahwoo.BRUTAL.Box2D 2026.10.2, net10.0 | `B2WorldDef.WorkerCount`, set-only `EnqueueTaskCallback` / `FinishTaskCallback`, `void* UserTaskContext` | Reuse native physics. This pinned version does not create worker threads; callbacks are mandatory when changing worker count. |
| Native task delegate | Same | Cdecl `B2TaskCallback(int start, int end, uint worker, nint context)` | Marshal supplied task pointer using the binding's delegate; no alternate native binding. |
| Enqueue and finish | Same | `void* B2EnqueueTaskCallback(nint task, int count, int minRange, nint context, nint userContext)`; `void B2FinishTaskCallback(nint token, nint userContext)` | Thin affinity/lifetime adapter only. A null enqueue return denotes completed inline work under Box2D's contract; bridge callback failures are fatal to the world, not an inline fallback. |
| Parallel jobs | ZeroAllocJobScheduler 1.1.2, netstandard2.1 | `JobScheduler(in Config)`, `IJob.Execute()`, `Schedule(IJob, JobHandle?)`, `Schedule(IJobParallelFor, int, JobHandle?)`, `Flush()`, `JobHandle.Complete()` / `CompleteAll(...)`, `Dispose()` | Reuse plain jobs and existing handle publication. Parallel master finalization polls subscribers; parallel `Finish` occurs afterward. No rollback/global-quiescence/worker-join API was found. No automatic capture/rethrow of job-body exceptions: bridge jobs capture errors before Execute returns. |

Before implementation, a disposable net10.0 probe using SDK 10.0.201 reflected the published DLLs and successfully compiled the exact callback/scheduler API calls. Native execution was not part of that API audit. Module tests subsequently exercise real native create/step/contact/destruction, serial versus 1/2/4-worker results, repeated lifetimes, concurrent independent worlds, GC pressure, range coverage, actual overlapping workers and captured managed failures. Synthetic managed callbacks are used for fault injection rather than deliberately corrupting a native simulation.

Run `dotnet restore tests/Physics/Physics.Tests.csproj --locked-mode`, then `dotnet test tests/Physics/Physics.Tests.csproj --no-restore`. Tests do not create windows or use a GPU. The measured contract is correctness on the tested Windows runtime, not a speedup, allocation-free stepping, cross-platform acceptance or recovery from native faults. Coordinate ECS and physics worker budgets at the application level; each bridge owns its own physics pool.
