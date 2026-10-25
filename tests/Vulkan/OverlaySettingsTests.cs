using CrushKit.Brutal.Vulkan;
using Xunit;

namespace CrushKit.Tests.Vulkan;

public sealed class OverlaySettingsTests
{
    [Fact]
    public void TextOnlySettingsRequireNoInspectorAssetOrAssembly()
    {
        // Header fixture proves pre-native configuration shape, not a rasterizable font.
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0, 1, 0, 0, 0, 0, 0, 0]);
            var settings = new OverlaySettings(true, path, 22, 10, 20, 24, 8, 128, 3, 1024);
            Assert.Equal(Path.GetFullPath(path), settings.FontPath);
            Assert.DoesNotContain(typeof(OverlaySettings).GetProperties(), p => p.Name.Contains("Debug") || p.Name.Contains("Image"));
            Assert.DoesNotContain(typeof(OverlaySettings).Assembly.GetReferencedAssemblies(), a => a.Name!.Contains("ImGui"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new OverlaySettings(true, path, 0, 10, 20, 24, 8, 128, 3, 1024));
        }
        finally { File.Delete(path); }
    }
}
