using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CrushKit.Brutal.ImGui;
using Xunit;
using Ui = Brutal.ImGuiApi.ImGui;

namespace CrushKit.Tests.ImGui;

public sealed class InspectorThemeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ExistingClrConstructorsRemainAndThemeInputsAreRetained()
    {
        Assert.NotNull(typeof(ImGuiInspector).GetConstructor([typeof(InspectorSettings), typeof(Action)]));
        Assert.NotNull(typeof(ImGuiInspector).GetConstructor([typeof(InspectorSettings), typeof(Action), typeof(FrameTimeHistory)]));
        var colors = Colors(); var metrics = Metrics(); var theme = new InspectorTheme(colors, metrics);
        using var inspector = new ImGuiInspector(Settings(), null, null, theme);
        Assert.Same(theme, inspector.Theme); Assert.Same(colors, theme.Colors); Assert.Same(metrics, theme.Metrics);
        using var existing = new ImGuiInspector(Settings());
        Assert.Null(existing.Theme);
        Assert.Throws<ArgumentNullException>(() => new InspectorTheme(null!, metrics));
        Assert.Throws<ArgumentNullException>(() => new InspectorTheme(colors, null!));
    }

    [Fact]
    public void EveryColorAndComponentIsValidatedAndCopiedByValue()
    {
        for (int token = 0; token < 12; token++)
            for (int component = 0; component < 4; component++)
                foreach (float bad in new[] { -0.01f, 1.01f, float.NaN, float.PositiveInfinity })
                {
                    var values = ColorValues(); var channels = new float[] { .4f, .4f, .4f, .8f };
                    channels[component] = bad; values[token] = new(channels[0], channels[1], channels[2], channels[3]);
                    Assert.Throws<ArgumentOutOfRangeException>(() => Colors(values));
                }
        var input = ColorValues(); var colors = Colors(input); var retained = colors.Text;
        input[0] = new float4(0); Assert.Equal(retained, colors.Text);
        var edge = ColorValues(); edge[0] = new(0, 1, 0, 1); Assert.Equal(edge[0], Colors(edge).Text);
    }

    [Fact]
    public void EveryMetricIsValidatedBeforeNativeUse()
    {
        for (int metric = 0; metric < 15; metric++)
            foreach (float bad in new[] { -1, 17, float.NaN, float.PositiveInfinity })
            {
                var values = MetricValues(); values[metric] = bad;
                Assert.Throws<ArgumentOutOfRangeException>(() => Metrics(values));
            }
        foreach (int metric in new[] { 12, 13 })
        {
            var values = MetricValues(); values[metric] = 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => Metrics(values));
        }
        foreach (float bad in new[] { -1, 1.01f, float.NaN, float.PositiveInfinity })
        {
            var values = MetricValues(); values[15] = bad;
            Assert.Throws<ArgumentOutOfRangeException>(() => Metrics(values));
        }
        var theme = new InspectorTheme(Colors(), Metrics());
        foreach (float bad in new[] { 0, -1, float.NaN, float.PositiveInfinity, float.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => theme.Apply(bad));
    }

    [Fact]
    public void NativeOperationsRequireACurrentContext()
    {
        var previous = Ui.GetCurrentContext();
        try
        {
            Ui.SetCurrentContext(default);
            var theme = new InspectorTheme(Colors(), Metrics());
            Assert.Throws<InvalidOperationException>(() => theme.Apply(22));
            Assert.Throws<InvalidOperationException>(() => { using var scope = theme.PushTone(InspectorTone.Error); });
        }
        finally { Ui.SetCurrentContext(previous); }
    }

    [Fact]
    public void ThemeAppliesAbsoluteDimensionsAndAllInteractionStatesWithoutChangingFontOrAlpha()
    {
        InContext(() =>
        {
            var theme = new InspectorTheme(Colors(), Metrics()); var c = theme.Colors; var m = theme.Metrics;
            Ui.GetStyle().FontSizeBase = 19; Ui.GetStyle().Alpha = .8f;
            foreach (float size in new[] { 22f, 11f, 22f })
            {
                theme.Apply(size); var s = Ui.GetStyle();
                Assert.Equal(m.WindowPaddingEm * size, s.WindowPadding);
                Assert.Equal(m.FramePaddingEm * size, s.FramePadding);
                Assert.Equal(m.ItemSpacingEm * size, s.ItemSpacing);
                Assert.Equal(m.InnerSpacingEm * size, s.ItemInnerSpacing);
                Assert.Equal(m.CellPaddingEm * size, s.CellPadding);
                Assert.Equal(m.RoundingEm * size, s.WindowRounding);
                Assert.Equal(s.WindowRounding, s.ChildRounding); Assert.Equal(s.WindowRounding, s.PopupRounding);
                Assert.Equal(s.WindowRounding, s.FrameRounding); Assert.Equal(s.WindowRounding, s.GrabRounding);
                Assert.Equal(s.GrabRounding, s.ScrollbarRounding);
                Assert.Equal(m.BorderEm * size, s.WindowBorderSize); Assert.Equal(s.WindowBorderSize, s.ChildBorderSize);
                Assert.Equal(s.WindowBorderSize, s.PopupBorderSize); Assert.Equal(s.WindowBorderSize, s.FrameBorderSize);
                Assert.Equal(m.ScrollbarEm * size, s.ScrollbarSize); Assert.Equal(m.GrabMinEm * size, s.GrabMinSize);
                Assert.Equal(m.IndentEm * size, s.IndentSpacing); Assert.Equal(m.DisabledAlpha, s.DisabledAlpha);
                Assert.Equal(19, s.FontSizeBase); Assert.Equal(.8f, s.Alpha);
                foreach (var (slot, expected) in new (ImGuiCol, float4)[]
                {
                    (ImGuiCol.Text,c.Text),(ImGuiCol.TextDisabled,c.MutedText),(ImGuiCol.WindowBg,c.Window),
                    (ImGuiCol.ChildBg,c.Window),(ImGuiCol.PopupBg,c.Popup),(ImGuiCol.Border,c.Border),
                    (ImGuiCol.FrameBg,c.Surface),(ImGuiCol.FrameBgHovered,c.AccentHovered),(ImGuiCol.FrameBgActive,c.AccentActive),
                    (ImGuiCol.TitleBg,c.Window),(ImGuiCol.TitleBgActive,c.Surface),(ImGuiCol.TitleBgCollapsed,c.Window),
                    (ImGuiCol.Header,c.Surface),(ImGuiCol.HeaderHovered,c.AccentHovered),(ImGuiCol.HeaderActive,c.AccentActive),
                    (ImGuiCol.Button,c.Accent),(ImGuiCol.ButtonHovered,c.AccentHovered),(ImGuiCol.ButtonActive,c.AccentActive),
                    (ImGuiCol.CheckMark,c.AccentActive),(ImGuiCol.SliderGrab,c.Accent),(ImGuiCol.SliderGrabActive,c.AccentActive),
                    (ImGuiCol.ScrollbarBg,c.Window),(ImGuiCol.ScrollbarGrab,c.Border),(ImGuiCol.ScrollbarGrabHovered,c.AccentHovered),
                    (ImGuiCol.ScrollbarGrabActive,c.AccentActive),(ImGuiCol.Separator,c.Border),(ImGuiCol.SeparatorHovered,c.AccentHovered),
                    (ImGuiCol.SeparatorActive,c.AccentActive),(ImGuiCol.ResizeGrip,c.Border),(ImGuiCol.ResizeGripHovered,c.AccentHovered),
                    (ImGuiCol.ResizeGripActive,c.AccentActive),(ImGuiCol.TextSelectedBg,c.Accent),(ImGuiCol.Tab,c.Surface),
                    (ImGuiCol.TabHovered,c.AccentHovered),(ImGuiCol.TabSelected,c.Accent),(ImGuiCol.TabDimmed,c.Window),
                    (ImGuiCol.TabDimmedSelected,c.Surface),(ImGuiCol.NavCursor,c.AccentActive),(ImGuiCol.TableHeaderBg,c.Surface),
                    (ImGuiCol.TableBorderStrong,c.Border),(ImGuiCol.TableBorderLight,c.Border),(ImGuiCol.PlotLines,c.Accent),
                    (ImGuiCol.PlotLinesHovered,c.AccentActive),(ImGuiCol.PlotHistogram,c.Accent),(ImGuiCol.PlotHistogramHovered,c.AccentActive)
                }) Assert.Equal(expected, s.Colors[(int)slot]);
            }
        });
    }

    [Fact]
    public void InspectorLeavesOptOutUntouchedAndReappliesOnlyForFontOrContextInvalidation()
    {
        var previous = Ui.GetCurrentContext();
        using var unthemed = new ImGuiInspector(Settings());
        using var themed = new ImGuiInspector(Settings(), null, null, new(Colors(), Metrics()));
        try
        {
            Attach(themed); Ui.GetStyle().WindowPadding = new float2(97);
            Apply(unthemed, 22); Assert.Equal(new float2(97), Ui.GetStyle().WindowPadding);
            Apply(themed, 22); Assert.Equal(themed.Theme!.Metrics.WindowPaddingEm * 22, Ui.GetStyle().WindowPadding);
            Ui.GetStyle().WindowPadding = new float2(97);
            Apply(themed, 22); Assert.Equal(new float2(97), Ui.GetStyle().WindowPadding); // No unchanged-frame style rewrite.
            Apply(themed, 11); Assert.Equal(themed.Theme.Metrics.WindowPaddingEm * 11, Ui.GetStyle().WindowPadding);
            Invoke(themed, "Release");
            Attach(themed); Ui.GetStyle().WindowPadding = new float2(97);
            Apply(themed, 11); Assert.Equal(themed.Theme.Metrics.WindowPaddingEm * 11, Ui.GetStyle().WindowPadding);
        }
        finally { themed.Dispose(); Ui.SetCurrentContext(previous); }
    }

    [Fact]
    public void SemanticTextScopesRestoreOnExceptionsNestAndRemainBalancedWhenDisabled()
    {
        InContext(() =>
        {
            var theme = new InspectorTheme(Colors(), Metrics()); theme.Apply(22);
            Ui.NewFrame(); Ui.Begin("Theme scopes"u8, ImGuiWindowFlags.NoSavedSettings);
            try
            {
                var original = Ui.GetStyle().Colors[(int)ImGuiCol.Text];
                Assert.Throws<InvalidOperationException>((Action)(() =>
                {
                    using var secondary = theme.PushTone(InspectorTone.Secondary);
                    Assert.Equal(theme.Colors.MutedText, Ui.GetStyle().Colors[(int)ImGuiCol.Text]);
                    using var error = theme.PushTone(InspectorTone.Error);
                    Assert.Equal(theme.Colors.Error, Ui.GetStyle().Colors[(int)ImGuiCol.Text]);
                    throw new InvalidOperationException("consumer failed");
                }));
                Assert.Equal(original, Ui.GetStyle().Colors[(int)ImGuiCol.Text]);
                foreach (var (tone, expected) in new[]
                {
                    (InspectorTone.Primary, theme.Colors.Text), (InspectorTone.Secondary, theme.Colors.MutedText),
                    (InspectorTone.Success, theme.Colors.Success), (InspectorTone.Warning, theme.Colors.Warning),
                    (InspectorTone.Error, theme.Colors.Error)
                })
                {
                    var scope = theme.PushTone(tone); Assert.Equal(expected, Ui.GetStyle().Colors[(int)ImGuiCol.Text]);
                    scope.Dispose(); scope.Dispose(); Assert.Equal(original, Ui.GetStyle().Colors[(int)ImGuiCol.Text]);
                }
                Assert.Throws<ArgumentOutOfRangeException>(() => { using var invalid = theme.PushTone((InspectorTone)99); });
                Ui.BeginDisabled();
                try { theme.TextWrapped("Disabled: unavailable while paused"u8, InspectorTone.Secondary); }
                finally { Ui.EndDisabled(); }
                theme.TextWrapped("Warning text stays explicit, not color alone"u8, InspectorTone.Warning);
                Assert.Equal(original, Ui.GetStyle().Colors[(int)ImGuiCol.Text]);
                Assert.Equal(1, Ui.GetStyle().Alpha);
            }
            finally { Ui.End(); Ui.EndFrame(); }
        });
    }

    private static InspectorThemeColors Colors(float4[]? values = null)
    {
        var v = values ?? ColorValues();
        return new(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11]);
    }
    private static float4[] ColorValues() => Enumerable.Range(0,12).Select(i => new float4(i / 12f,.3f,.6f,.9f)).ToArray();
    private static InspectorThemeMetrics Metrics(float[]? values = null)
    {
        var v = values ?? MetricValues();
        return new(new(v[0],v[1]),new(v[2],v[3]),new(v[4],v[5]),new(v[6],v[7]),new(v[8],v[9]),v[10],v[11],v[12],v[13],v[14],v[15]);
    }
    private static float[] MetricValues() => [.5f,.4f,.3f,.2f,.5f,.25f,.2f,.2f,.2f,.1f,.15f,.05f,.7f,.6f,1,.5f];
    private static InspectorSettings Settings() =>
        (InspectorSettings)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InspectorSettings));
    private static void Apply(ImGuiInspector inspector, float size) =>
        typeof(ImGuiInspector).GetMethod("ApplyTheme", Private)!.Invoke(inspector, [size]);
    private static void Invoke(ImGuiInspector inspector, string method) =>
        typeof(ImGuiInspector).GetMethod(method, Private)!.Invoke(inspector, null);
    private static void Attach(ImGuiInspector inspector)
    {
        var context = Ui.CreateContext();
        typeof(ImGuiInspector).GetField("_uiContext", Private)!.SetValue(inspector, context);
        typeof(ImGuiInspector).GetField("_hasUiContext", Private)!.SetValue(inspector, true);
    }
    private static void InContext(Action test)
    {
        var previous = Ui.GetCurrentContext(); var context = Ui.CreateContext();
        try
        {
            var io = Ui.GetIO(); io.IniFilename = default; io.LogFilename = default;
            io.DisplaySize = new float2(1000,800); io.DeltaTime = 1f / 60;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
            test();
        }
        finally { Ui.DestroyContext(context); Ui.SetCurrentContext(previous); }
    }
}
