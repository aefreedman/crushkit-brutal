using CrushKit.Brutal.Draw2D;

namespace CrushKit.Brutal.Vulkan;

/// <summary>Game-owned presentation facts, no native/UI types. Labels use the host's current palette.</summary>
public sealed record OverlayLabel(string Text, LinearColor Color);
public sealed record OverlayDiagnostics(string State, string Detail, double FrameMilliseconds,
    int PresentedShapeFrames, long ShapeVertices, long ShapeIndices);
public sealed record OverlayFrame(IReadOnlyList<OverlayLabel> Labels, OverlayDiagnostics Diagnostics);

/// <summary>Reset held/edge input baselines on either capture transition; never replay suppressed held input.</summary>
public readonly record struct OverlayCapture(bool Mouse, bool Keyboard, bool ResetBaselines);
public sealed class OverlayCaptureTracker
{
    private bool _mouse, _keyboard, _visible;
    public OverlayCapture Update(bool inspectorVisible, bool mouse, bool keyboard)
    {
        mouse &= inspectorVisible; keyboard &= inspectorVisible;
        var result = new OverlayCapture(mouse, keyboard, mouse != _mouse || keyboard != _keyboard || inspectorVisible != _visible);
        _mouse = mouse; _keyboard = keyboard; _visible = inspectorVisible;
        return result;
    }
}

/// <summary>Counts only nonempty overlay submissions followed by successful presentation, independent of shape receipts.
/// TextFrames/LabelQuads count direct Vulkan game-label draws; DrawIndices includes those plus optional inspector indices.</summary>
public readonly record struct OverlayPresentationEvidence(int PresentedFrames, int TextFrames, int ImageFrames,
    int InspectorFrames, long LabelQuads, long DrawIndices, int Width, int Height, int SwapchainGeneration);
