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
    public void RepeatedAndAlternatingResolutionsReusePublishedTopologyWithSymmetricRim()
    {
        using var adapter = new DiskMeshAdapter();
        using var positions = new NativeStrideList(ByteSize.Of<float3>());
        using var indices = new NativeStrideList(ByteSize.Of<uint>());
        foreach (int resolution in new[] { 8, 8, 12, 16, 20, 32, 32, 128, 4096, 8 })
        {
            positions.Clear(); indices.Clear();
            var mesh = new ProcGenMesh(positions, indices);
            ProcGenMeshLibrary.GenerateDisk(in mesh, new float3(0, 0, 0), new float3(0, 0, 1), 1, resolution, false);
            var expected = positions.ToSpan<float3>();
            adapter.Generate(resolution);
            var generated = adapter.Positions;
            Assert.Equal(3*resolution, generated.Length);
            var actual = new float3[resolution];
            for(int i=0;i<resolution;i++)
            {
                actual[i]=generated[(int)adapter.Indices[3*i]];
                Assert.Equal(new float3(0),generated[(int)adapter.Indices[3*i+2]]);
                Assert.Equal(generated[(int)adapter.Indices[3*((i+1)%resolution)]],generated[(int)adapter.Indices[3*i+1]]);
            }
            Assert.Equal(generated[0], generated[^2]);
            int q = resolution / 4;
            Assert.Equal(new float3(-1, 0, 0), actual[0]);
            Assert.Equal(new float3(0, 1, 0), actual[q]);
            Assert.Equal(new float3(1, 0, 0), actual[2*q]);
            Assert.Equal(new float3(0, -1, 0), actual[3*q]);
            foreach(float zero in new[]{actual[0].Y,actual[q].X,actual[2*q].Y,actual[3*q].X})
                Assert.Equal(0,BitConverter.SingleToInt32Bits(zero));
            for (int i = 0; i < resolution; i++)
            {
                var a = actual[i]; var b = actual[(i+q)%resolution];
                Assert.Equal(a.Y, b.X); Assert.Equal(-a.X, b.Y);
                var mirror = actual[(q-i+resolution)%resolution];
                Assert.Equal(-a.Y, mirror.X); Assert.Equal(-a.X, mirror.Y);
                Assert.InRange(Math.Abs(Math.Sqrt((double)a.X*a.X+(double)a.Y*a.Y)-1), 0, 1e-6);
                Assert.True((a.X == 0 || float.IsNormal(a.X)) && (a.Y == 0 || float.IsNormal(a.Y)));
            }
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
