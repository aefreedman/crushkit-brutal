using Brutal.Box2DApi;

namespace CrushKit.Brutal.Physics;

/// <summary>Serializes Box2D world-slot creation/destruction across independent simulation coordinators.</summary>
/// <remarks>
/// All callers sharing Box2D's process-global world storage must use these helpers, including failure
/// cleanup. This gate does not protect stepping, queries, body operations or duplicate destruction:
/// the owner must serialize those operations for each world and destroy each world exactly once.
/// </remarks>
public static class Box2DWorldLifetime
{
    private static readonly object Gate = new();

    public static B2World CreateWorld(in B2WorldDef definition)
    {
        lock (Gate) return Box2D.CreateWorld(in definition);
    }

    public static void DestroyWorld(B2World world)
    {
        lock (Gate) world.Dispose();
    }
}
