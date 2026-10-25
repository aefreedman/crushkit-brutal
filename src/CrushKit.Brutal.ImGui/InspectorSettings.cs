namespace CrushKit.Brutal.ImGui;

/// <summary>Immutable optional inspector configuration. All supplied assets/limits validate before native startup.
/// Font size and initial layout are physical framebuffer pixels; no runtime defaults.
/// Diagnostics can subsequently be moved/resized; its session layout is retained in memory.</summary>
public sealed class InspectorSettings
{
    public string FontPath { get; }
    public string ImagePath { get; }
    public float FontSize { get; }
    public bool Visible { get; }
    public float Left { get; }
    public float Top { get; }
    public float Width { get; }
    public float Height { get; }
    public float ImageSize { get; }
    public int MaxTextureDimension { get; }
    public int MaxEncodedImageBytes { get; }

    public InspectorSettings(string fontPath, string imagePath, float fontSize, bool visible, float left, float top,
        float width, float height, float imageSize, int maxTextureDimension, int maxEncodedImageBytes)
    {
        static float Range(float n, float min, float max, string name) => float.IsFinite(n) && n >= min && n <= max
            ? n : throw new ArgumentOutOfRangeException(name);
        static int Limit(int n, int min, int max, string name) => n >= min && n <= max ? n : throw new ArgumentOutOfRangeException(name);
        FontPath = CheckFile(fontPath, true); ImagePath = CheckFile(imagePath, false);
        FontSize = Range(fontSize, 1, 256, nameof(fontSize)); Visible = visible;
        Left = Range(left, 0, 65536, nameof(left)); Top = Range(top, 0, 65536, nameof(top));
        Width = Range(width, 1, 8192, nameof(width)); Height = Range(height, 1, 8192, nameof(height));
        ImageSize = Range(imageSize, 1, 8192, nameof(imageSize));
        MaxTextureDimension = Limit(maxTextureDimension, 1, 8192, nameof(maxTextureDimension));
        MaxEncodedImageBytes = Limit(maxEncodedImageBytes, 1, 67108864, nameof(maxEncodedImageBytes));
        if (new FileInfo(ImagePath).Length > MaxEncodedImageBytes) throw new ArgumentException("Image exceeds encoded byte limit.");
        using var image = File.OpenRead(ImagePath);
        Span<byte> header = stackalloc byte[24]; image.ReadExactly(header);
        int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
        int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
        if (!header[12..16].SequenceEqual("IHDR"u8) || w <= 0 || h <= 0 || w > MaxTextureDimension || h > MaxTextureDimension)
            throw new ArgumentException("PNG dimensions exceed inspector limits.");
    }
    private static string CheckFile(string path, bool font)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Inspector assets require absolute paths.");
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (file.Read(header) != header.Length || (font
            ? !(header[..4].SequenceEqual(new byte[] { 0, 1, 0, 0 }) || header[..4].SequenceEqual("OTTO"u8))
            : !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new ArgumentException(font ? "Expected a TrueType/OpenType font." : "Expected a PNG debug image.");
        return Path.GetFullPath(path);
    }
}
