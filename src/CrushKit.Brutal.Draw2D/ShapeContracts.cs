using Brutal.Numerics;
using System.Runtime.InteropServices;

namespace CrushKit.Brutal.Draw2D;

/// <summary>Input color is straight, linear RGBA. Every component must be finite and in [0, 1].</summary>
public readonly record struct LinearColor(float R, float G, float B, float A = 1)
{
    public static LinearColor FromSrgb(float r, float g, float b, float a = 1)
    {
        ValidateComponent(r); ValidateComponent(g); ValidateComponent(b); ValidateComponent(a);
        return new LinearColor(Decode(r), Decode(g), Decode(b), a);
    }

    internal void Validate()
    {
        ValidateComponent(R); ValidateComponent(G); ValidateComponent(B); ValidateComponent(A);
    }

    internal float4 Premultiplied => new(R * A, G * A, B * A, A);
    private static float Decode(float x) => x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    private static void ValidateComponent(float x)
    {
        if (!float.IsFinite(x) || x < 0 || x > 1)
            throw new ArgumentOutOfRangeException(nameof(x), "Color components must be finite and in [0, 1].");
    }
}

/// <summary>Half-open integer framebuffer rectangle. Origin is top-left, +Y down.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public bool IsEmpty => Right <= Left || Bottom <= Top;
}

/// <summary>GPU ABI: position at byte 0, premultiplied linear color at byte 8; stride exactly 24 bytes.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct ShapeVertex(float2 position, float4 color)
{
    public readonly float2 Position = position;
    public readonly float4 Color = color;
}

/// <summary>Ordered indexed triangle-list range. Indices address the shared vertex array without a base offset.</summary>
public readonly record struct ShapeCommand(int FirstIndex, int IndexCount, PixelRect Clip);

public enum LineCap { Butt, Square, Round }

/// <summary>
/// A finished frame's borrowed buffers, valid until its producer starts another frame.
/// Positions and scissors use physical framebuffer pixels, not logical window coordinates.
/// Consume synchronously on the render thread; do not retain across BeginFrame.
/// </summary>
public readonly struct ShapeFrame
{
    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<ShapeVertex> Vertices { get; }
    public ReadOnlyMemory<uint> Indices { get; }
    public ReadOnlyMemory<ShapeCommand> Commands { get; }

    internal ShapeFrame(int width, int height, ReadOnlyMemory<ShapeVertex> vertices,
        ReadOnlyMemory<uint> indices, ReadOnlyMemory<ShapeCommand> commands)
    {
        Width = width;
        Height = height;
        Vertices = vertices;
        Indices = indices;
        Commands = commands;
    }
}
