using CrushKit.Brutal.Vulkan;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.TextureApi;
using Brutal.TextureApi.Abstractions;
using CrushKit.Brutal.Draw2D;
using Xunit;

namespace CrushKit.Tests.Vulkan;

public sealed class OverlayTextureTests
{
    [Fact]
    public void PremultipliedTexelsFilterWithoutTransparentColorLeakOrDoubleAlpha()
    {
        Assert.Equal(0, OverlayPixels.PremultiplySrgb(255, 0));
        Assert.Equal(80, OverlayPixels.PremultiplySrgb(255, 80));
        Assert.Equal(17, OverlayPixels.PremultiplySrgb(128, 80));
        // Midpoint between opaque white and any fully transparent source color is PM white(.5), alpha(.5).
        double filteredColor = (OverlayPixels.PremultiplySrgb(255, 255) + OverlayPixels.PremultiplySrgb(255, 0)) / 510d;
        double filteredAlpha = .5;
        double premultipliedTint = .5;
        Assert.Equal(.25, filteredColor * premultipliedTint);
        Assert.Equal(.25, filteredAlpha * premultipliedTint);
        Assert.NotEqual(.25, filteredColor * premultipliedTint * filteredAlpha); // SRC_ALPHA would multiply twice.
    }

    [Fact]
    public void GenericTextureCopyOwnsDataAndUsesDestroyNotLoaderUnload()
    {
        var source = GenericTexture.Defaults.RGBA8UNorm(new int2(2, 2));
        GenericTexture copy;
        try { source.Data.Fill(123); copy = new GenericTexture(source); }
        finally { source.Destroy(); }
        try
        {
            Assert.All(copy.Data.ToArray(), b => Assert.Equal(123, b));
            Assert.Throws<ArgumentException>(() => TextureLoader.Unload(copy)); // Published ownership trap: no free happens here.
        }
        finally { copy.Destroy(); } // Exactly once, even after rejected Unload.
    }

    [Fact]
    public void UploadPlanPreservesPaddedRowsAndTranslatesToVulkanTexelPitch()
    {
        var texture = new GenericTexture(new GenericTexture.CreateInfo
        {
            Format = TextureFormat.R8G8B8A8_UNorm, Extent = new(2, 2, 1), LayerCount = 1, LevelCount = 1, FaceCount = 1, DimensionCount = 2, IsArray = false, IsCubemap = false,
        }, ByteSize.Of(24), new[] { new GenericTexture.CreateImageRange
        { StartByteOffset = ByteSize.Zero, ByteLength = ByteSize.Of(24), RowPitch = ByteSize.Of(12) } });
        try
        {
            for (int i = 0; i < texture.Data.Length; i++) texture.Data[i] = (byte)i;
            var layout = new TextureMemoryLayout(texture);
            Assert.True(layout.ImageLayout(0).IsRowPadded);
            var plan = UploadRegions.Build(texture);
            Assert.Equal(24, (int)plan.RequiredBufferSizeBytes);
            var stage = new byte[(int)plan.RequiredBufferSizeBytes];
            OverlayTextureUpload.CopyStaging(texture, plan, stage);
            Assert.Equal(texture.Data.ToArray(), stage);
            var copy = Assert.Single(OverlayTextureUpload.BuildCopies(texture, plan));
            Assert.Equal(3, copy.BufferRowLength); // 12-byte pitch / RGBA block size; not a second layout calculation.
            Assert.Equal(2, copy.ImageExtent.Width); Assert.Equal(2, copy.ImageExtent.Height);
            Assert.Equal(1, copy.ImageSubresource.LayerCount); Assert.Equal(0, copy.ImageSubresource.MipLevel);
            Assert.Equal((ByteSize64)0, copy.BufferOffset);
            Assert.Throws<ArgumentException>(() => OverlayTextureUpload.CopyStaging(texture, plan, new byte[23]));
        }
        finally { texture.Destroy(); }
    }

