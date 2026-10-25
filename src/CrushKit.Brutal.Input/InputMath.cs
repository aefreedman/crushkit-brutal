using Brutal.Numerics;

namespace CrushKit.Brutal.Input;

public static class InputMath
{
    public static float ValidateDeadzone(double deadzone)
    {
        float value = (float)deadzone;
        if (!double.IsFinite(deadzone) || deadzone < 0 || deadzone >= 1 ||
            !float.IsFinite(value) || value >= 1 || (deadzone > 0 && value == 0))
            throw new ArgumentOutOfRangeException(nameof(deadzone));
        return value;
    }
    /// <summary>Normalize beyond a radial deadzone, otherwise zero. No smoothing or aim-source policy.</summary>
    public static bool TryDirection(float2 value, float deadzone, out float2 direction)
    {
        ValidateDeadzone(deadzone);
        direction = default;
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y)) return false;
        double length = Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y);
        if (length <= deadzone) return false;
        direction = new((float)(value.X / length), (float)(value.Y / length));
        return true;
    }
}

/// <summary>Aggregate action edge across sources. New/reconnected sources baseline held input.
/// Pass inactive or Reset on focus/capture transitions. Bindings and transition policy belong to the app.</summary>
public sealed class ButtonEdges
{
    private readonly Dictionary<string, bool> previous = new(StringComparer.Ordinal);
    private bool initialized;
    public void Reset() { initialized = false; previous.Clear(); }
    public bool Update(bool active, IReadOnlyDictionary<string, bool> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (!active) { Reset(); return false; }
        bool held = false, wasHeld = false;
        foreach (var (source, down) in sources)
        {
            held |= down;
            // A newly appearing device's held button is not an action.
            wasHeld |= previous.TryGetValue(source, out bool old) ? old : down;
        }
        bool edge = initialized && held && !wasHeld;
        previous.Clear();
        foreach (var (source, down) in sources) previous.Add(source, down);
        initialized = true;
        return edge;
    }
}
