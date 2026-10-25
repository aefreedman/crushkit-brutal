namespace CrushKit.Brutal.Vulkan;

/// <summary>Immutable text-overlay settings, validated before native startup.
/// No inspector font, image or layout is required. Coordinates/font size are physical framebuffer pixels.</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; }
    public string FontPath { get; }
    public float FontSize { get; }
    public float Left { get; }
    public float Top { get; }
    public float LineSpacing { get; }
    public int CacheEntries { get; }
    public int MaxLabelCharacters { get; }
    public int MaxLabels { get; }
    public int MaxTextureDimension { get; }

    public OverlaySettings(bool enabled, string fontPath, float fontSize, float left, float top,
        float lineSpacing, int cacheEntries, int maxLabelCharacters, int maxLabels, int maxTextureDimension)
    {
        static float Range(float n, float min, float max, string name) => float.IsFinite(n) && n >= min && n <= max
            ? n : throw new ArgumentOutOfRangeException(name);
        static int Limit(int n, int min, int max, string name) => n >= min && n <= max ? n : throw new ArgumentOutOfRangeException(name);
        Enabled = enabled;
        if (string.IsNullOrWhiteSpace(fontPath) || !Path.IsPathFullyQualified(fontPath))
            throw new ArgumentException("Text font requires an absolute path.", nameof(fontPath));
        using (var file = File.OpenRead(fontPath))
        {
            Span<byte> header = stackalloc byte[8];
            file.ReadExactly(header);
            if (!(header[..4].SequenceEqual(new byte[] { 0, 1, 0, 0 }) || header[..4].SequenceEqual("OTTO"u8)))
                throw new ArgumentException("Expected a TrueType/OpenType font.", nameof(fontPath));
        }
        FontPath = Path.GetFullPath(fontPath);
        FontSize = Range(fontSize, 1, 256, nameof(fontSize));
        Left = Range(left, 0, 65536, nameof(left)); Top = Range(top, 0, 65536, nameof(top));
        LineSpacing = Range(lineSpacing, 1, 1024, nameof(lineSpacing));
        MaxLabels = Limit(maxLabels, 1, 128, nameof(maxLabels));
        CacheEntries = Limit(cacheEntries, maxLabels, 4096, nameof(cacheEntries));
        MaxLabelCharacters = Limit(maxLabelCharacters, 1, 4096, nameof(maxLabelCharacters));
        MaxTextureDimension = Limit(maxTextureDimension, 1, 8192, nameof(maxTextureDimension));
    }
}
