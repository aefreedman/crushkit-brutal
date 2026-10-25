using System.Text;
using Ui = global::Brutal.ImGuiApi.ImGui;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace CrushKit.Brutal.ImGui;

/// <summary>Inspector uses its explicitly configured TTF, never the implicit pixel-style default.</summary>
public static class InspectorFont
{
    public static ImFontPtr Load(ImGuiIOPtr io, string path, float physicalPixels)
    {
        ValidateSize(physicalPixels);
        var font = io.Fonts.AddFontFromFileTTF(Encoding.UTF8.GetBytes(path), physicalPixels);
        if (font.IsNull()) throw new InvalidOperationException($"Could not load inspector font: {path}");
        io.FontDefault = font;
        // Sizes in overlay.json are physical pixels, not monitor-scaled logical units.
        // ImGui 1.92's RendererHasTextures backend handles framebuffer rasterizer density automatically.
        io.ConfigDpiScaleFonts = false;
        Ui.GetStyle().FontScaleMain = 1;
        Ui.GetStyle().FontScaleDpi = 1;
        return font;
    }

    public static float LogicalSize(float physicalPixels, float2 framebufferScale)
    {
        ValidateSize(physicalPixels);
        if (!float.IsFinite(framebufferScale.X) || !float.IsFinite(framebufferScale.Y) ||
            framebufferScale.X <= 0 || framebufferScale.Y <= 0)
            throw new ArgumentException("Expected a positive finite framebuffer scale.", nameof(framebufferScale));
        // Dear ImGui's native minimum is one logical pixel; report actual size in diagnostics.
        return Math.Max(1, physicalPixels / framebufferScale.Y);
    }

    private static void ValidateSize(float size)
    {
        if (!float.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
    }
}
