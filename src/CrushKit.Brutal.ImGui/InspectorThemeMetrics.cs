using Brutal.Numerics;

namespace CrushKit.Brutal.ImGui;

/// <summary>Immutable logical-font-relative style dimensions. Each em component is finite and in [0,16];
/// scrollbar/grab dimensions are positive. Native disabled alpha is supplied explicitly in [0,1].
/// These implementation bounds reject pathological dimensions before native startup.</summary>
public sealed class InspectorThemeMetrics
{
    public float2 WindowPaddingEm { get; }
    public float2 FramePaddingEm { get; }
    public float2 ItemSpacingEm { get; }
    public float2 InnerSpacingEm { get; }
    public float2 CellPaddingEm { get; }
    public float RoundingEm { get; }
    public float BorderEm { get; }
    public float ScrollbarEm { get; }
    public float GrabMinEm { get; }
    public float IndentEm { get; }
    public float DisabledAlpha { get; }

    public InspectorThemeMetrics(float2 windowPaddingEm, float2 framePaddingEm, float2 itemSpacingEm,
        float2 innerSpacingEm, float2 cellPaddingEm, float roundingEm, float borderEm,
        float scrollbarEm, float grabMinEm, float indentEm, float disabledAlpha)
    {
        WindowPaddingEm = Pair(windowPaddingEm, nameof(windowPaddingEm));
        FramePaddingEm = Pair(framePaddingEm, nameof(framePaddingEm));
        ItemSpacingEm = Pair(itemSpacingEm, nameof(itemSpacingEm));
        InnerSpacingEm = Pair(innerSpacingEm, nameof(innerSpacingEm));
        CellPaddingEm = Pair(cellPaddingEm, nameof(cellPaddingEm));
        RoundingEm = Dimension(roundingEm, nameof(roundingEm));
        BorderEm = Dimension(borderEm, nameof(borderEm));
        ScrollbarEm = Dimension(scrollbarEm, nameof(scrollbarEm), positive: true);
        GrabMinEm = Dimension(grabMinEm, nameof(grabMinEm), positive: true);
        IndentEm = Dimension(indentEm, nameof(indentEm));
        if (!float.IsFinite(disabledAlpha) || disabledAlpha is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(disabledAlpha));
        DisabledAlpha = disabledAlpha;
    }

    private static float2 Pair(float2 value, string name)
    {
        Dimension(value.X, name); Dimension(value.Y, name); return value;
    }

    private static float Dimension(float value, string name, bool positive = false)
    {
        if (!float.IsFinite(value) || value < 0 || value > 16 || (positive && value == 0))
            throw new ArgumentOutOfRangeException(name, "Expected a finite font-relative style dimension in range.");
        return value;
    }
}
