namespace CrushKit.Brutal.Vulkan;

/// <summary>Startup-only validated overlay settings. Paths are absolute and checked before native startup.
/// Font size, positions and dimensions are physical framebuffer pixels (not ImGui logical units).</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; }
    public string FontPath { get; }
    public string DebugFontPath { get; }
    public string ImagePath { get; }
    public float FontSize { get; }
    public float Left { get; }
    public float Top { get; }
    public float LineSpacing { get; }
    public int CacheEntries { get; }
    public int MaxLabelCharacters { get; }
    public int MaxLabels { get; }
    public int MaxTextureDimension { get; }
    public int MaxEncodedImageBytes { get; }
    public bool DebugVisible { get; }
    public float DebugLeft { get; }
    public float DebugTop { get; }
    public float DebugWidth { get; }
    public float DebugHeight { get; }
    public float DebugImageSize { get; }

    public OverlaySettings(bool enabled, string fontPath, string debugFontPath, string imagePath, float fontSize, float left, float top,
        float lineSpacing, int cacheEntries, int maxLabelCharacters, int maxLabels, int maxTextureDimension,
        int maxEncodedImageBytes, bool debugVisible, float debugLeft, float debugTop, float debugWidth,
        float debugHeight, float debugImageSize)
    {
        static float Range(float n, float min, float max, string name) => float.IsFinite(n) && n >= min && n <= max
            ? n : throw new ArgumentOutOfRangeException(name);
        static int Limit(int n, int min, int max, string name) => n >= min && n <= max ? n : throw new ArgumentOutOfRangeException(name);
        Enabled = enabled;
        FontPath = CheckFile(fontPath, true);
        DebugFontPath = CheckFile(debugFontPath, true);
        ImagePath = CheckFile(imagePath, false);
        FontSize = Range(fontSize, 1, 256, nameof(fontSize));
        Left = Range(left, 0, 65536, nameof(left)); Top = Range(top, 0, 65536, nameof(top));
        LineSpacing = Range(lineSpacing, 1, 1024, nameof(lineSpacing));
        MaxLabels = Limit(maxLabels, 1, 128, nameof(maxLabels));
        CacheEntries = Limit(cacheEntries, maxLabels, 4096, nameof(cacheEntries));
        MaxLabelCharacters = Limit(maxLabelCharacters, 1, 4096, nameof(maxLabelCharacters));
        MaxTextureDimension = Limit(maxTextureDimension, 1, 8192, nameof(maxTextureDimension));
        MaxEncodedImageBytes = Limit(maxEncodedImageBytes, 1, 67108864, nameof(maxEncodedImageBytes));
        if (new FileInfo(ImagePath).Length > MaxEncodedImageBytes) throw new ArgumentException("Image exceeds encoded byte limit.");
        using (var image = File.OpenRead(ImagePath))
        {
            Span<byte> header = stackalloc byte[24];
            image.ReadExactly(header);
            int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
            int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            if (!header[12..16].SequenceEqual("IHDR"u8) || w <= 0 || h <= 0 || w > MaxTextureDimension || h > MaxTextureDimension)
                throw new ArgumentException("PNG dimensions exceed overlay limits.");
        }
        DebugVisible = debugVisible;
        DebugLeft = Range(debugLeft, 0, 65536, nameof(debugLeft)); DebugTop = Range(debugTop, 0, 65536, nameof(debugTop));
        DebugWidth = Range(debugWidth, 1, 8192, nameof(debugWidth)); DebugHeight = Range(debugHeight, 1, 8192, nameof(debugHeight));
        DebugImageSize = Range(debugImageSize, 1, 8192, nameof(debugImageSize));
    }

    private static string CheckFile(string path, bool font)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("Overlay assets require absolute paths.");
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        if (file.Read(header) != header.Length || (font
            ? !(header[..4].SequenceEqual(new byte[] { 0, 1, 0, 0 }) || header[..4].SequenceEqual("OTTO"u8))
            : !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })))
            throw new ArgumentException(font ? "Expected a TrueType/OpenType font." : "Expected a PNG debug image.");
        return Path.GetFullPath(path);
    }
}
