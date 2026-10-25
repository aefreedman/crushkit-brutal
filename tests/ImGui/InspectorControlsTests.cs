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
