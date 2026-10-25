using System.Text;
using Brutal.Numerics;
using Brutal.TextureApi.Abstractions;
using Brutal.SdlApi;
using Brutal.SdlApi.Ttf;

namespace CrushKit.Brutal.Vulkan;

/// <summary>Bounded whole-label SDL.Ttf cache. All access (including disposal) belongs to the creating thread.</summary>
public sealed unsafe class OverlayText : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly OverlaySettings _settings;
    private readonly Dictionary<string, GenericTexture> _cache = new(StringComparer.Ordinal);
    private TtfFont _font;
    private bool _initialized, _disposed;
    public int Rasterizations { get; private set; }
    public int CacheHits { get; private set; }
    public int Count => _cache.Count;
    public int Generation { get; private set; }

    public OverlayText(OverlaySettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        try { SdlTtf.Init(); _initialized = true; _font = SdlTtf.OpenFont(settings.FontPath, settings.FontSize);
            if (_font.IsNull()) throw new InvalidDataException("SDL.Ttf could not open the configured font."); }
        catch { Dispose(); throw; }
    }

    /// <returns>A cache-owned BRUTAL texture, borrowed only until eviction/disposal. Do not Destroy or Unload it.</returns>
    public GenericTexture Get(string label)
    {
        CheckThread(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(label) || label.Length > _settings.MaxLabelCharacters || label.Any(char.IsControl))
            throw new ArgumentException("Overlay label is empty, too long, or contains control characters.", nameof(label));
        if (_cache.TryGetValue(label, out var hit)) { CacheHits++; return hit; }
        int width = 0, height = 0;
        var length = (nuint)Encoding.UTF8.GetByteCount(label);
        if (!SdlTtf.GetStringSize(_font, label, length, &width, &height)) throw new InvalidDataException("SDL.Ttf measurement failed.");
        OverlayPixels.ValidateDimensions(width, height, _settings.MaxTextureDimension);
        var surface = SdlTtf.RenderTextBlended(_font, label, length, new SdlColor { r = 255, g = 255, b = 255, a = 255 });
        if (surface == null) throw new InvalidDataException("SDL.Ttf rasterization failed.");
        try
        {
            var converted = Sdl.PInvoke.ConvertSurface(surface, SdlPixelFormat.Rgba32);
            if (converted == null) throw new InvalidDataException("SDL RGBA conversion failed.");
            try
            {
                OverlayPixels.ValidateDimensions(converted->w, converted->h, _settings.MaxTextureDimension);
                var rowBytes = checked(converted->w * 4);
                if (converted->pitch < rowBytes || converted->pixels == null) throw new InvalidDataException("Invalid SDL surface pitch/pixels.");
                var result = GenericTexture.Defaults.RGBA8UNorm(new int2(converted->w, converted->h));
                try
                {
                    var pixels = result.ImageData(0, 0, 0); int pitch = checked((int)result.RowPitch(0));
                    for (int y = 0; y < converted->h; y++)
                    {
                        var source = new ReadOnlySpan<byte>((byte*)converted->pixels + y * converted->pitch, rowBytes);
                        for (int x = 0; x < converted->w; x++)
                        {
                            byte coverage = source[x * 4 + 3]; int i = y * pitch + x * 4;
                            // Premultiply before filtering: a white mask has RGB=A=linear coverage.
                            pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = coverage;
                        }
                    }
                    if (_cache.Count == _settings.CacheEntries) { ClearCache(); Generation++; }
                    _cache.Add(label, result); Rasterizations++;
                    return result;
                }
                catch { result.Destroy(); throw; }
            }
            finally { Sdl.PInvoke.DestroySurface(converted); }
        }
        finally { Sdl.PInvoke.DestroySurface(surface); }
    }

    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Overlay text is main-thread owned.");
    }
    public void Dispose()
    {
        CheckThread(); if (_disposed) return; _disposed = true;
        try { ClearCache(); }
        finally
        {
            if (_initialized)
            {
                try { if (!_font.IsNull()) SdlTtf.CloseFont(_font); }
                finally { SdlTtf.Quit(); }
            }
        }
    }

    private void ClearCache()
    {
        var textures = _cache.Values.ToArray(); _cache.Clear();
        ResourceLifetime.ReleaseAll(textures.Select(t => (Action)t.Destroy).ToArray());
    }
}
