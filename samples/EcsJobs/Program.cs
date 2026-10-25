using Arch.Core;
using CrushKit.Brutal.Ecs;

using var runtime = new ArchRuntime(new ArchRuntimeSettings(2, 64));
using var rendezvous = new Barrier(2);
Entity counter = default, marker = default;
runtime.Initialize(world =>
{
    counter = world.Create(new Counter());
    marker = world.Create(new Marker());
});
var threads = new int[2];
await runtime.RunAsync(
    new WorldJob("Counter", world =>
    {
        threads[0] = Environment.CurrentManagedThreadId;
        if (!rendezvous.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        world.Get<Counter>(counter).Value = 7;
    }),
    new WorldJob("Marker", world =>
    {
        threads[1] = Environment.CurrentManagedThreadId;
        if (!rendezvous.SignalAndWait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        world.Get<Marker>(marker).Value = 11;
    }),
    new WorldJob("Join", world =>
    {
        if (world.Get<Counter>(counter).Value != 7 || world.Get<Marker>(marker).Value != 11 || threads[0] == threads[1])
            throw new InvalidOperationException("Parallel stage contract failed.");
    }, 0, 1));
Console.WriteLine("ECS jobs PASS: independent Arch component stages ran on distinct workers; join observed both.");

internal struct Counter { public int Value; }
internal struct Marker { public int Value; }
