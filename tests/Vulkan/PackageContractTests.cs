using CrushKit.Brutal.Vulkan;
using Xunit;

namespace CrushKit.Tests.Vulkan;

public sealed class PackageContractTests
{
    [Fact]
    public void RendererOwnsItsShadersAndDoesNotReferenceBreakoutOrImGui()
    {
        var assembly = typeof(VulkanRenderer).Assembly;
        foreach (var name in new[] { "shapes.vert", "shapes.frag", "overlay.vert", "overlay.frag" })
            Assert.Contains("CrushKit.Brutal.Vulkan.Shaders." + name, assembly.GetManifestResourceNames());
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.Contains("Breakout", StringComparison.OrdinalIgnoreCase)
            || a.Name.Contains("ImGui", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InspectorRetiresAtSwapchainCleanupBeforeFinalDisposal()
    {
        // Constructor only: no native initialization, window, device or GPU call.
        using var renderer = (VulkanRenderer)Activator.CreateInstance(typeof(VulkanRenderer),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new object[] { default(global::Brutal.GlfwApi.GlfwWindow)! }, null)!;
        var events = new List<string>();
        var inspector = new FakeInspector(events);
        typeof(VulkanRenderer).GetField("_inspector", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(renderer, inspector);
        typeof(VulkanRenderer).GetMethod("DisposeOverlay", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(renderer, null);
        Assert.Equal(new[] { "release", "dispose" }, events);
    }

    private sealed class FakeInspector(List<string> events) : VulkanInspector
    {
        internal override bool InitiallyVisible => false;
        internal override OverlayCapture BeginFrame(VulkanRenderer renderer) => default;
        internal override InspectorEvidence Prepare(VulkanRenderer renderer, OverlayFrame frame, OverlayText text) => default;
        internal override void Record(VulkanRenderer renderer) { }
        internal override void Cancel() { }
        internal override void Release() => events.Add("release");
        public override void Dispose() => events.Add("dispose");
    }

    [Fact]
    public void CleanupAttemptsEveryReleaseAndRetainsBothFailures()
    {
        bool released = false;
        var original = new InvalidOperationException("original");
        var cleanup = new IOException("cleanup");
        var error = Assert.Throws<AggregateException>(() => ResourceLifetime.OnFailure(() => throw original,
            () => ResourceLifetime.ReleaseAll(() => throw cleanup, () => released = true)));
        Assert.True(released);
        Assert.Contains(original, error.InnerExceptions);
        Assert.Contains(cleanup, error.Flatten().InnerExceptions);
    }
}
