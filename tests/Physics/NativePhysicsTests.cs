using Brutal.Box2DApi;
using Brutal.Numerics;
using CrushKit.Brutal.Physics;
using Xunit;

namespace Physics.Tests;

public sealed class NativePhysicsTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void ParallelNativeCollisionMatchesSerialSolver(int workers)
    {
        var serial = Simulate(0, 128, 240);
        var parallel = Simulate(workers, 128, 240);
        Assert.True(serial.Contacts > 0);
        Assert.True(parallel.Contacts > 0);
        for (int i = 0; i < serial.Positions.Length; i++)
        {
            Assert.InRange(Math.Abs(serial.Positions[i].X - parallel.Positions[i].X), 0, 1e-4f);
            Assert.InRange(Math.Abs(serial.Positions[i].Y - parallel.Positions[i].Y), 0, 1e-4f);
            Assert.True(float.IsFinite(parallel.Positions[i].X));
            Assert.True(float.IsFinite(parallel.Positions[i].Y));
            Assert.True(parallel.Positions[i].Y >= .18f, "Circle penetrated the floor.");
        }
        Assert.InRange(parallel.Positions[0].Y, .18f, .3f);
    }

    [Fact]
    public void RepeatedIndependentWorldAndBridgeLifetimeDoesNotLoseContacts()
    {
        for (int cycle = 0; cycle < 12; cycle++)
        {
            var result = Simulate(2, 32, 100);
            Assert.True(result.Contacts > 0);
            Assert.All(result.Positions, p => Assert.True(p.Y >= .18f));
        }
    }

    [Fact]
    public async Task SharedLifetimeGateAllowsIndependentWorldsOnDifferentCoordinators()
    {
        var runs = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int cycle = 0; cycle < 4; cycle++) Assert.True(Simulate(2, 32, 100).Contacts > 0);
        })).ToArray();
        await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(60));
    }

    private readonly record struct Result(float2[] Positions, int Contacts);

    private static unsafe Result Simulate(int workers, int count, int steps)
    {
        using var scheduler = workers > 0 ? new Box2DTaskScheduler(workers) : null;
        var definition = B2WorldDef.Default;
        definition.Gravity = new(0, -10);
        definition.EnableSleep = false;
        scheduler?.Configure(ref definition);
        var world = Box2DWorldLifetime.CreateWorld(in definition);
        try
        {
            var floorDefinition = B2BodyDef.Default;
            floorDefinition.Position = new(0, -.5f);
            var floor = world.CreateBody(in floorDefinition);
            var shape = B2ShapeDef.Default;
            var floorBox = Box2D.MakeBox(20, .5f);
            floor.CreatePolygonShape(in shape, in floorBox);
            var bodies = new B2Body[count];
            shape.Density = 1;
            shape.EnableContactEvents = true;
            var circle = new B2Circle { Radius = .2f };
            for (int i = 0; i < count; i++)
            {
                var bodyDefinition = B2BodyDef.Default;
                bodyDefinition.Type = B2BodyType.Dynamic;
                bodyDefinition.Position = new((i % 16 - 8) * .6f, .25f + i / 16 * .6f);
                bodyDefinition.EnableSleep = false;
                bodies[i] = world.CreateBody(in bodyDefinition);
                bodies[i].CreateCircleShape(in shape, in circle);
            }
            int contacts = 0;
            for (int i = 0; i < steps; i++)
            {
                world.Step(1f / 120, 4);
                scheduler?.ThrowIfFaulted();
                contacts += world.GetContactEvents().BeginCount;
                if (i % 20 == 0) GC.Collect(); // Exercise rooted callbacks across native-world use.
            }
            scheduler?.Drain();
            if (scheduler is not null)
            {
                Assert.True(scheduler.ScheduledTaskCount > 0, "Native world did not enqueue tasks.");
                Assert.InRange(scheduler.ObservedWorkerCount, 1, workers);
            }
            return new(bodies.Select(body => body.GetPosition()).ToArray(), contacts);
        }
        finally { Box2DWorldLifetime.DestroyWorld(world); }
    }
}
