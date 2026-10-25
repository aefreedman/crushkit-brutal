using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;
using Xunit;

namespace Shapes.Tests;

public class DecorativeDashTests
{
    private static readonly LinearColor Half = new(1, 1, 1, .5f);
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
    [Theory]
    [InlineData(-1.0)] [InlineData(1.0)]
    public void AngledAnalyticGlyph(double modifier)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedSegment(V(10, 20), V(30, 20), 4,
            StrokePattern.Dashes(4, 16, .5).WithShape(DashShape.Angled, modifier), Half);
        var f = d.Finish(); Tiled(f); Assert.Equal(16, Area(f), 6);
        foreach (var p in new[] { V((float)(20 - 2 * modifier), 18), V((float)(24 - 2 * modifier), 18),
            V((float)(20 + 2 * modifier), 22), V((float)(24 + 2 * modifier), 22) })
            Assert.Contains(f.Vertices.ToArray(), v => v.Position == p);
        Assert.All(f.Vertices.ToArray(), v => Assert.Equal(.5f, v.Color.W));
    }

    [Theory]
    [InlineData(DashShape.Angled, false)] [InlineData(DashShape.Angled, true)]
    [InlineData(DashShape.Chevron, false)] [InlineData(DashShape.Chevron, true)]
    public void AcrossCornerAndSeam(DashShape shape, bool reverse)
    {
        float2[] open = [V(10, 10), V(30, 10), V(30, 30)];
        float2[] loop = [V(10, 10), V(30, 10), V(30, 30), V(10, 30)];
        if (reverse) { Array.Reverse(open); Array.Reverse(loop); }
        foreach (bool closed in new[] { false, true })
        foreach (double modifier in new[] { -1.0, 1.0 })
        {
            using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
            d.StrokePatternedPolyline(closed ? loop : open, 2,
                StrokePattern.Dashes(24, 16, closed ? .75 : .1, true).WithShape(shape, modifier), Half, closed);
            var f = d.Finish(); Assert.True(Area(f) > 0); Tiled(f);
        }
    }

    [Theory]
    [InlineData(DashShape.Angled, -1.0)] [InlineData(DashShape.Angled, 1.0)]
    [InlineData(DashShape.Chevron, -1.0)] [InlineData(DashShape.Chevron, 1.0)]
    public void ShortGlyphsAndOpenEndpointClipping(DashShape shape, double modifier)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedSegment(V(10, 20), V(18, 20), 4,
            StrokePattern.Dashes(.5, 1.5).WithShape(shape, modifier), Half);
        var f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 7.99999, 8.00001);
        Assert.All(f.Vertices.ToArray(), v => Assert.InRange(v.Position.X, 10, 18));
        Assert.Contains(f.Vertices.ToArray(), v => v.Position.X == 10);
        Assert.Contains(f.Vertices.ToArray(), v => v.Position.X == 18);
    }

    [Theory]
    [InlineData(DashShape.Angled, false)] [InlineData(DashShape.Angled, true)]
    [InlineData(DashShape.Chevron, false)] [InlineData(DashShape.Chevron, true)]
    public void BasicFallbackAtCorner(DashShape shape, bool zeroGap)
    {
        ShapeVertex[]? vertices = null; uint[]? indices = null;
        foreach (bool decorative in new[] { false, true })
        {
            using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
            var p = StrokePattern.Dashes(24, zeroGap ? 0 : 8);
            if (decorative) p = p.WithShape(shape, zeroGap ? 1 : 0);
            d.StrokePatternedPolyline([V(10, 10), V(30, 10), V(30, 30)], 2, p, Half);
            var f = d.Finish();
            if (!decorative) { vertices = f.Vertices.ToArray(); indices = f.Indices.ToArray(); }
            else { Assert.Equal(vertices, f.Vertices.ToArray()); Assert.Equal(indices, f.Indices.ToArray()); }
        }
    }

    [Theory]
    [InlineData(DashShape.Angled)] [InlineData(DashShape.Chevron)]
    public void SampledCircle(DashShape shape)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(200, 200);
        d.StrokePatternedCircle(V(80, 80), 40, 2, StrokePattern.FixedDashes(8, .5, .9).WithShape(shape), Half);
        var f = d.Finish(); Assert.InRange(Area(f), 240, 260); Tiled(f);
    }

    [Theory]
    [InlineData(-1.0)] [InlineData(1.0)] [InlineData(.5)]
    public void ChevronAnalyticConcaveGlyph(double modifier)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedSegment(V(10, 20), V(30, 20), 4,
            StrokePattern.Dashes(4, 16, .5).WithShape(DashShape.Chevron, modifier), Half);
        var f = d.Finish(); Tiled(f); Assert.Equal(16, Area(f), 6);
        foreach (var p in new[] { V(20, 20), V(24, 20), V((float)(20 + 2 * modifier), 18),
            V((float)(20 + 2 * modifier), 22), V((float)(24 + 2 * modifier), 18), V((float)(24 + 2 * modifier), 22) })
            Assert.Contains(f.Vertices.ToArray(), v => v.Position == p);
        // A point in the V's missing wedge is not covered by any triangle.
        double notchX = modifier > 0 ? 20 + modifier : 24 + modifier;
        var vs = f.Vertices.ToArray(); var ids = f.Indices.ToArray();
        for (int i = 0; i < ids.Length; i += 3)
        {
            var p = V((float)notchX, 18.25f);
            double a = Cross(vs[ids[i]].Position, vs[ids[i + 1]].Position, p);
            double b = Cross(vs[ids[i + 1]].Position, vs[ids[i + 2]].Position, p);
            double c = Cross(vs[ids[i + 2]].Position, vs[ids[i]].Position, p);
            Assert.False((a > 0 && b > 0 && c > 0) || (a < 0 && b < 0 && c < 0));
        }
        Assert.All(vs, v => Assert.Equal(.5f, v.Color.W));
    }

    [Theory]
    [InlineData(DashShape.Angled)] [InlineData(DashShape.Chevron)]
    public void SizingAndPhaseProduceSameGeometry(DashShape shape)
    {
        foreach (double phase in new[] { -.25, .75, 8.75, 1e300, -1e300 })
        {
            double normalized = phase % 1; if (normalized < 0) normalized += 1;
            StrokePattern[] patterns = [StrokePattern.Dashes(4, 4, normalized),
                StrokePattern.Dashes(4, 4, phase), StrokePattern.RelativeDashes(2, 2, phase),
                StrokePattern.FixedDashes(4, .5, phase)];
            ShapeVertex[]? vertices = null; uint[]? indices = null;
            foreach (var p in patterns)
            {
                using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
                d.StrokePatternedSegment(V(10, 20), V(42, 20), 2, p.WithShape(shape, .5), Half);
                var f = d.Finish(); Tiled(f);
                if (vertices == null) { vertices = f.Vertices.ToArray(); indices = f.Indices.ToArray(); }
                else { Assert.Equal(vertices, f.Vertices.ToArray()); Assert.Equal(indices, f.Indices.ToArray()); }
            }
        }
    }

    [Theory]
    [InlineData(DashShape.Angled, StrokeSpace.Local)] [InlineData(DashShape.Angled, StrokeSpace.Framebuffer)]
    [InlineData(DashShape.Chevron, StrokeSpace.Local)] [InlineData(DashShape.Chevron, StrokeSpace.Framebuffer)]
    public void ReflectionShearAndPixelWidth(DashShape shape, StrokeSpace space)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(200, 200);
        d.SetTransform(new float3x2(-3, 0, 2, 2, 100, 0));
        d.StrokePatternedSegment(V(10, 10), V(30, 10), 4,
            StrokePattern.Dashes(space == StrokeSpace.Local ? 4 : 12, space == StrokeSpace.Local ? 16 : 48, .5)
                .WithShape(shape), Half, space: space);
        var f = d.Finish(); Tiled(f);
        Assert.Equal(space == StrokeSpace.Local ? 96 : 48, Area(f), 5);
        Assert.Equal(space == StrokeSpace.Local ? 8 : 4,
            f.Vertices.ToArray().Max(v => v.Position.Y) - f.Vertices.ToArray().Min(v => v.Position.Y));
        if (space == StrokeSpace.Local)
        {
            var originals = shape == DashShape.Angled ? new[] { V(18, 8), V(22, 8), V(22, 12), V(26, 12) }
                : new[] { V(20, 10), V(24, 10), V(22, 8), V(26, 8), V(22, 12), V(26, 12) };
            foreach (var p in originals)
                Assert.Contains(f.Vertices.ToArray(), v => v.Position == V(-3 * p.X + 2 * p.Y + 100, 2 * p.Y));
        }
    }

    [Theory]
    [InlineData(DashShape.Angled)] [InlineData(DashShape.Chevron)]
    public void LateFailuresRestoreMergedCommand(DashShape shape)
    {
        foreach (bool precision in new[] { false, true })
        {
            using var d = precision ? new ShapeDrawList() : new ShapeDrawList(20, 60);
            d.BeginFrame(100, 100); d.FillTriangle(V(1, 1), V(2, 1), V(1, 2), Half);
            if (precision)
                Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(1048500, 10), V(1048600, 10),
                    2, StrokePattern.Dashes(10, 10).WithShape(shape), Half));
            else
                Assert.Throws<InvalidOperationException>(() => d.StrokePatternedSegment(V(10, 10), V(50, 10),
                    2, StrokePattern.Dashes(4, 4).WithShape(shape), Half));
            var f = d.Finish(); Assert.Equal(3, f.Vertices.Length); Assert.Equal(3, f.Indices.Length);
            Assert.Equal(3, Assert.Single(f.Commands.ToArray()).IndexCount);
        }
    }

    [Theory]
    [InlineData(DashShape.Angled)] [InlineData(DashShape.Chevron)]
    public void ValidationAndTightTurnsRejectTransactionally(DashShape shape)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        var p = StrokePattern.Dashes(15, 6).WithShape(shape);
        Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline([V(10, 10), V(30, 10), V(30, 11), V(10, 11)], 4, p, Half));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(0, 0), V(100, 0), 1,
            StrokePattern.Dashes(1e-300, 1e-300).WithShape(shape), Half));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedCircle(V(0, 0), 1e6f, 1, p, Half));
        var hidden = new LinearColor(1, 1, 1, 0);
        Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline([V(0, 0), V(0, 0), V(float.NaN, 1)], 0, p, hidden));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokePatternedSegment(V(0, 0), V(0, 0), 0, p, hidden, space: (StrokeSpace)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokePatternedPolyline(new float2[1025], 0, p, hidden));
        d.PushClip(new(0, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedCircle(V(float.PositiveInfinity, 0), 0, 0, p, Half));
        d.PopClip(); Assert.Empty(d.Finish().Indices.ToArray());
    }

    [Theory]
    [InlineData(DashShape.Angled)] [InlineData(DashShape.Chevron)]
    public void ActualFloatPrecisionAndInterFragmentOverlapReject(DashShape shape)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(200, 200);
        d.FillTriangle(V(1, 1), V(2, 1), V(1, 2), Half);
        var error = Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline(
            [V(10, 10), V(50, 10), V(50, 30), V(10, 30), V(10, 11), V(40, 11)],
            2, StrokePattern.Dashes(30, 10).WithShape(shape), Half));
        Assert.Contains("overlap", error.Message);
        d.SetTransform(new(1, 1.23456f, 0, 1e-6f, 100, 100));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(0, 0), V(40, 0), 4,
            StrokePattern.Dashes(4, 4).WithShape(shape), Half));
        var f = d.Finish(); Assert.Equal(3, f.Vertices.Length);
        Assert.Equal(3, Assert.Single(f.Commands.ToArray()).IndexCount);
    }

    [Theory]
    [InlineData(DashShape.Angled)] [InlineData(DashShape.Chevron)]
    public void LimitsAndComplexityAreBounded(DashShape shape)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(20000, 100);
        d.StrokePatternedSegment(V(0, 10), V(8192, 10), 1,
            StrokePattern.FixedDashes(4096, .5, .25).WithShape(shape, .5), Half);
        Assert.InRange(Area(d.Finish()), 4095.5, 4096.5); // accumulated FLOAT32 area quantization
        d.BeginFrame(20000, 100);
        d.StrokePatternedPolyline(Enumerable.Range(0, 1024).Select(i => V(i, 10)).ToArray(), 1,
            StrokePattern.Dashes(4, 4).WithShape(shape), Half);
        Assert.True(Area(d.Finish()) > 500);
        d.BeginFrame(20000, 100);
        d.FillTriangle(V(1, 1), V(2, 1), V(1, 2), Half);
        // Many short curved edges times many intervals exhaust the shared 4-million-candidate budget.
        var points = Enumerable.Range(0, 1024).Select(i => V(i * 10, i % 2)).ToArray();
        var error = Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline(points, .1f,
            StrokePattern.FixedDashes(2048).WithShape(shape), Half));
        Assert.Contains("complexity", error.Message, StringComparison.OrdinalIgnoreCase);
        var f = d.Finish(); Assert.Equal(3, f.Vertices.Length);
        Assert.Equal(3, Assert.Single(f.Commands.ToArray()).IndexCount);
    }

    [Theory]
    [InlineData(DashShape.Angled, StrokeSpace.Local)] [InlineData(DashShape.Angled, StrokeSpace.Framebuffer)]
    [InlineData(DashShape.Chevron, StrokeSpace.Local)] [InlineData(DashShape.Chevron, StrokeSpace.Framebuffer)]
    public void TransformedSampledCurves(DashShape shape, StrokeSpace space)
    {
        foreach (double modifier in new[] { -1.0, .5, 1.0 })
        {
            using var d = new ShapeDrawList(); d.BeginFrame(500, 500);
            d.SetTransform(new(-2, 0, 1, 3, 300, 0));
            d.StrokePatternedCircle(V(40, 40), 20, 2,
                StrokePattern.RelativeDashes(4, 4, .13, true).WithShape(shape, modifier), Half, space: space);
            var f = d.Finish(); Assert.True(Area(f) > 0); Tiled(f);
        }
    }

    [Fact]
    public void NearCollinearClippedCurveRejectsWithoutPrecisionRepair()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(500, 500);
        d.FillTriangle(V(1, 1), V(2, 1), V(1, 2), Half);
        d.SetTransform(new(-2, 0, 1, 3, 300, 0));
        var error = Assert.Throws<ArgumentException>(() => d.StrokePatternedCircle(V(40, 40), 20, 2,
            StrokePattern.RelativeDashes(4, 4, -.25, true).WithShape(DashShape.Chevron, -1), Half));
        Assert.Contains("FLOAT32", error.Message);
        var f = d.Finish(); Assert.Equal(3, f.Vertices.Length);
        Assert.Equal(3, Assert.Single(f.Commands.ToArray()).IndexCount);
    }

    [Theory]
    [InlineData(DashShape.Basic)] [InlineData(DashShape.Angled)] [InlineData(DashShape.Chevron)]
    public void StyleRetainsSampler(DashShape shape)
    {
        StrokePattern[] patterns = [StrokePattern.Dashes(3, 2, -1e300, true),
            StrokePattern.RelativeDashes(3, 2, -.25, true), StrokePattern.FixedDashes(5, .3, .75)];
        foreach (var p in patterns) foreach (double modifier in new[] { -1.0, 0, 1 })
        {
            var a = p.Sample(35, 2, true); var b = p.WithShape(shape, modifier).Sample(35, 2, true);
            Assert.Equal(a.Intervals, b.Intervals); Assert.Equal(a.Solid, b.Solid);
        }
    }

    [Fact]
    public void InvalidStylesReject()
    {
        var p = StrokePattern.Dashes(3, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => p.WithShape((DashShape)99));
        foreach (double m in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.01, 1.01 })
            foreach (var s in Enum.GetValues<DashShape>())
                Assert.Throws<ArgumentOutOfRangeException>(() => p.WithShape(s, m));
        foreach (var pDot in new[] { StrokePattern.Dots(3), StrokePattern.RelativeDots(3), StrokePattern.FixedDots(3) })
            foreach (var s in new[] { DashShape.Angled, DashShape.Chevron })
                Assert.Throws<ArgumentOutOfRangeException>(() => pDot.WithShape(s, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => default(StrokePattern).WithShape(DashShape.Basic));
    }

    [Theory]
    [InlineData(DashShape.Angled, LineCap.Square)] [InlineData(DashShape.Angled, LineCap.Round)]
    [InlineData(DashShape.Chevron, LineCap.Square)] [InlineData(DashShape.Chevron, LineCap.Round)]
    public void CapsRejectBeforeNoOutput(DashShape shape, LineCap cap)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        var p = StrokePattern.Dashes(3, 2).WithShape(shape, 0);
        Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(1, 1), V(1, 1), 0, p, new(0, 0, 0, 0), cap));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedCircle(V(1, 1), 0, 0, p, Half, cap));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline([], 0, p, Half, cap: cap));
        Assert.Empty(d.Finish().Indices.ToArray());
    }
}
