using Brutal.Numerics;

namespace CrushKit.Brutal.ImGui;

/// <summary>Immutable, explicitly supplied native ImGui straight-RGBA colors. Components are finite and
/// normalized; the kit performs no gamma conversion or premultiplication and does not choose game colors.</summary>
public sealed class InspectorThemeColors
{
    public float4 Text { get; }
    public float4 MutedText { get; }
    public float4 Window { get; }
    public float4 Surface { get; }
    public float4 Popup { get; }
    public float4 Border { get; }
    public float4 Accent { get; }
    public float4 AccentHovered { get; }
    public float4 AccentActive { get; }
    public float4 Success { get; }
    public float4 Warning { get; }
    public float4 Error { get; }

    public InspectorThemeColors(float4 text, float4 mutedText, float4 window, float4 surface,
        float4 popup, float4 border, float4 accent, float4 accentHovered, float4 accentActive,
        float4 success, float4 warning, float4 error)
    {
        Text = Color(text, nameof(text)); MutedText = Color(mutedText, nameof(mutedText));
        Window = Color(window, nameof(window)); Surface = Color(surface, nameof(surface));
        Popup = Color(popup, nameof(popup)); Border = Color(border, nameof(border));
        Accent = Color(accent, nameof(accent)); AccentHovered = Color(accentHovered, nameof(accentHovered));
        AccentActive = Color(accentActive, nameof(accentActive)); Success = Color(success, nameof(success));
        Warning = Color(warning, nameof(warning)); Error = Color(error, nameof(error));
    }

    private static float4 Color(float4 value, string name)
    {
        static bool Channel(float channel) => float.IsFinite(channel) && channel is >= 0 and <= 1;
        if (!Channel(value.X) || !Channel(value.Y) || !Channel(value.Z) || !Channel(value.W))
            throw new ArgumentOutOfRangeException(name, "Expected finite normalized native straight-RGBA components.");
        return value;
    }
}
