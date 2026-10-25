using System.Reflection;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using CrushKit.Brutal.ImGui;
using Xunit;
using Ui = Brutal.ImGuiApi.ImGui;

namespace CrushKit.Tests.ImGui;

// Native ImGui CPU frames only: no GLFW backend, Vulkan device, font files or windows.
public sealed class InspectorControlsTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ConstructorIsOptionalAndDoesNotInvokeControls()
    {
        int calls = 0;
        using var legacy = new ImGuiInspector(Settings());
        using var explicitNull = new ImGuiInspector(Settings(), null);
        var settings = Settings();
        using var controls = new ImGuiInspector(settings, () => calls++);
        Assert.Same(settings, controls.Settings);
        Assert.Equal(0, calls);
        Assert.Throws<ArgumentNullException>(() => new ImGuiInspector(null!, () => calls++));
        var parameter = typeof(ImGuiInspector).GetConstructors().Single().GetParameters()[1];
        Assert.Equal(typeof(Action), parameter.ParameterType);
        Assert.True(parameter.IsOptional);
        Assert.Null(parameter.DefaultValue);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ControlsRunInOpenFrameBeforeDiagnosticsAndCancelImmediatelyOnException(bool fail, bool omitControls)
    {
        var previous = Ui.GetCurrentContext();
        int calls = 0;
        bool diagnosticsEmitted = false;
        var expected = new InvalidOperationException("application UI failed");
        ImGuiInspector? callbackInspector = null;
        using var inspector = new ImGuiInspector(Settings(), omitControls ? null : () =>
        {
            calls++;
            Assert.False(diagnosticsEmitted);
            Assert.Equal(Field("_uiContext").GetValue(callbackInspector!), Ui.GetCurrentContext());
            Assert.True(Ui.GetCurrentContext().WithinFrameScope);
            Assert.True((bool)Field("_uiFrame").GetValue(callbackInspector!)!);
            Ui.Begin("Application controls"u8);
            try
            {
                Ui.Text("CPU controls"u8);
                if (fail) throw expected;
            }
            finally { Ui.End(); }
        });
        callbackInspector = inspector;
        try
        {
            AttachCpuFrame(inspector);
            if (fail)
            {
                var error = Assert.Throws<TargetInvocationException>(() => Invoke(inspector, "DrawControls"));
                Assert.Same(expected, error.InnerException);
                Assert.False(Ui.GetCurrentContext().WithinFrameScope);
                Assert.False((bool)Field("_uiFrame").GetValue(inspector)!);
            }
            else
            {
                Invoke(inspector, "DrawControls");
                // Callback does not render/end the frame: diagnostics are emitted later in Prepare.
                Assert.True(Ui.GetCurrentContext().WithinFrameScope);
                Assert.True((bool)Field("_uiFrame").GetValue(inspector)!);
                Ui.Begin("Diagnostics (read only)"u8);
                Ui.End();
                diagnosticsEmitted = true;
            }
            Assert.Equal(omitControls ? 0 : 1, calls);
            Invoke(inspector, "Cancel");
            Invoke(inspector, "Cancel");
            Assert.False(Ui.GetCurrentContext().WithinFrameScope);
            // Simulate the renderer's fence-protected Release following a failed BeginFrame.
            // There is no GPU work in this test, so this retirement boundary needs no fence.
            Invoke(inspector, "Release");
            AssertRetired(inspector);
        }
        finally
        {
            inspector.Dispose();
            Ui.SetCurrentContext(previous);
        }
    }

    [Fact]
    public void FirstBeginFailureRetiresContextAndNextVisibleBeginRecreatesIt()
    {
        var previous = Ui.GetCurrentContext();
        int calls = 0;
        using var inspector = new ImGuiInspector(Settings(), () =>
        {
            if (++calls == 1) throw new InvalidOperationException("first begin failed");
            Ui.Begin("Recreated controls"u8);
            Ui.End();
        });
        try
        {
            AttachCpuFrame(inspector);
            Assert.Throws<TargetInvocationException>(() => Invoke(inspector, "DrawControls"));
            Assert.False(Ui.GetCurrentContext().WithinFrameScope);
            // BeginOverlayFrame must release even though InspectorActive was never set.
            Invoke(inspector, "Release");
            AssertRetired(inspector); // Hidden next begin has no stranded context/platform callbacks.
            Invoke(inspector, "Cancel");
            Invoke(inspector, "Release");
            AttachCpuFrame(inspector); // CPU analogue of Ensure recreating context/backends.
            Invoke(inspector, "DrawControls");
            Assert.Equal(2, calls);
            Assert.True(Ui.GetCurrentContext().WithinFrameScope);
            inspector.Dispose();
            AssertRetired(inspector);
            inspector.Dispose();
        }
        finally
        {
            inspector.Dispose();
            Ui.SetCurrentContext(previous);
        }
    }

    [Fact]
    public void DiagnosticsAreMovableResizableAndRestoreClampedPhysicalBounds()
    {
        var previous = Ui.GetCurrentContext();
        using var inspector = new ImGuiInspector(Settings());
        Field("_hasDiagnosticsBounds").SetValue(inspector, true);
        Field("_diagnosticsPosition").SetValue(inspector, new float2(392, 16));
        Field("_diagnosticsSize").SetValue(inspector, new float2(440, 480));
        try
        {
            AttachCpuFrame(inspector);
            Ui.GetIO().DisplaySize = new float2(960, 540);
            BeginDiagnostics(inspector);
            var flags = Ui.GetCurrentContext().CurrentWindow.Flags;
            Assert.Equal(0, (int)(flags & (ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse)));
            Invoke(inspector, "EndDiagnostics");
            NextCpuFrame();
            // External geometry must not be overridden on an ordinary frame.
            Ui.SetNextWindowPos(new float2(100, 80)); Ui.SetNextWindowSize(new float2(300, 350));
            BeginDiagnostics(inspector);
            AssertBounds(100, 80, 300, 350);
            Invoke(inspector, "EndDiagnostics");
            Invoke(inspector, "Cancel"); Invoke(inspector, "Release");
            AttachCpuFrame(inspector);
            BeginDiagnostics(inspector);
            AssertBounds(100, 80, 300, 350);
            Invoke(inspector, "EndDiagnostics");
            NextCpuFrame();
            Ui.GetIO().DisplaySize = new float2(240, 180);
            Ui.GetIO().DisplayFramebufferScale = new float2(2, 2);
            BeginDiagnostics(inspector);
            AssertBounds(50, 5, 150, 175); // Same physical size, recovered bottom edge.
            Invoke(inspector, "EndDiagnostics");
            NextCpuFrame();
            Ui.GetIO().DisplaySize = new float2(120, 100);
            BeginDiagnostics(inspector);
            AssertBounds(0, 0, 120, 100);
            Invoke(inspector, "EndDiagnostics");
        }
        finally { inspector.Dispose(); Ui.SetCurrentContext(previous); }
    }

    [Fact]
    public void DiagnosticsSectionsAndCollapseSurviveRetirementAndNarrowTablesBalance()
    {
        var previous = Ui.GetCurrentContext();
        using var inspector = new ImGuiInspector(Settings());
        Field("_hasDiagnosticsBounds").SetValue(inspector, true);
        Field("_diagnosticsPosition").SetValue(inspector, new float2(0, 0));
        Field("_diagnosticsSize").SetValue(inspector, new float2(110, 300));
        Assert.True((bool)Field("_previewOpen").GetValue(inspector)!);
        foreach (var name in new[] { "_renderingOpen", "_textOpen", "_fontOpen" })
            Assert.False((bool)Field(name).GetValue(inspector)!);
        try
        {
            AttachCpuFrame(inspector);
            BeginDiagnostics(inspector);
            typeof(ImGuiInspector).GetMethod("DrawOverview", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [new CrushKit.Brutal.Vulkan.OverlayDiagnostics("Ready", "A long detail string that must wrap in a narrow value column without stranding a table scope.", 16.67, 1, 2, 3)]);
            // A second window after the table and diagnostics exercises stack balancing.
            Invoke(inspector, "EndDiagnostics");
            Ui.Begin("Independent controls"u8); Ui.End();
            Field("_previewOpen").SetValue(inspector, false);
            Field("_renderingOpen").SetValue(inspector, true);
            NextCpuFrame();
            Ui.SetNextWindowCollapsed(true);
            BeginDiagnostics(inspector);
            Assert.True(Ui.IsWindowCollapsed());
            Invoke(inspector, "EndDiagnostics");
            Invoke(inspector, "Cancel"); Invoke(inspector, "Release");
            AttachCpuFrame(inspector);
            BeginDiagnostics(inspector);
            Assert.True(Ui.IsWindowCollapsed());
            Assert.False((bool)Field("_previewOpen").GetValue(inspector)!);
            Assert.True((bool)Field("_renderingOpen").GetValue(inspector)!);
            Invoke(inspector, "EndDiagnostics");
            NextCpuFrame();
            Ui.SetNextWindowCollapsed(false);
            BeginDiagnostics(inspector);
            AssertBounds(0, 0, 110, 300);
            Assert.False(Section("Preview", false));
            Assert.True(Section("Rendering", true));
            Assert.False(Section("Font", false));
            Invoke(inspector, "EndDiagnostics");
        }
        finally { inspector.Dispose(); Ui.SetCurrentContext(previous); }
    }

    [Fact]
    public void ThrowingRowFormattingStillBalancesTableAndWindowScopes()
    {
        var previous = Ui.GetCurrentContext();
        using var inspector = new ImGuiInspector(Settings());
        Field("_hasDiagnosticsBounds").SetValue(inspector, true);
        Field("_diagnosticsSize").SetValue(inspector, new float2(160, 300));
        try
        {
            AttachCpuFrame(inspector);
            BeginDiagnostics(inspector);
            try
            {
                var error = Assert.Throws<TargetInvocationException>(() =>
                    typeof(ImGuiInspector).GetMethod("DrawOverview", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                        [new CrushKit.Brutal.Vulkan.OverlayDiagnostics("Ready", null!, 0, 0, 0, 0)]));
                Assert.IsType<ArgumentNullException>(error.InnerException);
                Assert.True(Ui.GetCurrentContext().CurrentTable.IsNull());
            }
            finally { Invoke(inspector, "EndDiagnostics"); }
            Ui.Begin("Controls after failed diagnostics"u8); Ui.Text("Still balanced"u8); Ui.End();
            Invoke(inspector, "Cancel");
            Assert.False(Ui.GetCurrentContext().WithinFrameScope);
        }
        finally { inspector.Dispose(); Ui.SetCurrentContext(previous); }
    }

    [Theory]
    [InlineData(960, 540)]
    [InlineData(1280, 720)]
    [InlineData(160, 400)]
    public void DefaultPreviewEmitsActualImageIndicesWithinAvailableBody(int width, int height)
    {
        var previous = Ui.GetCurrentContext();
        var settings = Settings();
        typeof(InspectorSettings).GetField("<ImageSize>k__BackingField", PrivateInstance)!.SetValue(settings, 256f);
        using var inspector = new ImGuiInspector(settings);
        Field("_hasDiagnosticsBounds").SetValue(inspector, true);
        Field("_diagnosticsPosition").SetValue(inspector, new float2(392, 16));
        Field("_diagnosticsSize").SetValue(inspector, new float2(440, 480));
        var pixels = global::Brutal.TextureApi.Abstractions.GenericTexture.Defaults.RGBA8UNorm(new int2(2, 2));
        pixels.Data.Fill(255);
        Field("_previewPixels").SetValue(inspector, pixels); // Inspector owns the CPU pixels from here.
        try
        {
            AttachCpuFrame(inspector);
            Ui.GetStyle().FontSizeBase = 22;
            Ui.GetIO().DisplaySize = new float2(width, height);
            NextCpuFrame();
            BeginDiagnostics(inspector);
            typeof(ImGuiInspector).GetMethod("DrawOverview", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [new CrushKit.Brutal.Vulkan.OverlayDiagnostics("Ready", "Inspector preview", 16.67, 1, 2, 3)]);
            Assert.True(Section("Preview", (bool)Field("_previewOpen").GetValue(inspector)!));
            var min = Ui.GetCursorScreenPos(); var available = Ui.GetContentRegionAvail();
            // A CPU-only stand-in for an already uploaded texture. No renderer/device is supplied.
            Field("_hasPreviewReference").SetValue(inspector, true);
            typeof(ImGuiInspector).GetMethod("DrawPreview", PrivateInstance)!.Invoke(inspector, [null]);
            Assert.True((bool)Field("_recordedImage").GetValue(inspector)!);
            var max = Ui.GetItemRectMax();
            Assert.InRange(max.X, min.X + 1, min.X + available.X + 0.01f);
            Assert.InRange(max.Y, min.Y + 1, min.Y + available.Y + 0.01f);
            // No available space => no upload attempt (null renderer would throw if attempted).
            Ui.Dummy(new float2(1, Math.Max(0, Ui.GetContentRegionAvail().Y)));
            Field("_hasPreviewReference").SetValue(inspector, false);
            Field("_recordedImage").SetValue(inspector, false);
            typeof(ImGuiInspector).GetMethod("DrawPreview", PrivateInstance)!.Invoke(inspector, [null]);
            Assert.False((bool)Field("_recordedImage").GetValue(inspector)!);
            Invoke(inspector, "EndDiagnostics");
        }
        finally
        {
            Field("_hasPreviewReference").SetValue(inspector, false);
            inspector.Dispose(); Ui.SetCurrentContext(previous);
        }
    }

    private static bool Section(string name, bool open)
    {
        object[] args = [name, open];
        return (bool)typeof(ImGuiInspector).GetMethod("Section", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    }

    private static void BeginDiagnostics(ImGuiInspector inspector) => Invoke(inspector, "BeginDiagnostics");
    private static void NextCpuFrame() { Ui.EndFrame(); Ui.NewFrame(); }
    private static void AssertBounds(float x, float y, float w, float h)
    {
        Assert.Equal(x, Ui.GetWindowPos().X, 2); Assert.Equal(y, Ui.GetWindowPos().Y, 2);
        Assert.Equal(w, Ui.GetWindowSize().X, 2); Assert.Equal(h, Ui.GetWindowSize().Y, 2);
    }

    private static InspectorSettings Settings() =>
        // No asset loading: settings validation is independent of callback/frame ownership.
        (InspectorSettings)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InspectorSettings));

    private static void AttachCpuFrame(ImGuiInspector inspector)
    {
        var context = Ui.CreateContext();
        Field("_uiContext").SetValue(inspector, context);
        Field("_hasUiContext").SetValue(inspector, true);
        var io = Ui.GetIO();
        io.IniFilename = default; io.LogFilename = default;
        io.DisplaySize = new float2(640, 480); io.DeltaTime = 1f / 60;
        // Dynamic font atlas support without a GPU backend; no texture upload is performed.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        Ui.NewFrame();
        Field("_uiFrame").SetValue(inspector, true);
    }

    private static void AssertRetired(ImGuiInspector inspector)
    {
        Assert.False((bool)Field("_uiFrame").GetValue(inspector)!);
        Assert.False((bool)Field("_hasUiContext").GetValue(inspector)!);
        Assert.Null(Field("_uiRenderer").GetValue(inspector));
        Assert.Null(Field("_uiPlatform").GetValue(inspector));
        Assert.True(Ui.GetCurrentContext().IsNull());
    }

    private static void Invoke(ImGuiInspector inspector, string method) =>
        typeof(ImGuiInspector).GetMethod(method, PrivateInstance)!.Invoke(inspector, null);

    private static FieldInfo Field(string name) => typeof(ImGuiInspector).GetField(name, PrivateInstance)!;
}
