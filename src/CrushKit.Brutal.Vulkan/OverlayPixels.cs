using System.Buffers.Binary;
using Brutal.TextureApi;
using Brutal.TextureApi.Abstractions;

namespace CrushKit.Brutal.Vulkan;

/// <summary>Game color policy over BRUTAL textures, not a second codec/storage abstraction.
/// Returned GenericTextures are caller-owned: Destroy exactly once, never Using/Unload.</summary>
public static class OverlayPixels
{
    private static double Linear(byte value)
    {
        var s = value / 255d;
        return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
    public static byte SrgbToLinear(byte value) => (byte)Math.Round(Linear(value) * 255);
    public static byte PremultiplySrgb(byte value, byte alpha) => (byte)Math.Round(Linear(value) * alpha);

    public static GenericTexture LoadPng(string path, int maxDimension, int maxEncodedBytes)
    {
        using var file = File.OpenRead(path);
        if (file.Length > maxEncodedBytes) throw new InvalidDataException("PNG exceeds encoded byte limit.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        return DecodePng(bytes, maxDimension, maxEncodedBytes);
    }

    public static GenericTexture DecodePng(ReadOnlySpan<byte> encoded, int maxDimension, int maxEncodedBytes)
    {
        if (encoded.Length > maxEncodedBytes || encoded.Length < 33 ||
            !encoded[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            !encoded.Slice(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("Expected a bounded PNG.");
        int width = BinaryPrimitives.ReadInt32BigEndian(encoded.Slice(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(encoded.Slice(20, 4));
        ValidateDimensions(width, height, maxDimension);
        ITexture decoded = TextureLoader.LoadFromMemory(encoded, TextureLoader.FormatType.Png,
            global::Brutal.TextureApi.Stb.Loader.LoadSettings.ForceRgba8);
        using var loadedLifetime = decoded.Using(); // Only loader-owned types support Unload/Using.
        ValidateRgba(decoded);
        if (decoded.Extent.X != width || decoded.Extent.Y != height) throw new InvalidDataException("Unexpected decoded PNG dimensions.");
        var result = new GenericTexture(decoded); // Actual owned copy; loader lifetime ends below.
        try
        {
            var data = result.ImageData(0, 0, 0); int pitch = checked((int)result.RowPitch(0));
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = y * pitch + x * 4; byte a = data[i + 3];
                    data[i] = PremultiplySrgb(data[i], a); data[i + 1] = PremultiplySrgb(data[i + 1], a); data[i + 2] = PremultiplySrgb(data[i + 2], a);
                }
            return result;
        }
        catch { result.Destroy(); throw; }
    }

    /// <summary>One derived straight-linear preview for stock ImGui SRC_ALPHA blending, never for game draws.
    /// Source PNG is decoded once; unpremultiplication can lose low-alpha byte precision.</summary>
    public static GenericTexture CreateInspectorPreview(ITexture premultiplied)
    {
        ValidateRgba(premultiplied);
        var result = new GenericTexture(premultiplied);
        try
        {
            var data = result.ImageData(0, 0, 0); int pitch = checked((int)result.RowPitch(0));
            for (int y = 0; y < result.Height; y++)
                for (int x = 0; x < result.Width; x++)
                {
                    int i = y * pitch + x * 4; byte a = data[i + 3];
                    for (int c = 0; c < 3; c++) data[i + c] = a == 0 ? (byte)0 : (byte)Math.Clamp((int)Math.Round(data[i + c] * 255d / a), 0, 255);
                }
            return result;
        }
        catch { result.Destroy(); throw; }
    }

    public static bool HasCoverage(ITexture texture)
    {
        ValidateRgba(texture);
        var data = texture.ImageData(0, 0, 0); int pitch = checked((int)texture.RowPitch(0));
        for (int y = 0; y < texture.Extent.Y; y++)
            for (int x = 0; x < texture.Extent.X; x++) if (data[y * pitch + x * 4 + 3] != 0) return true;
        return false;
    }

    public static void ValidateRgba(ITexture texture)
    {
        if (texture.Format != TextureFormat.R8G8B8A8_UNorm || texture.LevelCount != 1 || texture.LayerCount != 1 || texture.FaceCount != 1 ||
            texture.Extent.Z != 1 || texture.Extent.X <= 0 || texture.Extent.Y <= 0 || texture.RowPitch(0) < global::Brutal.ByteSize.Of(checked(texture.Extent.X * 4)))
            throw new InvalidDataException("Overlay requires one 2D RGBA8 UNORM image, with a valid row pitch.");
    }

    internal static void ValidateDimensions(int width, int height, int maxDimension)
    {
        if (maxDimension <= 0 || width <= 0 || height <= 0 || width > maxDimension || height > maxDimension)
            throw new InvalidDataException("Overlay texture exceeds configured dimensions.");
        _ = checked(width * height * 4);
    }
}
