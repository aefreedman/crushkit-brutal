using System.Runtime.InteropServices;
using Brutal;
using Brutal.Collections;
using Brutal.Numerics;
using Brutal.Render.Mesh;
using CrushKit.Brutal.Draw2D;
using Xunit;

namespace Shapes.Tests;

public class DiskReuseTests
{
    [Fact]
    public void RepeatedAndAlternatingResolutionsMatchPublishedGeneratorBitwise()
    {
        using var adapter = new DiskMeshAdapter();
        using var positions = new NativeStrideList(ByteSize.Of<float3>());
        using var indices = new NativeStrideList(ByteSize.Of<uint>());
        foreach (int resolution in new[] { 8, 8, 32, 32, 8, 128, 8, 128, 128 })
        {
            positions.Clear(); indices.Clear();
            var mesh = new ProcGenMesh(positions, indices);
            ProcGenMeshLibrary.GenerateDisk(in mesh, new float3(0, 0, 0), new float3(0, 0, 1), 1, resolution, false);
            var expected = positions.ToSpan<float3>();
            expected[^2] = expected[0];
            adapter.Generate(resolution);
            Assert.True(MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(adapter.Positions)));
            Assert.True(indices.ToSpan<uint>().SequenceEqual(adapter.Indices));
        }
    }

    [Fact]
    public void DisposedAdapterNeverReturnsFreedCache()
    {
        var adapter = new DiskMeshAdapter(); adapter.Generate(8); adapter.Dispose(); adapter.Dispose();
        Assert.Throws<ObjectDisposedException>(() => adapter.Generate(8));
        Assert.Throws<ObjectDisposedException>(() => { _ = adapter.Positions.Length; });
        Assert.Throws<ObjectDisposedException>(() => { _ = adapter.Indices.Length; });
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(-1f)]
    [InlineData(-3f)]
    public void ReusedFramesMatchFreshProducerAcrossScaleFlipAndOutline(float scale)
    {
        using var reused = new ShapeDrawList();
        foreach (float radius in new[] { 2f, 2f, 30f, 2f, 70f, 70f, 2f })
        {
            using var fresh = new ShapeDrawList();
            ShapeFrame Build(ShapeDrawList draw)
            {
                draw.BeginFrame(2000, 2000, new float3x2(scale, 0, 0, Math.Abs(scale), 500, 500));
                draw.FillDisc(new float2(10, 20), radius, new(1, .5f, .25f));
                draw.StrokeCircle(new float2(10, 20), radius, 1, new(1, 1, 1));
                return draw.Finish();
            }
            var actual = Build(reused); var expected = Build(fresh);
            Assert.True(MemoryMarshal.AsBytes(expected.Vertices.Span).SequenceEqual(MemoryMarshal.AsBytes(actual.Vertices.Span)));
            Assert.True(expected.Indices.Span.SequenceEqual(actual.Indices.Span));
            Assert.True(expected.Commands.Span.SequenceEqual(actual.Commands.Span));
        }
    }
}
