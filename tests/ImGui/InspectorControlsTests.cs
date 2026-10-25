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
        // Constructor does not read assets or start native resources.
        var settings = (InspectorSettings)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InspectorSettings));
        using var legacy = new ImGuiInspector(settings);
        using var explicitNull = new ImGuiInspector(settings, null);
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
    public void CpuFrameInvokesOwnWindowBeforeRenderAndCancelsOnException(bool fail, bool omitControls)
    {
        var previous = Ui.GetCurrentContext();
        var context = Ui.CreateContext();
        try
        {
            var io = Ui.GetIO();
            io.IniFilename = default; io.LogFilename = default;
            io.DisplaySize = new float2(640, 480);
            io.DeltaTime = 1f / 60;
            // Dynamic font atlas support without a GPU backend; no texture upload is performed.
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
            int calls = 0;
            var expected = new InvalidOperationException("application UI failed");
            var settings = (InspectorSettings)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InspectorSettings));
            ImGuiInspector? callbackInspector = null;
            using var inspector = new ImGuiInspector(settings, omitControls ? null : () =>
            {
                calls++;
                Assert.Equal(context, Ui.GetCurrentContext());
                Assert.True(context.WithinFrameScope);
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
            Field("_uiContext").SetValue(inspector, context);
            // The test owns/destroys the CPU context; inspector owns frame cancellation only.
            Ui.NewFrame();
            Field("_uiFrame").SetValue(inspector, true);
            Ui.Begin("Diagnostics (read only)"u8);
            Ui.End();
            var complete = typeof(ImGuiInspector).GetMethod("CompleteUiFrame", PrivateInstance)!;
            if (fail)
            {
                var error = Assert.Throws<TargetInvocationException>(() => complete.Invoke(inspector, null));
                Assert.Same(expected, error.InnerException);
            }
            else
            {
                complete.Invoke(inspector, null);
                Assert.True(Ui.GetDrawData().Valid);
            }
            Assert.Equal(omitControls ? 0 : 1, calls);
            Assert.False(context.WithinFrameScope);
            Assert.Equal(context, Ui.GetCurrentContext());
            Assert.False((bool)Field("_uiFrame").GetValue(inspector)!);
            // Cancellation is idempotent, and the next frame is usable after either path.
            typeof(ImGuiInspector).GetMethod("Cancel", PrivateInstance)!.Invoke(inspector, null);
            Ui.NewFrame();
            Ui.EndFrame();
        }
        finally
        {
            Ui.DestroyContext(context);
            Ui.SetCurrentContext(previous);
        }
    }

    [Fact]
    public void CallbackFailureKeepsContextOwnedUntilInspectorDisposal()
    {
        var previous = Ui.GetCurrentContext();
        var context = Ui.CreateContext();
        var settings = (InspectorSettings)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(InspectorSettings));
        var inspector = new ImGuiInspector(settings, () => throw new InvalidOperationException("UI failure"));
        Field("_uiContext").SetValue(inspector, context);
        Field("_hasUiContext").SetValue(inspector, true);
        try
        {
            var io = Ui.GetIO();
            io.IniFilename = default; io.LogFilename = default;
            io.DisplaySize = new float2(640, 480); io.DeltaTime = 1f / 60;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
            Ui.NewFrame();
            Field("_uiFrame").SetValue(inspector, true);
            Assert.Throws<TargetInvocationException>(() =>
                typeof(ImGuiInspector).GetMethod("CompleteUiFrame", PrivateInstance)!.Invoke(inspector, null));
            Assert.False(context.WithinFrameScope);
            Assert.True((bool)Field("_hasUiContext").GetValue(inspector)!);
            inspector.Dispose();
            Assert.False((bool)Field("_hasUiContext").GetValue(inspector)!);
            Assert.True(Ui.GetCurrentContext().IsNull());
            inspector.Dispose(); // Idempotent, including after callback failure.
        }
        finally
        {
            inspector.Dispose();
            Ui.SetCurrentContext(previous);
        }
    }

    private static FieldInfo Field(string name) => typeof(ImGuiInspector).GetField(name, PrivateInstance)!;
}
