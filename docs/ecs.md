# Arch world and job hosting

`CrushKit.Brutal.Ecs` provides generic ownership and execution plumbing, not a game ECS schema or a second scheduler. It references published `Arch` **2.1.0** and `ZeroAllocJobScheduler` **1.1.2**. The module has no graphics, input, audio, physics, JSON or game dependency.

Applications own components, systems/rules, physics adapters, input commands, snapshots, job dependencies and configuration defaults. Construct immutable settings explicitly before allocating a host:

```csharp
using CrushKit.Brutal.Ecs;

using var runtime = new ArchRuntime(new ArchRuntimeSettings(
    workerCount: 2, maxExpectedConcurrentJobs: 64));
runtime.Initialize(world =>
{
    // Create entities and register the component schema here.
});

await runtime.RunAsync(
    new WorldJob("Prepare physics", world => { /* exclusive native/world input */ }),
    new WorldJob("Gameplay", world => { /* ball/round data only */ }, 0),
    new WorldJob("Brick state", world => { /* brick data only */ }, 0),
    new WorldJob("Publish", world => { /* immutable application snapshot */ }, 1, 2));
```

Independent stages run on scheduler workers concurrently. The earlier-stage indexes are explicit dependencies, not inferred component-access declarations. The example allows stages 1 and 2 to overlap, then joins both before publication. `RunAsync` copies and validates the graph before queueing it, rejecting repeated dependency indexes as well as invalid/forward references. Submissions serialize in invocation order; separate runtime instances own separate worlds and schedulers. Callbacks are synchronous jobs; the returned task is the asynchronous completion of the whole graph, not permission to launch fire-and-forget work inside a callback.

## Safety and lifetime

- The caller must declare every conflicting access as a dependency. Concurrent stages must access disjoint mutable state (or explicitly synchronize their own shared data). There is no automatic borrow checker or race detector.
- Create/delete entities, add/remove components, and any operation changing archetypes or chunk storage only in an exclusive stage that runs after all readers/writers. Arch references/spans must not survive such changes. Check component presence for operations whose Arch API does not check it.
- Do not retain a world, component reference, native-event buffer or mutable snapshot outside its callback's valid scope. Publish owned immutable snapshots for consumers outside the graph.
- `Initialize` is an exclusive queued stage, also serialized across kit runtimes to protect initial use of Arch's global registries. Register component schemas there before concurrently executing worlds; the host does not make arbitrary global Arch registry mutation thread-safe.
- A thrown application-stage exception is captured rather than escaping the scheduler worker. Descendants are skipped; independent branches drain; the completion task faults with the original exception or an aggregate for multiple failures. There is no rollback. The application decides whether partial state permits another submission.
- `Dispose`/`DisposeAsync` stop acceptance, drain all accepted submissions, destroy the world and stop workers. Disposal does not cancel work and can wait indefinitely if application jobs never return. Ordinary callback failures are reported by their submission task, not hidden in disposal.
- Reentrant submission or disposal from this runtime's worker callback is rejected rather than deadlocking. Do not synchronously wait on mutually dependent runtimes from their callbacks.
- Settings bounds are resource validation, not a performance recommendation: 1..64 workers and 1..65,536 expected simultaneous scheduler handles. Capacity is an allocation budget, not a zero-allocation guarantee or submission-queue limit. The application must budget ECS, physics, rendering and other workers together.

## Published scheduling constraints

Reflection and compile probes against the published DLLs under pinned .NET SDK 10.0.201 verified:

- `Schedulers.JobScheduler.Schedule(IJob, JobHandle?)`, `CombineDependencies(ReadOnlySpan<JobHandle>)`, `Flush()` and `JobHandle.CompleteAll(ReadOnlySpan<JobHandle>)`.
- Schedule/flush require the scheduler's construction thread. This module therefore uses a dedicated coordinator thread for scheduler ownership, with real work dispatched onto its configured workers. `RunAsync` never relies on an arbitrary task continuation becoming the scheduler owner.
- Arch 2.1.0's `World.SharedJobScheduler` is **static**, not per-world. This host deliberately never assigns or disposes it. Do **not** call Arch's scheduler-backed `ParallelQuery` or `ScheduleInlineParallelChunkQuery` inside this host's jobs. Nested scheduling from a worker violates scheduler ownership, and swapping a global scheduler would interfere with other worlds. Ordinary `world.Query` calls in independent graph stages are supported.
- Scheduler worker execution does not capture user exceptions for its caller. The host wraps every stage and completes all graph handles before reporting failures.
- Published scheduler IL does not roll back partial `Schedule`/`CombineDependencies` submission. `Flush` enqueues items one at a time and only clears its pending list afterward; retrying after a partial failure could dispatch duplicates. Completion waits do not flush pending work. If the scheduler infrastructure itself throws during scheduling, flushing or completion, the host uses **process-fatal `Environment.FailFast`**, because no public API can prove that unknown jobs have stopped touching the world. It does not destroy the world or report an ordinary task fault while such jobs may still run. This narrow unrecoverable-infrastructure policy does not apply to application callback exceptions.

This establishes parallel graph-stage execution, not automatic parallelization of every query. A process-wide coordinator-owned Arch parallel-query integration would be a separate feature requiring explicit static ownership; it is not simulated by an unsafe per-session scheduler swap. Physics engines likewise retain their own version-specific access boundaries; this module does not make native world calls thread-safe.

## Validation and notices

`tests/Ecs` exercises real concurrent worker rendezvous, dependency joins, ordered submissions with Arch component mutation, failure/descendant skipping, branch draining, disposal, independent hosts, invalid input and one-worker graphs. It does not execute native physics or GPU code.

The dependency closure and upstream licence texts are propagated under `Licenses/CrushKit/Ecs` in build/publish output and inside the NuGet package. `ThirdParty/PROVENANCE.txt` records pinned upstream sources where published packages declare a licence expression without embedding its text. This is notice propagation, not publication or distribution clearance.
