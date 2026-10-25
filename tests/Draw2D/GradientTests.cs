using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;
using Xunit;

public class GradientTests
{
    [Fact]
    public void LocalEndpointsSurviveAffineTransformAndInterpolateLinearly()
    {
        using var draw = new ShapeDrawList();
        draw.BeginFrame(800, 600, new(2, .5f, .25f, 3, 10, 20));
        var top = new LinearColor(.2f, .4f, .6f);
        var bottom = new LinearColor(.8f, .6f, .4f);
        draw.FillVerticalGradientRect(new(0), new(100, 100), top, bottom);
        var frame = draw.Finish();
        Assert.Equal(6, frame.Indices.Length);
        var v = frame.Vertices.ToArray();
        Assert.Equal(new float2(10, 20), v[0].Position);
        Assert.Equal(new float2(235, 370), v[2].Position);
        Assert.Equal(new float4(.2f, .4f, .6f, 1), v[0].Color);
        Assert.Equal(v[0].Color, v[1].Color);
        Assert.Equal(new float4(.8f, .6f, .4f, 1), v[2].Color);
        Assert.Equal(v[2].Color, v[3].Color);
        // The center is halfway along the shared diagonal, so both triangles agree.
        Assert.Equal(.5f, (v[0].Color.X + v[2].Color.X) / 2);
        Assert.Equal(.5f, (v[0].Color.Y + v[2].Color.Y) / 2);
        Assert.Equal(.5f, (v[0].Color.Z + v[2].Color.Z) / 2);
        Assert.Equal(new uint[] { 0, 1, 2, 0, 2, 3 }, frame.Indices.ToArray());
    }

    [Fact]
    public void ValidationAndBudgetFailuresLeavePriorGeometryUnchanged()
    {
        using var draw = new ShapeDrawList(4, 6);
        draw.BeginFrame(100, 100);
        draw.FillRect(new(0), new(10), new(1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => draw.FillVerticalGradientRect(new(0), new(10), new(0, 0, 0, 0), new(float.NaN, 0, 0)));
        Assert.Throws<InvalidOperationException>(() => draw.FillVerticalGradientRect(new(0), new(10), new(0, 1, 0), new(0, 0, 1)));
        Assert.Throws<ArgumentException>(() => draw.FillVerticalGradientRect(new(1048576), new(10), new(0, 1, 0), new(0, 0, 1)));
        var frame = draw.Finish();
        Assert.Equal(6, frame.Indices.Length);
        Assert.All(frame.Vertices.ToArray(), v => Assert.Equal(new float4(1, 0, 0, 1), v.Color));
    }

    [Fact]
    public void TransparentEndpointUsesPremultiplicationAndKnownNoOutputCasesSkipBudgets()
    {
        using var draw = new ShapeDrawList();
        draw.BeginFrame(100, 100);
        draw.FillVerticalGradientRect(new(0), new(10), new(1, 0, 0, 0), new(0, 0, 1, .5f));
        var frame = draw.Finish();
        Assert.Equal(new float4(0), frame.Vertices.Span[0].Color);
        Assert.Equal(new float4(0, 0, .5f, .5f), frame.Vertices.Span[2].Color);
        using var empty = new ShapeDrawList(0, 0);
        empty.BeginFrame(100, 100);
        empty.FillVerticalGradientRect(new(0), new(10), new(1, 0, 0, 0), new(0, 0, 1, 0));
        empty.FillVerticalGradientRect(new(0), new(0), new(1, 0, 0), new(0, 0, 1));
        empty.SetTransform(new(0, 0, 0, 0, 0, 0));
        empty.FillVerticalGradientRect(new(0), new(10), new(1, 0, 0), new(0, 0, 1));
        Assert.Empty(empty.Finish().Indices.ToArray());
    }
}