    [Fact]
    public void InspectorPreviewIsAnOwnedStraightCopyWithZeroAlphaSanitized()
    {
        var texture = GenericTexture.Defaults.RGBA8UNorm(new int2(2, 1));
        try
        {
            new byte[] { 80, 40, 0, 80, 0, 0, 0, 0 }.CopyTo(texture.Data);
            var preview = OverlayPixels.CreateInspectorPreview(texture);
            try
            {
                Assert.Equal(new byte[] { 255, 128, 0, 80, 0, 0, 0, 0 }, preview.Data.ToArray());
                Assert.Equal(80, texture.Data[0]);
            }
            finally { preview.Destroy(); }
        }
        finally { texture.Destroy(); }
    }

    [Fact]
    public void UvQuadUsesSeparateAbiAndSafeRowMajorIndicesWithCorrectClipping()
    {
        Assert.Equal(24, Marshal.SizeOf<ShapeVertex>());
        Assert.Equal(32, Marshal.SizeOf<OverlayVertex>());
        Assert.Equal(8, Marshal.OffsetOf<OverlayVertex>(nameof(OverlayVertex.Uv)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<OverlayVertex>(nameof(OverlayVertex.Color)).ToInt32());
        using var batch = new OverlayQuadBatch();
        var tint = new LinearColor(.2f, .1f, .05f, .5f);
        Assert.True(batch.TryAdd(-10, -5, 20, 10, 100, 100, tint));
        Assert.Equal(4, batch.Vertices.Length); Assert.Equal(6, batch.Indices.Length);
        Assert.Equal(new float2(0, 0), batch.Vertices[0].Position);
        Assert.Equal(new float2(.5f, .5f), batch.Vertices[0].Uv);
        Assert.Equal(new float2(10, 0), batch.Vertices[1].Position);
        Assert.Equal(new float2(1, .5f), batch.Vertices[1].Uv);
        Assert.Equal(new float2(0, 5), batch.Vertices[2].Position);
        Assert.Equal(new float2(.5f, 1), batch.Vertices[2].Uv);
        Assert.Equal(new float2(10, 5), batch.Vertices[3].Position);
        Assert.Equal(new float2(1, 1), batch.Vertices[3].Uv);
        Assert.Equal(new float4(.1f, .05f, .025f, .5f), batch.Vertices[0].Color); // Straight LinearColor is premultiplied once, without byte quantization.
        float area = 0;
        for (int i = 0; i < 6; i += 3)
        {
            var ids = batch.Indices.Slice(i, 3).ToArray();
            Assert.Equal(3, ids.Distinct().Count()); Assert.All(ids, id => Assert.InRange(id, 0u, 3u));
            var a = batch.Vertices[(int)ids[0]].Position;
            var b = batch.Vertices[(int)ids[1]].Position;
            var c = batch.Vertices[(int)ids[2]].Position;
            float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            Assert.True(cross > 0); // Consistent positive-framebuffer winding; pipeline deliberately disables culling.
            area += cross / 2;
        }
        Assert.Equal(50, area); // Two triangles exactly cover the clipped rectangle.
        Assert.True(batch.TryAdd(90, 90, 20, 20, 100, 100, tint));
        Assert.All(batch.Indices[6..].ToArray(), id => Assert.InRange(id, 4u, 7u));
        Assert.Equal(new float2(.5f, .5f), batch.Vertices[7].Uv);
        Assert.False(batch.TryAdd(100, 0, 10, 10, 100, 100, tint));
        Assert.False(batch.TryAdd(0, 0, 10, 10, 100, 100, new(0, 0, 0, 0)));
        Assert.Equal(12, batch.Indices.Length);
        for (int q = 2; q < 7; q++)
        {
            Assert.True(batch.TryAdd(q, q, 10, 10, 100, 100, tint));
            Assert.All(batch.Indices.Slice(q * 6, 6).ToArray(), id => Assert.InRange(id, (uint)(q * 4), (uint)(q * 4 + 3)));
        }
        Assert.Equal(28, batch.Vertices.Length); Assert.Equal(42, batch.Indices.Length);
        batch.Clear(); Assert.Empty(batch.Indices.ToArray());
    }
}
