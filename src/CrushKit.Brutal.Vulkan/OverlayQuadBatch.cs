using System.Runtime.InteropServices;
using Brutal;
using Brutal.Collections;
using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;

namespace CrushKit.Brutal.Vulkan;

/// <summary>Separate 32-byte UV material ABI; never changes the 24-byte ShapeVertex.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct OverlayVertex(float2 position, float2 uv, float4 color)
{
    public readonly float2 Position = position;
    public readonly float2 Uv = uv;
    public readonly float4 Color = color;
}

/// <summary>Physical-framebuffer-pixel quads in BRUTAL native storage, with renderer-specific UV/index glue.</summary>
public sealed class OverlayQuadBatch : IDisposable
{
    private readonly NativeStrideList _vertices = new(ByteSize.Of<OverlayVertex>());
    private readonly NativeStrideList _indices = new(ByteSize.Of<uint>());
    private bool _disposed;
    public ReadOnlySpan<OverlayVertex> Vertices => _vertices.AsSpan<OverlayVertex>();
    public ReadOnlySpan<uint> Indices => _indices.AsSpan<uint>();
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this); _vertices.Clear(); _indices.Clear();
    }

    public bool TryAdd(float left, float top, int width, int height, int framebufferWidth, int framebufferHeight, LinearColor tint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!float.IsFinite(left) || !float.IsFinite(top) || width <= 0 || height <= 0 || framebufferWidth <= 0 || framebufferHeight <= 0)
            throw new ArgumentException("Quad dimensions/coordinates must be finite and positive.");
        if (!float.IsFinite(tint.R) || !float.IsFinite(tint.G) || !float.IsFinite(tint.B) || !float.IsFinite(tint.A) || tint.A < 0 || tint.A > 1 ||
            tint.R < 0 || tint.G < 0 || tint.B < 0 || tint.R > 1 || tint.G > 1 || tint.B > 1) throw new ArgumentException("Expected straight-linear tint.");
        float right = left + width, bottom = top + height;
        float x0 = Math.Clamp(left, 0, framebufferWidth), y0 = Math.Clamp(top, 0, framebufferHeight);
        float x1 = Math.Clamp(right, 0, framebufferWidth), y1 = Math.Clamp(bottom, 0, framebufferHeight);
        if (x1 <= x0 || y1 <= y0 || tint.A <= 0) return false;
        // LinearColor's public contract is straight RGBA, like the shape producer; premultiply once at the vertex boundary.
        var color = new float4(tint.R * tint.A, tint.G * tint.A, tint.B * tint.A, tint.A);
        OverlayVertex Vertex(float x, float y) => new(new(x, y), new((x - left) / width, (y - top) / height), color);
        // Render.Mesh2026.10.2 AddQuad<T> emits a 24-index 2x2-cell grid for four vertices (verified defect).
        // This narrow six-index emission is intentional, not a fallback/repair of the library's output.
        uint first = checked((uint)Vertices.Length);
        ReadOnlySpan<OverlayVertex> quad = stackalloc OverlayVertex[] { Vertex(x0, y0), Vertex(x1, y0), Vertex(x0, y1), Vertex(x1, y1) };
        ReadOnlySpan<uint> indices = stackalloc uint[] { first, first + 1, first + 2, first + 2, first + 1, first + 3 };
        _vertices.AddRange(quad); _indices.AddRange(indices);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        ResourceLifetime.ReleaseAll(_vertices.Dispose, _indices.Dispose);
    }
}
