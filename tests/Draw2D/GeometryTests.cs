using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;
using System.Runtime.InteropServices;
using Xunit;

namespace Shapes.Tests;

public class GeometryTests
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
    [Fact]
    public void UnrepresentableThinShearRejectsTransactionally()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.FillRect(V(0, 0), V(1, 1), White);
        d.SetTransform(new(1, 1.23456f, 0, 1e-6f, 100, 100));
        var error = Assert.Throws<ArgumentException>(() => d.FillDisc(V(0, 0), 10, White)); Assert.Contains("FLOAT32", error.Message);
        var f = d.Finish(); Assert.Equal(4, f.Vertices.Length); Assert.Equal(6, f.Indices.Length); Assert.Equal(1, Area(f));
    }
    [Theory]
    [InlineData(1f, .001f, 100f)]
    [InlineData(-1f, .001f, 100f)]
    [InlineData(.001f, .001f, 0f)]
    [InlineData(10f, 20f, 0f)]
    public void RepresentableThinShearReflectionAndZoomDoNotFalseReject(float scale, float thickness, float offset)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100, new(scale, scale * 1.23456f, 0, thickness, offset, offset)); d.FillDisc(V(0, 0), 10, White); var f = d.Finish();
        Assert.True(Area(f) > 0); Tiled(f);
    }
    [Fact]
    public void QuantizedPolylineGapMustRemainSimple()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(2000, 2000); d.FillRect(V(0, 0), V(1, 1), White); d.SetTransform(float3x2.CreateTranslation(1000, 1000));
        var error = Assert.Throws<ArgumentException>(() => d.StrokePolyline([V(0, 0), V(10, 0), V(10, 1.00001f), V(0, 1.00001f)], 1, White)); Assert.Contains("FLOAT32", error.Message);
        var f = d.Finish(); Assert.Equal(4, f.Vertices.Length); Assert.Equal(1, Area(f));
        d.BeginFrame(2000, 2000); d.SetTransform(float3x2.CreateTranslation(1000, 1000));
        d.StrokePolyline([V(0, 0), V(10, 0), V(10, 1.001f), V(0, 1.001f)], 1, White); f = d.Finish(); Assert.True(Area(f) > 20); Tiled(f);
    }
    [Fact]
    public void FrozenAbiAndColors()
    {
        Assert.Equal(8, Marshal.SizeOf<float2>()); Assert.Equal(16, Marshal.SizeOf<float4>());
        Assert.Equal(24, Marshal.SizeOf<ShapeVertex>()); Assert.Equal(0, Marshal.OffsetOf<ShapeVertex>(nameof(ShapeVertex.Position)).ToInt32()); Assert.Equal(8, Marshal.OffsetOf<ShapeVertex>(nameof(ShapeVertex.Color)).ToInt32());
        Assert.InRange(LinearColor.FromSrgb(.5f, 0, 1).R, .21404f, .21405f);
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.FillRect(V(0, 0), V(1, 1), new(.8f, .4f, .2f, .5f)); var c = d.Finish().Vertices.Span[0].Color;
        Assert.Equal(.4f, c.X); Assert.Equal(.2f, c.Y); Assert.Equal(.1f, c.Z); Assert.Equal(.5f, c.W);
    }
    [Fact]
    public void PublishedDiskProbeIsPlanarIndexedTiledAndCoversCircle()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.FillDisc(V(20, 30), 10, White); var f = d.Finish();
        Assert.InRange(Area(f), 306, Math.PI * 100); Tiled(f);
        Assert.True(f.Indices.Length >= 24);
        foreach (var p in f.Vertices.Span) { double r = Math.Sqrt(Math.Pow(p.Position.X - 20, 2) + Math.Pow(p.Position.Y - 30, 2)); Assert.True(r < .0001 || Math.Abs(r - 10) < .0001); }
        Assert.Contains(f.Vertices.ToArray(), v => v.Position.X < 11); Assert.Contains(f.Vertices.ToArray(), v => v.Position.X > 29);
    }
    [Fact]
    public void RingHasHoleAndNoDoubleBlending()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.FillRing(V(30, 30), 5, 10, new(1, 0, 0, .3f)); var f = d.Finish();
        Assert.InRange(Area(f), 230, Math.PI * 75); Tiled(f);
        var vs = f.Vertices.Span; var ids = f.Indices.Span;
        for (int i = 0; i < ids.Length; i += 3) { var a = vs[(int)ids[i]].Position; var b = vs[(int)ids[i + 1]].Position; var c = vs[(int)ids[i + 2]].Position; var p = V(30, 30); double x = Cross(a, b, p), y = Cross(b, c, p), z = Cross(c, a, p); Assert.False((x >= 0 && y >= 0 && z >= 0) || (x <= 0 && y <= 0 && z <= 0)); }
    }
    [Theory]
    [InlineData(LineCap.Butt, 40)]
    [InlineData(LineCap.Square, 44)]
    [InlineData(LineCap.Round, 43)]
    public void SegmentCapsAreSingleTiledShapes(LineCap cap, double expected)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.StrokeSegment(V(10, 10), V(30, 10), 2, cap, White); var f = d.Finish(); Assert.InRange(Area(f), expected - .4, expected + .2); Tiled(f);
        float min = f.Vertices.ToArray().Min(v => v.Position.X); Assert.Equal(cap == LineCap.Butt ? 10 : 9, min, 3);
    }
    [Fact]
    public void RoundedRectClampsAndTiles()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.FillRoundedRect(V(0, 0), V(20, 10), 100, White); var f = d.Finish(); Assert.InRange(Area(f), 174, 179); Tiled(f);
    }
    [Fact]
    public void PolylineDuplicatesCollinearAndBevelArea()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.StrokePolyline([V(0, 0), V(0, 0), V(5, 0), V(10, 0), V(10, 10)], 2, White); var f = d.Finish(); Assert.Equal(39.5, Area(f), 5); Tiled(f);
        d.BeginFrame(100, 100); d.StrokePolyline([V(10, 10), V(10, 0), V(0, 0)], 2, White); f = d.Finish(); Assert.Equal(39.5, Area(f), 5); Tiled(f);
    }
    [Fact]
    public void ReversalCrossingsAndOverlappingOutlinesRejected()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        Assert.Throws<ArgumentException>(() => d.StrokePolyline([V(0, 0), V(10, 0), V(5, 0)], 2, White));
        Assert.Throws<ArgumentException>(() => d.StrokePolyline([V(0, 0), V(10, 10), V(0, 10), V(10, 0)], 2, White));
        Assert.Throws<ArgumentException>(() => d.StrokePolyline([V(0, 0), V(10, 0), V(10, 1), V(0, 1)], 2, White));
        Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Fact]
    public void TransformConventionMatchesBrutalLocalThenWorld()
    {
        var l = new float3x2(2, 1, -1, 3, 5, 7); var w = new float3x2(1, -2, 3, 1, 20, 30);
        using var d = new ShapeDrawList(); d.BeginFrame(500, 500, w); d.SetTransform(l); d.FillTriangle(V(1, 2), V(3, 2), V(1, 4), White); var f = d.Finish();
        Assert.Equal(float2.Transform(float2.Transform(V(1, 2), l), w), f.Vertices.Span[0].Position);
        Assert.Equal(2 * 7 * 7, Area(f), 5);
    }
    [Theory]
    [InlineData(-1, 0, 0, 1)]
    [InlineData(1, 2, 0, 1)]
    public void ReflectionsAndShearsTile(float a, float b, float c, float e)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.SetTransform(new(a, b, c, e, 0, 0)); d.FillRect(V(0, 0), V(10, 20), White); var f = d.Finish(); Assert.Equal(200, Area(f), 4); Tiled(f);
    }
    [Fact]
    public void SingularTransformsEmitNothing()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100, new(1, 3, 2, 6, 0, 0)); d.FillDisc(V(0, 0), 10, White); d.FillRect(V(0, 0), V(3, 7), White); Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Fact]
    public void CurvesMeetTransformedSagittaUnderShear()
    {
        using var d = new ShapeDrawList(); var m = new float3x2(3, 2, 1, 4, 100, 100); d.BeginFrame(1000, 1000, m); d.FillDisc(V(0, 0), 70, White); var f = d.Finish(); var vs = f.Vertices.Span; var ids = f.Indices.Span;
        // Published triangles are boundary, boundary, center. Compare the transformed analytic arc midpoint to its chord midpoint.
        for (int i = 0; i < ids.Length; i += 3)
        {
            var a = vs[(int)ids[i]].Position; var b = vs[(int)ids[i + 1]].Position;
            Assert.True(float3x2.Invert(m, out var inv)); var la = float2.Transform(a, inv); var lb = float2.Transform(b, inv);
            double x = la.X + lb.X, y = la.Y + lb.Y, len = Math.Sqrt(x * x + y * y); var mid = float2.Transform(V((float)(70 * x / len), (float)(70 * y / len)), m);
            double dx = mid.X - (a.X + b.X) / 2, dy = mid.Y - (a.Y + b.Y) / 2; Assert.True(Math.Sqrt(dx * dx + dy * dy) <= .25);
        }
    }
    [Fact]
    public void ClipsAreNestedInvariantAndDrawOrderStable()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(20, 20); d.FillRect(V(0, 0), V(5, 5), new(1, 0, 0, .5f));
        d.SetTransform(float3x2.CreateTranslation(100, 200)); d.PushClip(new(2, 3, 18, 19)); d.PushClip(new(-10, 5, 10, 30)); d.FillRect(V(0, 0), V(5, 5), new(0, 1, 0, .5f)); d.PopClip(); d.PopClip();
        d.FillRect(V(0, 0), V(5, 5), new(0, 0, 1, .5f)); d.FillRect(V(0, 0), V(5, 5), White); var f = d.Finish();
        Assert.Equal(3, f.Commands.Length); Assert.Equal(new PixelRect(2, 5, 10, 19), f.Commands.Span[1].Clip); Assert.Equal(12, f.Commands.Span[2].IndexCount);
        Assert.Equal(.5f, f.Vertices.Span[0].Color.X); Assert.Equal(.5f, f.Vertices.Span[4].Color.Y); Assert.Equal(.5f, f.Vertices.Span[8].Color.Z);
        Assert.Equal(V(100, 200), f.Vertices.Span[4].Position);
    }
    [Fact]
    public void EmptyClipsAndDegenerateInputsEmitNothing()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(20, 20); d.PushClip(new(50, 50, 70, 70)); d.FillDisc(V(2, 2), 4, White); d.PopClip();
        d.FillDisc(V(1, 1), 0, White); d.FillRing(V(1, 1), 2, 2, White); d.FillRect(V(1, 1), V(0, 2), White); d.FillTriangle(V(0, 0), V(1, 1), V(2, 2), White);
        d.FillRoundedRect(V(0, 0), V(0, 3), 2, White); d.StrokeSegment(V(1, 1), V(1, 1), 2, LineCap.Square, White); d.StrokeSegment(V(1, 1), V(4, 4), 0, LineCap.Round, White);
        d.FillRect(V(0, 0), V(4, 4), new(1, 1, 1, 0)); d.StrokePolyline([], 1, White); Assert.Empty(d.Finish().Indices.ToArray());
        d.BeginFrame(20, 20); d.StrokeSegment(V(5, 5), V(5, 5), 4, LineCap.Round, White); Assert.InRange(Area(d.Finish()), 11, 13);
    }
    [Fact]
    public void AllInvalidInputsAreTransactional()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.FillRect(V(0, 0), V(2, 3), White);
        Action[] bad = [() => d.FillDisc(V(float.NaN, 0), 1, White), () => d.FillDisc(V(0, 0), -1, White), () => d.FillRing(V(0, 0), 2, 1, White), () => d.FillRect(V(0, 0), V(-1, 1), White), () => d.FillRoundedRect(V(0, 0), V(1, 1), float.PositiveInfinity, White), () => d.FillTriangle(V(0, 0), V(0, 1), V(1, 0), new(2, 0, 0, 0)), () => d.StrokeSegment(V(0, 0), V(1, 1), -1, LineCap.Butt, White), () => d.StrokeSegment(V(0, 0), V(1, 1), 1, (LineCap)99, White), () => d.StrokePolyline(new float2[1025], 1, White), () => d.FillDisc(V(0, 0), 1_000_000, White), () => d.FillRect(V(1048576, 0), V(1, 1), White), () => d.SetTransform(new(float.NaN, 0, 0, 1, 0, 0)), () => d.PushClip(new(2, 0, 1, 2)), () => d.PopClip()];
        foreach (var action in bad) Assert.ThrowsAny<Exception>(action);
        var f = d.Finish(); Assert.Equal(4, f.Vertices.Length); Assert.Equal(6, f.Indices.Length); Assert.Single(f.Commands.ToArray()); Assert.Equal(6, Area(f));
    }
    [Fact]
    public void BudgetsAndArithmeticAreBoundedAndTransactional()
    {
        using var d = new ShapeDrawList(7, 9); d.BeginFrame(100, 100); d.FillRect(V(0, 0), V(2, 3), White);
        Assert.Throws<InvalidOperationException>(() => d.FillRect(V(0, 0), V(2, 3), White)); d.FillTriangle(V(0, 0), V(1, 0), V(0, 1), White); var f = d.Finish(); Assert.Equal(7, f.Vertices.Length); Assert.Equal(9, f.Indices.Length);
        using var zero = new ShapeDrawList(0, 0); zero.BeginFrame(1, 1); zero.FillDisc(V(0, 0), 0, White); Assert.Throws<InvalidOperationException>(() => zero.FillTriangle(V(0, 0), V(1, 0), V(0, 1), White)); Assert.Empty(zero.Finish().Commands.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShapeDrawList(-1));
    }
    [Fact]
    public void NoOutputRuleStillValidatesEverySuppliedComponent()
    {
        using var d = new ShapeDrawList(0, 0); var clear = new LinearColor(1, 1, 1, 0); d.BeginFrame(100, 100);
        d.FillDisc(V(0, 0), 1e20f, clear); d.FillRing(V(0, 0), 1e20f, 1e20f, White);
        d.FillRect(V(1e30f, 0), V(0, 1e30f), White); d.FillTriangle(V(1e30f, 0), V(1e30f, 0), V(1e30f, 0), White);
        d.StrokePolyline([V(0, 0), V(1, 0), V(0, 0)], 0, White);
        d.PushClip(new(200, 200, 300, 300)); d.FillDisc(V(0, 0), 1e20f, White); d.PopClip();
        d.SetTransform(new(1e20f, 0, 0, 0, 0, 0)); d.FillDisc(V(0, 0), 1e20f, White);
        Assert.Throws<ArgumentException>(() => d.StrokePolyline([V(0, 0), V(1, 0), V(0, 0), V(float.NaN, 0)], 0, clear));
        Assert.Throws<ArgumentException>(() => d.FillTriangle(V(0, 0), V(0, 0), V(float.NaN, 0), clear));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokeSegment(V(0, 0), V(0, 0), 0, (LineCap)99, clear));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.FillDisc(V(0, 0), -1, clear));
        Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Fact]
    public void CurveResolutionIsBoundedAndMinimumEight()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(1, 1); d.FillDisc(V(0, 0), .0001f, White); var tiny = d.Finish(); Assert.Equal(24, tiny.Indices.Length);
        d.BeginFrame(1, 1); d.FillDisc(V(0, 0), 123000, White); var f = d.Finish(); Assert.InRange(f.Indices.Length / 3, 4000, 4096); Assert.True(Area(f) > 0);
        // Welded seam is exact even at large scale, where float sin(Tau) would visibly overlap.
        Assert.Equal(f.Vertices.Span[0].Position, f.Vertices.Span[^2].Position);
        d.BeginFrame(1, 1); Assert.Throws<ArgumentException>(() => d.FillDisc(V(0, 0), 130000, White)); Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Fact]
    public void LimitsEnvelopeAndFrameReset()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(1, 1); d.FillRect(V(1048575, 1048575), V(1, 1), White); Assert.Equal(1, Area(d.Finish()));
        d.BeginFrame(1, 1); Assert.Throws<ArgumentException>(() => d.FillRect(V(-1048576, -1048576), V(1, float.MaxValue), White));
        Assert.Throws<ArgumentException>(() => d.FillDisc(V(0, 0), float.MaxValue, White));
        d.FillRect(V(0, 0), V(1, 1), White); var f = d.Finish(); Assert.Equal(4, f.Vertices.Length); Assert.Equal(new PixelRect(0, 0, 1, 1), f.Commands.Span[0].Clip);
        using var byIndex = new ShapeDrawList(100, 5); byIndex.BeginFrame(1, 1); Assert.Throws<InvalidOperationException>(() => byIndex.FillRect(V(0, 0), V(1, 1), White)); Assert.Empty(byIndex.Finish().Vertices.ToArray());
    }
    [Fact]
    public void LongAndAlternatingPolylinesRemainFiniteAndTiled()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(20000, 100);
        var points = Enumerable.Range(0, 1024).Select(i => V(i * 10, i % 2)).ToArray();
        d.StrokePolyline(points, .25f, White); var f = d.Finish(); Assert.True(Area(f) > 2500); Assert.True(f.Vertices.Length < 4096);
        d.BeginFrame(100, 100); d.StrokePolyline([V(0, 0), V(10, 0), V(10, 10), V(20, 10), V(20, 20), V(30, 20)], 2, White); f = d.Finish(); Assert.Equal(98, Area(f), 4); Tiled(f);
    }
    [Fact]
    public void FailedFrameAndDerivedGeometryDoNotChangePriorOutput()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.FillTriangle(V(0, 0), V(1, 0), V(0, 1), White);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.BeginFrame(-1, 10));
        Assert.Throws<ArgumentException>(() => d.BeginFrame(20, 20, new(float.NaN, 0, 0, 1, 0, 0)));
        Assert.Throws<ArgumentException>(() => d.StrokePolyline([V(0, 0), V(10, 10), V(0, 10), V(10, 0)], 2, White));
        d.FillTriangle(V(5, 5), V(6, 5), V(5, 6), White); var f = d.Finish(); Assert.Equal(100, f.Width); Assert.Equal(6, f.Vertices.Length); Assert.Equal(1, Area(f)); Assert.Equal(V(0, 0), f.Vertices.Span[0].Position); Assert.Equal(V(5, 5), f.Vertices.Span[3].Position);
        Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, f.Indices.ToArray());
    }
    [Fact]
    public void FrameAndDisposalState()
    {
        var d = new ShapeDrawList(); Assert.Throws<InvalidOperationException>(() => d.Finish()); Assert.Throws<InvalidOperationException>(() => d.FillDisc(V(0, 0), 1, White)); Assert.Throws<ArgumentOutOfRangeException>(() => d.BeginFrame(0, 10));
        d.BeginFrame(10, 10); d.PushClip(new(0, 0, 1, 1)); Assert.Throws<InvalidOperationException>(() => d.Finish()); d.PopClip(); d.Finish(); Assert.Throws<InvalidOperationException>(() => d.PopClip());
        d.BeginFrame(20, 30); var f = d.Finish(); Assert.Equal(20, f.Width); Assert.Equal(30, f.Height); Assert.Empty(f.Vertices.ToArray());
        d.Dispose(); d.Dispose(); Assert.Throws<ObjectDisposedException>(() => d.BeginFrame(1, 1)); Assert.Throws<ObjectDisposedException>(() => d.SetTransform(float3x2.Identity));
    }
}
