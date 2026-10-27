using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;
using Xunit;

namespace Draw2D.Tests;
public class CircleDetailTests
{
    [Theory]
    [InlineData(3)] [InlineData(9)] [InlineData(10)] [InlineData(-4)] [InlineData(4100)] [InlineData(int.MaxValue)]
    public void InvalidExplicitCountIsNeverRounded(int n) => Assert.Throws<ArgumentOutOfRangeException>(() => new CircleDetail(CircleDetailMode.FixedSideCount,n));

    [Theory]
    [InlineData(.1f,8)] [InlineData(2f,12)] [InlineData(1000f,12)] [InlineData(1000f,16)]
    public void FixedMeansExactTrianglesAtAnyRadiusIncludingOrderedFallback(float radius,int n)
    {
        var detail=new CircleDetail(CircleDetailMode.FixedSideCount,n);
        using var draw=new ShapeDrawList(); using var ordered=new OrderedShapeDrawList(new(8,4_000_000,4_000_000,8,32));
        draw.BeginFrame(4096,4096);draw.FillDisc(new(2000),radius,new(1,1,1),detail);
        Assert.Equal(3*n,draw.Finish().Indices.Length);
        draw.BeginFrame(1280,720);ordered.BeginFrame();ordered.Disc(draw,new(0),radius,new(1,1,1),detail);
        var shared=ordered.Finish(draw); Assert.Equal(0,ordered.Fallbacks);
        Assert.Equal(3*n,Assert.Single(shared.Operations.ToArray()).Mesh!.IndexCount);
        draw.BeginFrame(4096,4096);ordered.BeginFrame();draw.SetTransform(new(1,.1f,0,1,0,0));
        ordered.Disc(draw,new(2000),radius,new(1,1,1),detail);
        var fallback=ordered.Finish(draw);Assert.Equal(1,ordered.Fallbacks);Assert.Equal(3*n,fallback.Triangles.Indices.Length);
    }
    [Fact]
    public void AutoRoundsNineUpToTwelveAndPolicyDoesNotPoisonGeometryCache()
    {
        using var draw=new ShapeDrawList();using var ordered=new OrderedShapeDrawList(new(8,4_000_000,4_000_000,8,32));
        var fixed12=new CircleDetail(CircleDetailMode.FixedSideCount,12);
        long identity=0;
        foreach(var detail in new CircleDetail?[]{null,fixed12,null})
        {
            draw.BeginFrame(1280,720);ordered.BeginFrame();ordered.Disc(draw,new(100),2.5f,new(1,1,1),detail);
            var mesh=Assert.Single(ordered.Finish(draw).Operations.ToArray()).Mesh!;
            Assert.Equal(36,mesh.IndexCount);
            if(identity==0)identity=mesh.Identity;else Assert.Equal(identity,mesh.Identity);
        }
        Assert.Equal(1,ordered.MeshBakes);
        draw.BeginFrame(1280,720);ordered.BeginFrame();ordered.Disc(draw,new(0),1000,new(1,1,1),fixed12);
        Assert.Equal(identity,Assert.Single(ordered.Finish(draw).Operations.ToArray()).Mesh!.Identity);
        draw.BeginFrame(1280,720);ordered.BeginFrame();ordered.Disc(draw,new(0),1000,new(1,1,1));
        var automatic=Assert.Single(ordered.Finish(draw).Operations.ToArray()).Mesh!;
        Assert.True(automatic.IndexCount>36);Assert.Equal(0,automatic.IndexCount/3%4);
        Assert.True(1000*automatic.UnitCurveError<=.16);
    }
}
