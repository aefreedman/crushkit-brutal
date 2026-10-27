using Brutal.ImGuiApi;
using Ui = global::Brutal.ImGuiApi.ImGui;

namespace CrushKit.Brutal.ImGui;

/// <summary>Semantic emphasis, not application state. Always communicate warnings/errors with text too.</summary>
public enum InspectorTone { Primary, Secondary, Success, Warning, Error }

/// <summary>Opt-in immutable style recipe. The application supplies/owns its colors and dimensions;
/// this object has no native lifetime, global singleton, game defaults or persistence.</summary>
public sealed class InspectorTheme
{
    public InspectorThemeColors Colors { get; }
    public InspectorThemeMetrics Metrics { get; }

    public InspectorTheme(InspectorThemeColors colors, InspectorThemeMetrics metrics)
    {
        Colors = colors ?? throw new ArgumentNullException(nameof(colors));
        Metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <summary>Applies absolute style dimensions to the current context, never cumulative scaling.
    /// The inspector invokes this only on context/font-size invalidation. An application-owned context
    /// may call it before NewFrame, outside active style scopes. This does not select fonts or alter color space.</summary>
    public void Apply(float logicalFontSize)
    {
        if (!float.IsFinite(logicalFontSize) || logicalFontSize <= 0 || logicalFontSize > float.MaxValue / 16)
            throw new ArgumentOutOfRangeException(nameof(logicalFontSize));
        RequireContext();
        var s = Ui.GetStyle(); var c = Colors; var m = Metrics;
        s.Colors[(int)ImGuiCol.Text] = c.Text;
        s.Colors[(int)ImGuiCol.TextDisabled] = c.MutedText;
        s.Colors[(int)ImGuiCol.WindowBg] = c.Window;
        s.Colors[(int)ImGuiCol.ChildBg] = c.Window;
        s.Colors[(int)ImGuiCol.PopupBg] = c.Popup;
        s.Colors[(int)ImGuiCol.Border] = c.Border;
        s.Colors[(int)ImGuiCol.FrameBg] = c.Surface;
        s.Colors[(int)ImGuiCol.FrameBgHovered] = c.AccentHovered;
        s.Colors[(int)ImGuiCol.FrameBgActive] = c.AccentActive;
        s.Colors[(int)ImGuiCol.TitleBg] = c.Window;
        s.Colors[(int)ImGuiCol.TitleBgActive] = c.Surface;
        s.Colors[(int)ImGuiCol.TitleBgCollapsed] = c.Window;
        s.Colors[(int)ImGuiCol.Header] = c.Surface;
        s.Colors[(int)ImGuiCol.HeaderHovered] = c.AccentHovered;
        s.Colors[(int)ImGuiCol.HeaderActive] = c.AccentActive;
        s.Colors[(int)ImGuiCol.Button] = c.Accent;
        s.Colors[(int)ImGuiCol.ButtonHovered] = c.AccentHovered;
        s.Colors[(int)ImGuiCol.ButtonActive] = c.AccentActive;
        s.Colors[(int)ImGuiCol.CheckMark] = c.AccentActive;
        s.Colors[(int)ImGuiCol.SliderGrab] = c.Accent;
        s.Colors[(int)ImGuiCol.SliderGrabActive] = c.AccentActive;
        s.Colors[(int)ImGuiCol.ScrollbarBg] = c.Window;
        s.Colors[(int)ImGuiCol.ScrollbarGrab] = c.Border;
        s.Colors[(int)ImGuiCol.ScrollbarGrabHovered] = c.AccentHovered;
        s.Colors[(int)ImGuiCol.ScrollbarGrabActive] = c.AccentActive;
        s.Colors[(int)ImGuiCol.Separator] = c.Border;
        s.Colors[(int)ImGuiCol.SeparatorHovered] = c.AccentHovered;
        s.Colors[(int)ImGuiCol.SeparatorActive] = c.AccentActive;
        s.Colors[(int)ImGuiCol.ResizeGrip] = c.Border;
        s.Colors[(int)ImGuiCol.ResizeGripHovered] = c.AccentHovered;
        s.Colors[(int)ImGuiCol.ResizeGripActive] = c.AccentActive;
        s.Colors[(int)ImGuiCol.TextSelectedBg] = c.Accent;
        s.Colors[(int)ImGuiCol.Tab] = c.Surface;
        s.Colors[(int)ImGuiCol.TabHovered] = c.AccentHovered;
        s.Colors[(int)ImGuiCol.TabSelected] = c.Accent;
        s.Colors[(int)ImGuiCol.TabDimmed] = c.Window;
        s.Colors[(int)ImGuiCol.TabDimmedSelected] = c.Surface;
        s.Colors[(int)ImGuiCol.NavCursor] = c.AccentActive;
        s.Colors[(int)ImGuiCol.TableHeaderBg] = c.Surface;
        s.Colors[(int)ImGuiCol.TableBorderStrong] = c.Border;
        s.Colors[(int)ImGuiCol.TableBorderLight] = c.Border;
        s.Colors[(int)ImGuiCol.PlotLines] = c.Accent;
        s.Colors[(int)ImGuiCol.PlotLinesHovered] = c.AccentActive;
        s.Colors[(int)ImGuiCol.PlotHistogram] = c.Accent;
        s.Colors[(int)ImGuiCol.PlotHistogramHovered] = c.AccentActive;
        s.WindowPadding = m.WindowPaddingEm * logicalFontSize;
        s.FramePadding = m.FramePaddingEm * logicalFontSize;
        s.ItemSpacing = m.ItemSpacingEm * logicalFontSize;
        s.ItemInnerSpacing = m.InnerSpacingEm * logicalFontSize;
        s.CellPadding = m.CellPaddingEm * logicalFontSize;
        s.WindowRounding = s.ChildRounding = s.PopupRounding = s.FrameRounding = m.RoundingEm * logicalFontSize;
        s.WindowBorderSize = s.ChildBorderSize = s.PopupBorderSize = s.FrameBorderSize = m.BorderEm * logicalFontSize;
        s.GrabRounding = s.ScrollbarRounding = m.RoundingEm * logicalFontSize;
        s.ScrollbarSize = m.ScrollbarEm * logicalFontSize;
        s.GrabMinSize = m.GrabMinEm * logicalFontSize;
        s.IndentSpacing = m.IndentEm * logicalFontSize;
        s.DisabledAlpha = m.DisabledAlpha;
    }

    /// <summary>Pushes one native text color. Dispose locally even when drawing throws; do not copy,
    /// retain, switch contexts or manually pop the returned borrowed scope.</summary>
    public InspectorToneScope PushTone(InspectorTone tone)
    {
        var color = tone switch
        {
            InspectorTone.Primary => Colors.Text,
            InspectorTone.Secondary => Colors.MutedText,
            InspectorTone.Success => Colors.Success,
            InspectorTone.Warning => Colors.Warning,
            InspectorTone.Error => Colors.Error,
            _ => throw new ArgumentOutOfRangeException(nameof(tone)),
        };
        RequireContext();
        Ui.PushStyleColor(ImGuiCol.Text, color);
        return new InspectorToneScope(active: true);
    }

    /// <summary>Wrapped UTF-8 text with semantic emphasis. Text is borrowed for this synchronous call.</summary>
    public void TextWrapped(ReadOnlySpan<byte> text, InspectorTone tone)
    {
        using var color = PushTone(tone);
        Ui.TextWrapped(text);
    }

    private static void RequireContext()
    {
        if (Ui.GetCurrentContext().IsNull()) throw new InvalidOperationException("An ImGui context must be current.");
    }
}

/// <summary>One borrowed native text-color scope. Valid only in its original current context.</summary>
public ref struct InspectorToneScope
{
    private bool _active;
    internal InspectorToneScope(bool active) { _active = active; }

    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        Ui.PopStyleColor();
    }
}
