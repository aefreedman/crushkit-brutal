using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;
using Xunit;

namespace Shapes.Tests;

public class OutlineTests
{
    private static readonly LinearColor White = new(1, 1, 1);
    private static float2 V(float x, float y) => new(x, y);
    private static double Cross(float2 a, float2 b, float2 c) => ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);
    private static double Area(ShapeFrame f)
    {
        double area = 0; var vs = f.Vertices.Span; var ids = f.Indices.Span;
        Assert.Equal(0, ids.Length % 3);
        foreach (var v in vs) { Assert.True(float.IsFinite(v.Position.X)); Assert.True(float.IsFinite(v.Position.Y)); }
        for (int i = 0; i < ids.Length; i += 3)
        {
            Assert.InRange(ids[i], 0u, (uint)vs.Length - 1); Assert.InRange(ids[i + 1], 0u, (uint)vs.Length - 1); Assert.InRange(ids[i + 2], 0u, (uint)vs.Length - 1);
            double a = Math.Abs(Cross(vs[(int)ids[i]].Position, vs[(int)ids[i + 1]].Position, vs[(int)ids[i + 2]].Position)) / 2;
            Assert.True(a > 0); area += a;
        }
        return area;
    }
    // Convex triangle clipping gives exact positive-area overlap, permitting shared tile boundaries.
    private static void Tiled(ShapeFrame frame)
    {
        var vs = frame.Vertices.ToArray(); var ids = frame.Indices.ToArray();
        for (int i = 0; i < ids.Length; i += 3) for (int j = i + 3; j < ids.Length; j += 3)
        {
            var poly = new List<(double x, double y)>(); for (int k = 0; k < 3; k++) { var p = vs[ids[i + k]].Position; poly.Add((p.X, p.Y)); }
            var t = Enumerable.Range(0, 3).Select(k => vs[ids[j + k]].Position).ToArray(); double sign = Math.Sign(Cross(t[0], t[1], t[2]));
            for (int edge = 0; edge < 3 && poly.Count > 0; edge++)
            {
                var a = t[edge]; var b = t[(edge + 1) % 3];
                double Side((double x, double y) p) => sign * ((b.X - a.X) * (p.y - a.Y) - (b.Y - a.Y) * (p.x - a.X));
                var output = new List<(double x, double y)>(); var previous = poly[^1]; double ps = Side(previous);
                foreach (var current in poly) { double cs = Side(current); if ((cs >= 0) != (ps >= 0)) { double u = ps / (ps - cs); output.Add((previous.x + u * (current.x - previous.x), previous.y + u * (current.y - previous.y))); } if (cs >= 0) output.Add(current); previous = current; ps = cs; }
                poly = output;
            }
            double area = 0; for (int k = 0; k < poly.Count; k++) { var a = poly[k]; var b = poly[(k + 1) % poly.Count]; area += a.x * b.y - a.y * b.x; }
            Assert.True(Math.Abs(area) < 0.0001, $"Overlapping triangles {i / 3}/{j / 3}: {area / 2}");
        }
    }
    private static void Hollow(ShapeFrame frame, float2 point)
    {
        var vs = frame.Vertices.Span; var ids = frame.Indices.Span;
        for (int i = 0; i < ids.Length; i += 3)
        {
            var a = vs[(int)ids[i]].Position; var b = vs[(int)ids[i + 1]].Position; var c = vs[(int)ids[i + 2]].Position;
            double sign = Math.Sign(Cross(a, b, c));
            Assert.False(sign * Cross(a, b, point) >= 0 && sign * Cross(b, c, point) >= 0 && sign * Cross(c, a, point) >= 0);
        }
    }
    [Theory]
    [InlineData(StrokeAlignment.Center, -1, 12, 80)]
    [InlineData(StrokeAlignment.Inside, 0, 10, 64)]
    [InlineData(StrokeAlignment.Outside, -2, 14, 96)]
    public void RectangleAlignmentIsHollowAndTiled(StrokeAlignment alignment, float min, float extent, double area)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokeRect(V(0, 0), V(10, 10), 2, new(1, 1, 1, .5f), alignment);
        var f = d.Finish(); Assert.Equal(area, Area(f), 5); Tiled(f); Hollow(f, V(5, 5));
        Assert.Equal(min, f.Vertices.ToArray().Min(v => v.Position.X));
        Assert.Equal(min + extent, f.Vertices.ToArray().Max(v => v.Position.X));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvexCleanupWindingAndConsumedHole(bool reverse)
    {
        float2[] p = [V(0, 0), V(5, 0), V(10, 0), V(10, 0), V(10, 10), V(0, 10), V(0, 0)];
        if (reverse) Array.Reverse(p);
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokeClosedPolyline(p, 10, White, StrokeAlignment.Inside);
        var f = d.Finish(); Assert.Equal(100, Area(f), 5); Tiled(f);
    }
    [Fact]
    public void AcuteTriangleAndDisappearingInsetEdgesTile()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(1000, 1000);
        d.StrokeTriangle(V(0, 0), V(100, 0), V(1, 2), 1, White);
        var f = d.Finish(); Assert.True(Area(f) > 0); Tiled(f);
        d.BeginFrame(1000, 1000);
        d.StrokeClosedPolyline([V(0, 0), V(10, 0), V(10, 1), V(9, 10), V(0, 10)], 2, White, StrokeAlignment.Inside);
        f = d.Finish(); Assert.True(Area(f) > 0); Tiled(f);
    }
    [Fact]
    public void InvalidContoursRejectAndBudgetFailureIsAtomic()
    {
        using var d = new ShapeDrawList(4, 6); d.BeginFrame(100, 100); d.FillRect(V(0, 0), V(1, 1), White);
        Assert.Throws<ArgumentException>(() => d.StrokeClosedPolyline([V(0, 0), V(10, 0), V(2, 2), V(10, 10), V(0, 10)], 1, White));
        Assert.Throws<ArgumentException>(() => d.StrokeClosedPolyline([V(0, 0), V(10, 10), V(0, 10), V(10, 0)], 1, White));
        Assert.Throws<InvalidOperationException>(() => d.StrokeRect(V(0, 0), V(10, 10), 1, White));
        var f = d.Finish(); Assert.Equal(4, f.Vertices.Length); Assert.Equal(1, Area(f));
    }
    [Theory]
    [InlineData(StrokeAlignment.Center, 9, 11)]
    [InlineData(StrokeAlignment.Inside, 8, 10)]
    [InlineData(StrokeAlignment.Outside, 10, 12)]
    public void CircleRadiiAreaAndTiling(StrokeAlignment alignment, double inner, double outer)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokeCircle(V(20, 20), 10, 2, new(1, 1, 1, .5f), alignment);
        var f = d.Finish(); Tiled(f); Hollow(f, V(20, 20));
        Assert.InRange(Area(f), Math.PI * (outer * outer - inner * inner) * .97, Math.PI * (outer * outer - inner * inner));
        foreach (var v in f.Vertices.Span)
        {
            double r = Math.Sqrt(Math.Pow(v.Position.X - 20, 2) + Math.Pow(v.Position.Y - 20, 2));
            Assert.True(Math.Abs(r - inner) < .00001 || Math.Abs(r - outer) < .00001);
        }
        Assert.Equal(20 - outer, f.Vertices.ToArray().Min(v => v.Position.X));
        Assert.Equal(20 + outer, f.Vertices.ToArray().Max(v => v.Position.X));
    }
    [Theory]
    [InlineData(StrokeAlignment.Center, 1)]
    [InlineData(StrokeAlignment.Inside, 2)]
    [InlineData(StrokeAlignment.Outside, 2)]
    public void RoundedRectConsistentCornerRadii(StrokeAlignment alignment, float width)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokeRoundedRect(V(0, 0), V(20, 10), 3, width, White, alignment);
        var f = d.Finish(); Tiled(f);
        double outside = alignment == StrokeAlignment.Inside ? 0 : alignment == StrokeAlignment.Center ? width / 2.0 : width;
        double inside = width - outside;
        double outerArea = (20 + 2 * outside) * (10 + 2 * outside) - (4 - Math.PI) * Math.Pow(3 + outside, 2);
        double innerArea = (20 - 2 * inside) * (10 - 2 * inside) - (4 - Math.PI) * Math.Pow(3 - inside, 2);
        Assert.InRange(Area(f), (outerArea - innerArea) * .97, (outerArea - innerArea) * 1.03);
    }
    [Theory]
    [InlineData(StrokeSpace.Local, 2f)]
    [InlineData(StrokeSpace.Framebuffer, 2f)]
    [InlineData(StrokeSpace.Local, 200000f)]
    [InlineData(StrokeSpace.Framebuffer, 200000f)]
    public void ZeroRadiusOutlineMatchesRectangleWithoutCurveLimits(StrokeSpace space, float width)
    {
        using var rounded = new ShapeDrawList();
        using var rectangle = new ShapeDrawList();
        rounded.BeginFrame(100, 100);
        rectangle.BeginFrame(100, 100);
        rounded.StrokeRoundedRect(V(0, 0), V(10, 10), 0, width, White, StrokeAlignment.Outside, space);
        rectangle.StrokeRect(V(0, 0), V(10, 10), width, White, StrokeAlignment.Outside, space);
        var a = rounded.Finish(); var b = rectangle.Finish();
        Assert.Equal(Area(b), Area(a), 5);
        var actual = a.Vertices.ToArray().Select(v => v.Position).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        var expected = b.Vertices.ToArray().Select(v => v.Position).Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        Assert.Equal(expected, actual);
    }
    [Fact]
    public void ConsumedCircleAndRoundedHoleBecomeSolid()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokeCircle(V(0, 0), 2, 4, White, StrokeAlignment.Inside);
        var f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 11, 4 * Math.PI);
        d.BeginFrame(100, 100); d.StrokeRoundedRect(V(0, 0), V(20, 10), 3, 5, White, StrokeAlignment.Inside);
        f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 190, 193);
        d.BeginFrame(100, 100); d.StrokeRoundedRect(V(0, 0), V(20, 10), 1, 2, White, StrokeAlignment.Inside);
        f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 102, 104);
    }
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(-2, 3)]
    [InlineData(.1, .2)]
    public void FramebufferWidthIsConstantUnderShearZoomAndReflection(float sx, float sy)
    {
        float3x2 transform = new(sx, sx * .7f, 0, sy, 50, 50);
        using var d = new ShapeDrawList(); d.BeginFrame(1000, 1000, transform);
        d.StrokeRect(V(0, 0), V(100, 100), 2, White, space: StrokeSpace.Framebuffer);
        var f = d.Finish(); Tiled(f);
        double perimeter = 200 * (Math.Sqrt(sx * sx + sx * sx * .7 * .7) + Math.Abs(sy));
        Assert.InRange(Area(f), perimeter * 2 - .02, perimeter * 2 + .02);
        float2 Pixel(float x, float y) => V(x * transform.M11 + y * transform.M21 + 50, x * transform.M12 + y * transform.M22 + 50);
        float2[] boundary = [Pixel(0, 0), Pixel(100, 0), Pixel(100, 100), Pixel(0, 100)];
        foreach (var v in f.Vertices.Span)
        {
            double min = double.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                var a = boundary[i]; var b = boundary[(i + 1) % 4];
                double len = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
                min = Math.Min(min, Math.Abs(Cross(a, b, v.Position)) / len);
            }
            Assert.InRange(min, .9999, 1.0001);
        }
        d.BeginFrame(1000, 1000, transform); d.StrokeRect(V(0, 0), V(100, 100), 2, White);
        f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 800 * Math.Abs(sx * sy) - .02, 800 * Math.Abs(sx * sy) + .02);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurvedFramebufferOffsetsFollowTransformedBoundaryNotScalarRadius(bool rounded)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(1000, 1000, new(3, .7f, 1.2f, .8f, 50, 50));
        if (rounded) d.StrokeRoundedRect(V(-10, -10), V(20, 20), 10, 2, White, space: StrokeSpace.Framebuffer);
        else d.StrokeCircle(V(0, 0), 10, 2, White, space: StrokeSpace.Framebuffer);
        var f = d.Finish(); Tiled(f);
        double extent = 10 * Math.Sqrt(9 + 1.2 * 1.2);
        Assert.InRange(f.Vertices.ToArray().Max(v => v.Position.X), 50 + extent + .8, 50 + extent + 1.2);
        Assert.InRange(f.Vertices.ToArray().Min(v => v.Position.X), 50 - extent - 1.2, 50 - extent - .8);
    }
    [Fact]
    public void ValidationRunsBeforeHiddenNoopsAndAllNoopsPreserveOutput()
    {
        using var d = new ShapeDrawList();
        Assert.Throws<InvalidOperationException>(() => d.StrokeCircle(V(0, 0), 1, 1, White));
        d.BeginFrame(100, 100); d.FillRect(V(0, 0), V(1, 1), White);
        var hidden = new LinearColor(1, 1, 1, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokeCircle(V(0, 0), -1, 0, hidden));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokeRect(V(0, 0), V(1, 1), -1, hidden));
        Assert.Throws<ArgumentException>(() => d.StrokeTriangle(V(float.NaN, 0), V(1, 0), V(0, 1), 1, hidden));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokeRect(V(0, 0), V(1, 1), 1, hidden, (StrokeAlignment)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokeCircle(V(0, 0), 1, 1, hidden, space: (StrokeSpace)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokeRoundedRect(V(0, 0), V(-1, 1), 1, 1, hidden));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokeCircle(V(0, 0), float.PositiveInfinity, 1, hidden));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokeClosedPolyline(new float2[1025], 0, hidden));
        d.StrokeCircle(V(0, 0), 0, 10, White); d.StrokeRect(V(0, 0), V(0, 10), 10, White);
        d.StrokeRoundedRect(V(0, 0), V(10, 0), 1, 10, White); d.StrokeTriangle(V(0, 0), V(0, 0), V(1, 1), 1, White);
        d.StrokeClosedPolyline([V(0, 0), V(1, 1)], 1, White);
        d.StrokeCircle(V(0, 0), float.MaxValue, 1, hidden);
        d.PushClip(new(0, 0, 0, 0)); d.StrokeCircle(V(0, 0), float.MaxValue, 1, White); d.PopClip();
        d.SetTransform(new(1, 0, 0, 0, 0, 0)); d.StrokeCircle(V(0, 0), float.MaxValue, 1, White);
        var f = d.Finish(); Assert.Equal(1, Area(f)); Assert.Equal(4, f.Vertices.Length);
    }
    [Fact]
    public void PrecisionAndTessellationFailuresRemainTransactional()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.FillRect(V(0, 0), V(1, 1), White);
        Assert.Throws<ArgumentException>(() => d.StrokeCircle(V(0, 0), 200000, 1, White));
        Assert.Throws<ArgumentException>(() => d.StrokeRect(V(1048576, 0), V(10, 10), 1, White));
        d.SetTransform(new(1, 0, 0, 1, 1000, 1000));
        Assert.Throws<ArgumentException>(() => d.StrokeRect(V(0, 0), V(10, 10), .000001f, White));
        var f = d.Finish(); Assert.Equal(1, Area(f)); Assert.Equal(4, f.Vertices.Length);
    }
    [Fact]
    public void ExactTriangleInradiusConsumesHole()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokeTriangle(V(0, 0), V(6, 0), V(0, 8), 2, White, StrokeAlignment.Inside);
        var f = d.Finish(); Assert.Equal(24, Area(f), 5); Tiled(f);
        d.BeginFrame(100, 100);
        d.StrokeTriangle(V(0, 0), V(6, 0), V(0, 8), 1.999f, White, StrokeAlignment.Inside);
        f = d.Finish(); Tiled(f); Hollow(f, V(2, 2)); Assert.InRange(Area(f), 23.99, 24);
    }
    [Fact]
    public void MaximumConvexContourIsBoundedAndAccepted()
    {
        var points = Enumerable.Range(0, 1024).Select(i => V((float)(100 * Math.Cos(i * Math.Tau / 1024)), (float)(100 * Math.Sin(i * Math.Tau / 1024)))).ToArray();
        using var d = new ShapeDrawList(); d.BeginFrame(1000, 1000);
        d.StrokeClosedPolyline(points, 1, White);
        var f = d.Finish(); Assert.InRange(Area(f), 628, 629); Assert.InRange(f.Vertices.Length, 1, 4096);
    }
}
