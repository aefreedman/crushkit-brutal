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

Each bridge starts the requested fixed number of scheduler workers plus one lightweight coordination thread. The coordinator owns ZeroAllocJobScheduler because its published `Schedule` and `Flush` APIs require their construction thread. Native enqueue callbacks synchronously marshal scheduling requests to that coordinator; actual native task ranges execute asynchronously on scheduler workers. Native finish callbacks wait for the relevant job handle. There is no custom general-purpose job scheduler.

Ranges cover `[0, itemCount)` exactly once and respect native `minRange` as a partitioning hint. A thread-local mapping assigns stable native worker indices in `[0, WorkerCount)` to physical scheduler threads. Live native callback delegates are rooted fields. Per-task callback delegates, context and job handles remain rooted in an owned task map until finish. Opaque increasing integer tokens avoid dereferencing stale `GCHandle` pointers.

`Drain()` waits for accepted work and reports faults; it does not run a world step, release native task tokens or prevent future enqueues. Call it at an externally coordinated barrier, not concurrently with world stepping or disposal. `Dispose()` must likewise run only after configured worlds have stopped stepping and other calls have completed. It closes dispatch, drains accepted jobs, then stops workers and releases tokens/delegates. Disposal is idempotent. It does not throw stored job faults, so cleanup is not hidden by a prior failure; use `ThrowIfFaulted()` explicitly.

Ordinary application exceptions from job bodies, enqueue or finish are captured inside the bridge, not thrown through unmanaged callbacks or out of scheduler workers. `ThrowIfFaulted()` rethrows the first failure with its captured stack; faults are sticky. A fault means the world must no longer be simulated: a partially failed physics task cannot be rolled back. This is error containment, not a promise that native execution can recover from arbitrary corrupt pointers, native faults, allocation exhaustion or invalid concurrent world access. Do not call lifecycle methods from a physics callback.

Scheduler infrastructure failure is distinct and **process-fatal**. Published ZeroAllocJobScheduler 1.1.2 DLL IL shows that `Schedule(IJobParallelFor,...)` can enqueue multiple internal jobs before returning the aggregate handle. `Flush()` copies each queued item to its master queue before clearing the pending list, so a failure partway through cannot safely be retried. `JobHandle.Complete()` only subscribes/waits; it does not flush pending work. There is no public rollback or global quiescence API. If `Schedule` or `Flush` itself throws, or handle completion fails so quiescence is unknown, the bridge calls `Environment.FailFast` instead of returning null and falsely telling Box2D that native task work is finished. This is not used for ordinary managed callback failures.

An injected application-side dispatch failure at a known boundary after a handle exists is containable: flush once if not yet flushed, wait for the known handle, then fault dispatch and return null. The native task stays rooted until that barrier. Regression tests inject failures both before the first flush and after a successful flush, hold both worker callbacks active, and verify enqueue cannot return null until every callback has exited exactly once.

## Published API evidence and limits

Exact versions are locked in the module and test projects, sourced from `https://api.nuget.org/v3/index.json`.

| Capability | Published package/version | Verified API | Reuse / limitation |
| --- | --- | --- | --- |
| Native worker configuration | Ahwoo.BRUTAL.Box2D 2026.10.2, net10.0 | `B2WorldDef.WorkerCount`, set-only `EnqueueTaskCallback` / `FinishTaskCallback`, `void* UserTaskContext` | Reuse native physics. This pinned version does not create worker threads; callbacks are mandatory when changing worker count. |
| Native task delegate | Same | Cdecl `B2TaskCallback(int start, int end, uint worker, nint context)` | Marshal supplied task pointer using the binding's delegate; no alternate native binding. |
| Enqueue and finish | Same | `void* B2EnqueueTaskCallback(nint task, int count, int minRange, nint context, nint userContext)`; `void B2FinishTaskCallback(nint token, nint userContext)` | Thin affinity/lifetime adapter only. A null enqueue return denotes completed inline work under Box2D's contract; bridge callback failures are fatal to the world, not an inline fallback. |
| Parallel jobs | ZeroAllocJobScheduler 1.1.2, netstandard2.1 | `JobScheduler(in Config)`, `Schedule(IJobParallelFor, int, JobHandle?)`, `Flush()`, `JobHandle.Complete()`, `Dispose()` | Reuse scheduler. Published DLL inspection showed no automatic capture/rethrow of job-body exceptions, so bridge jobs capture their own errors. |

Before implementation, a disposable net10.0 probe using SDK 10.0.201 reflected the published DLLs and successfully compiled the exact callback/scheduler API calls. Native execution was not part of that API audit. Module tests subsequently exercise real native create/step/contact/destruction, serial versus 1/2/4-worker results, repeated lifetimes, concurrent independent worlds, GC pressure, range coverage, actual overlapping workers and captured managed failures. Synthetic managed callbacks are used for fault injection rather than deliberately corrupting a native simulation.

Run `dotnet restore tests/Physics/Physics.Tests.csproj --locked-mode`, then `dotnet test tests/Physics/Physics.Tests.csproj --no-restore`. Tests do not create windows or use a GPU. The measured contract is correctness on the tested Windows runtime, not a speedup, allocation-free stepping, cross-platform acceptance or recovery from native faults. Coordinate ECS and physics worker budgets at the application level; each bridge owns its own physics pool.
