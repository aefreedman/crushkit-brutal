using Brutal.Box2DApi;
using CrushKit.Brutal.Physics;

using var jobs = new Box2DTaskScheduler(2);
var definition = B2WorldDef.Default;
definition.Gravity = new(0, -10);
jobs.Configure(ref definition);
var world = Box2DWorldLifetime.CreateWorld(in definition);
try
{
    var floorDefinition = B2BodyDef.Default;
    floorDefinition.Position = new(0, -.5f);
    var floor = world.CreateBody(in floorDefinition);
    var shape = B2ShapeDef.Default;
    var box = Box2D.MakeBox(20, .5f);
    floor.CreatePolygonShape(in shape, in box);
    var ballDefinition = B2BodyDef.Default;
    ballDefinition.Type = B2BodyType.Dynamic;
    ballDefinition.Position = new(0, 2);
    var ball = world.CreateBody(in ballDefinition);
    shape.Density = 1;
    shape.EnableContactEvents = true;
    var circle = new B2Circle { Radius = .2f };
    ball.CreateCircleShape(in shape, in circle);
    int contacts = 0;
    for (int tick = 0; tick < 240; tick++)
    {
        world.Step(1f / 120, 4);
        jobs.ThrowIfFaulted();
        contacts += world.GetContactEvents().BeginCount;
    }
    if (contacts == 0 || ball.GetPosition().Y < .18f)
        throw new InvalidOperationException("Native collision/job package contract failed.");
    Console.WriteLine("Physics jobs PASS: native collision preserved with configured Box2D task workers.");
}
finally { Box2DWorldLifetime.DestroyWorld(world); }
