using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;
using Xunit;

namespace Draw2D.Tests;

public class BakedMeshTests
{
    private static BakedMeshLimits Limits()=>new(24,1_000_000,1_000_000,2048,8192);
    [Fact]
    public void DiscSharesExactTopologyAndMovementReusesCpuMesh()
    {
        using var draw=new ShapeDrawList();using var ordered=new OrderedShapeDrawList(Limits());
        long identity=0;
        for(int frame=0;frame<10;frame++)
        {
            draw.BeginFrame(1280,720);ordered.BeginFrame();
            var center=new float2(100+frame*.125f,200);var tint=new LinearColor(.2f,.5f,.6f,.7f);
            ordered.Disc(draw,center,8,tint);var result=ordered.Finish(draw);
            var op=Assert.Single(result.Operations.ToArray());Assert.NotNull(op.Mesh);
            if(frame==0)identity=op.Mesh!.Identity;else Assert.Equal(identity,op.Mesh!.Identity);
            using var reference=new ShapeDrawList();reference.BeginFrame(1280,720);reference.FillDisc(center,8,tint);
            Compare(reference.Finish(),result,.01);
        }
        Assert.Equal(1,ordered.MeshBakes);
    }
    [Fact]
    public void TriangleCommandsSplitAtInsertionWithoutSortingOrClipChanges()
    {
        using var draw=new ShapeDrawList();using var ordered=new OrderedShapeDrawList(Limits());
        draw.BeginFrame(1280,720);ordered.BeginFrame();
        draw.FillRect(new(0),new(8),new(.2f,.3f,.4f));
        ordered.Disc(draw,new(100),8,new(.2f,.4f,.6f,.5f));
        draw.FillRect(new(20),new(8),new(.2f,.3f,.4f));
        draw.PushClip(new(1,2,600,500));ordered.Disc(draw,new(200),8,new(.3f,.2f,.5f,.5f));draw.PopClip();
        draw.FillRect(new(40),new(8),new(.2f,.3f,.4f));
        var f=ordered.Finish(draw);var ops=f.Operations.ToArray();Assert.Equal(5,ops.Length);
        Assert.Null(ops[0].Mesh);Assert.NotNull(ops[1].Mesh);Assert.Null(ops[2].Mesh);Assert.NotNull(ops[3].Mesh);Assert.Null(ops[4].Mesh);
        Assert.Equal(new PixelRect(1,2,600,500),ops[3].Clip);
        Assert.Equal(18,f.Triangles.Indices.Length);Assert.Single(f.Triangles.Commands.ToArray());
    }
    [Fact]
    public void UnsupportedTransformFallsBackInPlaceAndVirtualBudgetIsRetained()
    {
        using var draw=new ShapeDrawList();using var ordered=new OrderedShapeDrawList(Limits());
        draw.BeginFrame(1280,720);ordered.BeginFrame();ordered.Disc(draw,new(100),4,new(1,0,0));
        draw.SetTransform(new float3x2(1,.2f,0,1,0,0));ordered.Disc(draw,new(200),4,new(0,1,0));
        draw.SetTransform(float3x2.Identity);ordered.Disc(draw,new(300),4,new(0,0,1));
        var f=ordered.Finish(draw);Assert.Equal(1,ordered.Fallbacks);Assert.Equal(3,f.Operations.Length);Assert.Null(f.Operations.Span[1].Mesh);
        using var measure=new ShapeDrawList();measure.BeginFrame(1280,720);measure.FillDisc(new(100),4,new(1,0,0));var measured=measure.Finish();
        using var small=new ShapeDrawList(measured.Vertices.Length,measured.Indices.Length);using var bounded=new OrderedShapeDrawList(Limits());
        small.BeginFrame(1280,720);bounded.BeginFrame();bounded.Disc(small,new(100),4,new(1,0,0));
        Assert.Throws<InvalidOperationException>(()=>bounded.Disc(small,new(200),4,new(1,0,0)));
    }
    [Fact]
    public void CancellationOfHugeCpuIntermediatesCannotQualifyBySmallFinalPose()
    {
        using var draw=new ShapeDrawList();using var ordered=new OrderedShapeDrawList(Limits());
        draw.BeginFrame(1280,720);ordered.BeginFrame();draw.SetTransform(new(1,0,0,1,-1e20f,-1e20f));
        ordered.Disc(draw,new(1e20f),4,new(1,0,0));var f=ordered.Finish(draw);
        Assert.Empty(f.Instances.ToArray());Assert.Empty(f.Triangles.Indices.ToArray());Assert.Equal(1,ordered.Fallbacks);
    }
    [Fact]
    public void CpuCacheCountsPinsReservationsAndInvalidationDuringChurn()
    {
        var limits=new BakedMeshLimits(3,50_000,50_000,10,20);using var cache=new BakedMeshCache(limits);
        cache.ReserveExternalCpuBytes(8192);
        var weak=new List<WeakReference>();
        for(int i=0;i<2000;i++)
        {
            cache.BeginFrame();var mesh=cache.Get(i,BakedShapeMesh.DiscCharge(8),()=>BakedShapeMesh.Disc(8));
            Assert.NotNull(mesh);if(i<10)weak.Add(new(mesh));Assert.InRange(cache.Count,1,3);Assert.InRange(cache.RetainedBytes,8192,limits.CpuBytes);
        }
        cache.BeginFrame();cache.Clear();Assert.Equal(8192,cache.RetainedBytes);Assert.Equal(0,cache.Count);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Assert.All(weak,w=>Assert.False(w.IsAlive));
        cache.Dispose();Assert.Equal(0,cache.RetainedBytes);
    }
    [Fact]
    public void CompoundGeometryIsOwnedNonoverlappingAndSeamsRemainIdentical()
    {
        MeshRectangle[] source=[new(-10,-10,0,20),new(0,10,10,20)];var mesh=BakedShapeMesh.Rectangles(source);
        source[0]=new(1,1,2,2);Assert.Equal(-10,mesh.Vertices[0].X);
        Assert.Equal(mesh.Vertices[2].X,mesh.Vertices[4].X);
        Assert.Throws<ArgumentException>(()=>BakedShapeMesh.Rectangles(new MeshRectangle[]{new(0,0,2,2),new(1,1,3,3)}));
    }
    [Fact]
    public void ExhaustiveAdmittedDiscGridMatchesReferenceAndBothArithmeticForms()
    {
        int admitted=0,fallback=0;
        foreach(float x in new[]{0f,.33333334f,100.125f,719.999f,8191f})
        foreach(float radius in new[]{.0001f,.1f,1f,4f,16f,40f,512f})
        foreach(float scale in new[]{.001f,.5f,1f,1.25f,3f})
        {
            using var ordinary=new ShapeDrawList();using var draw=new ShapeDrawList();using var ordered=new OrderedShapeDrawList(Limits());
            var transform=new float3x2(scale,0,0,scale,.125f,.375f);var p=new float2(x,100.33333f);var color=new LinearColor(.2f,.4f,.8f,.6f);
            ordinary.BeginFrame(1280,720);ordinary.SetTransform(transform);ordinary.FillDisc(p,radius,color);var reference=ordinary.Finish();
            draw.BeginFrame(1280,720);ordered.BeginFrame();draw.SetTransform(transform);ordered.Disc(draw,p,radius,color);var result=ordered.Finish(draw);
            if(result.Instances.IsEmpty)fallback++;else admitted++;
            Compare(reference,result,.01);
        }
        Assert.True(admitted>50);Assert.True(fallback>0);
    }
    [Fact]
    public void CachedPrecisionBoundsCoverEveryVertexAndTriangleForAdmittedTransforms()
    {
        int admitted=0,rejected=0;
        foreach(int resolution in new[]{8,12,32,128,1024,4096})
        {
            var mesh=BakedShapeMesh.Disc(resolution);
            foreach(float radius in new[]{.001f,.1f,1f,4f,40f,512f})foreach(float scale in new[]{.001f,.5f,1f,1.3333334f})
            foreach(float coordinate in new[]{0f,.33333334f,100.125f,4096f,8191f})
            {
                double x=(double)coordinate*scale+.125,y=100.333333f*(double)scale+.375,s=(double)radius*scale;
                var origin=new float2((float)x,(float)y);float packed=(float)s;
                if(!BakedPrecision.TryBound(mesh,x,y,s,origin,packed,0,1280,720,out var bounds)){rejected++;continue;}
                admitted++;
                var reference=new float2[mesh.VertexCount];var candidate=new float2[mesh.VertexCount];
                for(int i=0;i<mesh.VertexCount;i++)
                {
                    var u=mesh.Vertices[i];
                    reference[i]=new((float)(((double)coordinate+(double)u.X*radius)*scale+.125),(float)((100.333333f+(double)u.Y*radius)*scale+.375));
                    float px=origin.X+packed*u.X,py=origin.Y+packed*u.Y;
                    candidate[i]=new((float)(((2f*px/1280-1f)+1d)*640),(float)(((2f*py/720-1f)+1d)*360));
                    Assert.InRange(Math.Abs((double)candidate[i].X-(x+s*u.X)),0,bounds.ShaderError);
                    Assert.InRange(Math.Abs((double)candidate[i].Y-(y+s*u.Y)),0,bounds.ShaderError);
                    Assert.InRange(Math.Abs((double)reference[i].X-(x+s*u.X)),0,bounds.ReferenceError);
                    Assert.InRange(Math.Abs((double)reference[i].Y-(y+s*u.Y)),0,bounds.ReferenceError);
                }
                for(int i=0;i<mesh.IndexCount;i+=3)
                {
                    int a=(int)mesh.Indices[i],b=(int)mesh.Indices[i+1],c=(int)mesh.Indices[i+2];
                    Assert.True(Area(candidate[a],candidate[b],candidate[c])<0);Assert.True(Area(reference[a],reference[b],reference[c])<0);
                    var ua=mesh.Vertices[a];var ub=mesh.Vertices[b];double mx=(double)ua.X+ub.X,my=(double)ua.Y+ub.Y,length=Math.Sqrt(mx*mx+my*my);
                    double dx=x+s*mx/length-((double)candidate[a].X+candidate[b].X)/2,dy=y+s*my/length-((double)candidate[a].Y+candidate[b].Y)/2;
                    Assert.InRange(Math.Sqrt(dx*dx+dy*dy),0,.25);
                }
            }
        }
        Assert.True(admitted>100);Assert.True(rejected>100);
        static double Area(float2 a,float2 b,float2 c)=>((double)b.X-a.X)*((double)c.Y-a.Y)-((double)b.Y-a.Y)*((double)c.X-a.X);
    }
    internal static void Compare(ShapeFrame reference,OrderedShapeFrame candidate,double tolerance)
    {
        var expected=reference.Indices.Span;int index=0;
        foreach(var op in candidate.Operations.Span)
        {
            if(op.Mesh is null)
            {
                for(int i=op.First;i<op.First+op.Count;i++)
                {
                    var a=reference.Vertices.Span[(int)expected[index++]];var b=candidate.Triangles.Vertices.Span[(int)candidate.Triangles.Indices.Span[i]];
                    Assert.Equal(a.Position,b.Position);Assert.Equal(a.Color,b.Color);
                }
            }
            else foreach(var instance in candidate.Instances.Span.Slice(op.First,op.Count))foreach(uint localIndex in op.Mesh.Indices)
            {
                var a=reference.Vertices.Span[(int)expected[index++]];var u=op.Mesh.Vertices[(int)localIndex];
                float2 b=new(instance.Origin.X+instance.AxisX.X*u.X+instance.AxisY.X*u.Y,instance.Origin.Y+instance.AxisX.Y*u.X+instance.AxisY.Y*u.Y);
                Assert.InRange(Math.Abs((double)a.Position.X-b.X),0,tolerance);Assert.InRange(Math.Abs((double)a.Position.Y-b.Y),0,tolerance);Assert.Equal(a.Color,instance.Color);
                // Equivalent contraction of the exact shader pixel expression, then NDC and viewport mapping.
                float fx=MathF.FusedMultiplyAdd(instance.AxisY.X,u.Y,MathF.FusedMultiplyAdd(instance.AxisX.X,u.X,instance.Origin.X));
                float fy=MathF.FusedMultiplyAdd(instance.AxisY.Y,u.Y,MathF.FusedMultiplyAdd(instance.AxisX.Y,u.X,instance.Origin.Y));
                double nx=((2f*fx/reference.Width-1f)+1d)*reference.Width/2;
                double ny=((2f*fy/reference.Height-1f)+1d)*reference.Height/2;
                Assert.InRange(Math.Abs(a.Position.X-nx),0,tolerance);Assert.InRange(Math.Abs(a.Position.Y-ny),0,tolerance);
            }
        }
        Assert.Equal(expected.Length,index);
    }
}
