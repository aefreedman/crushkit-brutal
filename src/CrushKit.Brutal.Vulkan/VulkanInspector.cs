using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("CrushKit.Brutal.ImGui")]
[assembly: InternalsVisibleTo("Vulkan.Tests")]

namespace CrushKit.Brutal.Vulkan;

/// <summary>Optional kit-provided inspector. Ownership transfers to the renderer at configuration.
/// Runs synchronously on the renderer thread; resources retire before render-pass destruction.</summary>
public abstract class VulkanInspector : IDisposable
{
    internal VulkanInspector() { }
    internal abstract OverlayCapture BeginFrame(VulkanRenderer renderer, OverlaySettings settings);
    internal abstract InspectorEvidence Prepare(VulkanRenderer renderer, OverlaySettings settings, OverlayFrame frame, OverlayText text);
    internal abstract void Record(VulkanRenderer renderer);
    internal abstract void Cancel();
    internal abstract void Release();
    public abstract void Dispose();
}

internal readonly record struct InspectorEvidence(int Indices, bool Image, bool Window);
