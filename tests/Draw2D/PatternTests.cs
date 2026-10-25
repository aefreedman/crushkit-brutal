using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;
using Xunit;

namespace Shapes.Tests;

public class PatternTests
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
    [InlineData(-2.25, .75)] [InlineData(10.75, .75)] [InlineData(1e300, 0)] [InlineData(-1e300, 0)]
    public void PhaseIsPeriodic(double phase, double normalized)
    {
        var a = StrokePattern.Dashes(3, 2, phase).Sample(20, 1, false);
        var b = StrokePattern.Dashes(3, 2, normalized).Sample(20, 1, false);
        Assert.Equal(b.Intervals, a.Intervals);
        Assert.Equal(StrokePattern.Dots(5, normalized).Sample(20, 1, false).Sites,
            StrokePattern.Dots(5, phase).Sample(20, 1, false).Sites);
    }
    [Fact]
    public void RelativeAndFixedSemantics()
    {
        Assert.Equal(StrokePattern.Dashes(6, 4).Sample(30, 1, false).Intervals,
            StrokePattern.RelativeDashes(3, 2).Sample(30, 2, false).Intervals);
        Assert.Equal(new double[] { 0, 5, 10, 15 }, StrokePattern.FixedDots(4).Sample(20, 1, false).Sites);
        Assert.Equal(new double[] { 0, 5, 10, 15, 20 }, StrokePattern.Dots(5).Sample(20, 1, false).Sites);
        Assert.Equal(new double[] { 0, 10 }, StrokePattern.RelativeDots(5).Sample(15, 2, false).Sites);
        Assert.Equal(5, StrokePattern.FixedDashes(4, .5, .75).Sample(20, 1, false).Intervals.Count);
    }
    [Fact]
    public void ClosedFitAndSeam()
    {
        var s = StrokePattern.Dashes(3, 2, .8, true).Sample(22, 1, true).Intervals;
        Assert.Equal(4, s.Count); Assert.Equal(22.0 + 2.2, s[^1].End, 10);
        Assert.Equal(13.2, s.Sum(x => x.End - x.Start), 10);
        Assert.Equal(3, StrokePattern.Dots(4, fitClosed: true).Sample(10, 1, true).Sites.Count); // midpoint up
        Assert.Equal(4, StrokePattern.Dots(5).Sample(20, 1, true).Sites.Count);
    }
    [Theory]
    [InlineData(LineCap.Butt, 24)] [InlineData(LineCap.Square, 36)]
    public void DashCapsHaveExplicitExtensions(LineCap cap, double area)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedSegment(V(10, 10), V(28, 10), 2, StrokePattern.Dashes(4, 2), Half, cap);
        var f = d.Finish(); Assert.Equal(area, Area(f), 6); Tiled(f);
        Assert.Equal(cap == LineCap.Butt ? 10 : 9, f.Vertices.ToArray().Min(v => v.Position.X));
    }
    [Fact]
    public void RoundCapsAreTiledAndCircular()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedSegment(V(10, 10), V(30, 10), 2, StrokePattern.Dashes(4, 4), Half, LineCap.Round);
        var f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 30, 34);
        Assert.Contains(f.Vertices.ToArray(), v => v.Position.X < 10 && v.Position.Y != 9 && v.Position.Y != 11);
    }
    [Fact]
    public void PhaseContinuesAcrossCorner()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedPolyline([V(10, 10), V(16, 10), V(16, 20)], 2, StrokePattern.Dashes(8, 4), Half);
        var f = d.Finish(); Tiled(f);
        Assert.Contains(f.Vertices.ToArray(), v => v.Position.Y == 12);
        Assert.DoesNotContain(f.Vertices.ToArray(), v => v.Position.Y == 18); // no per-edge phase reset
        Assert.InRange(Area(f), 23, 24);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClosedSeamIsOneUncappedFragment(bool reverse)
    {
        float2[] points = [V(10, 10), V(30, 10), V(30, 30), V(10, 30)];
        if (reverse) Array.Reverse(points);
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedPolyline(points, 2, StrokePattern.FixedDashes(4, .5, .75), Half, true, LineCap.Round);
        var f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 88, 93);
    }
    [Theory]
    [InlineData(LineCap.Round)] [InlineData(LineCap.Square)]
    public void CapOverlapRejectsWholeCallIncludingMergedCommand(LineCap cap)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.FillTriangle(V(1, 1), V(2, 1), V(1, 2), Half);
        Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(10, 10), V(40, 10), 4, StrokePattern.Dashes(4, 1), Half, cap));
        var f = d.Finish(); Assert.Equal(3, f.Vertices.Length); Assert.Equal(3, f.Indices.Length);
        Assert.Equal(3, Assert.Single(f.Commands.ToArray()).IndexCount);
    }
    [Fact]
    public void LateBudgetFailureRestoresPriorCommand()
    {
        using var d = new ShapeDrawList(12, 30); d.BeginFrame(100, 100);
        d.FillTriangle(V(1, 1), V(2, 1), V(1, 2), Half);
        Assert.Throws<InvalidOperationException>(() => d.StrokePatternedSegment(V(10, 10), V(40, 10), 2, StrokePattern.Dashes(4, 4), Half));
        var f = d.Finish(); Assert.Equal(3, f.Vertices.Length); Assert.Equal(3, f.Indices.Length);
        Assert.Equal(3, Assert.Single(f.Commands.ToArray()).IndexCount);
    }
    [Fact]
    public void FramebufferDashWidthAndSpacingIgnoreShearZoom()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(200, 200);
        d.SetTransform(new float3x2(3, 0, 2, 2, 0, 0));
        d.StrokePatternedSegment(V(10, 10), V(20, 10), 2, StrokePattern.Dashes(4, 2), Half, space: StrokeSpace.Framebuffer);
        var f = d.Finish(); Tiled(f); Assert.Equal(40, Area(f), 5);
        Assert.Equal(2, f.Vertices.ToArray().Max(v => v.Position.Y) - f.Vertices.ToArray().Min(v => v.Position.Y));
    }
    [Fact]
    public void DashValidationPrecedesHiddenNoops()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); var hidden = new LinearColor(1, 1, 1, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokePatternedSegment(V(0, 0), V(0, 0), 0, default, hidden));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokePatternedSegment(V(0, 0), V(0, 0), 0, StrokePattern.Dashes(1, 1), hidden, (LineCap)99));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(float.NaN, 0), V(0, 0), 0, StrokePattern.Dashes(1, 1), hidden));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokePatternedPolyline(new float2[1025], 0, StrokePattern.Dashes(1, 1), hidden));
        d.StrokePatternedSegment(V(1, 1), V(1, 1), 2, StrokePattern.Dashes(1e-300, 1e-300), Half, LineCap.Round);
        Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Theory]
    [InlineData(false, 5)] [InlineData(true, 4)]
    public void DotsAreCircularAndEndpointPolicyIsExplicit(bool fixedCount, int count)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedSegment(V(10, 10), V(30, 10), 2, fixedCount ? StrokePattern.FixedDots(4) : StrokePattern.Dots(5), Half, LineCap.Square);
        var f = d.Finish(); Tiled(f); Assert.InRange(Area(f), count * 2.8, count * Math.PI);
        var vs = f.Vertices.ToArray();
        foreach (var v in vs)
        {
            double cx = 10 + 5 * Math.Round((v.Position.X - 10) / 5);
            Assert.Equal(1, Math.Sqrt(Math.Pow(v.Position.X - cx, 2) + Math.Pow(v.Position.Y - 10, 2)), 5);
        }
        Assert.Equal(fixedCount ? 26 : 31, vs.Max(v => v.Position.X));
    }
    [Fact]
    public void ClosedDotsHaveOneSeamSiteAndEitherWinding()
    {
        foreach (bool reverse in new[] { false, true })
        {
            float2[] points = [V(10, 10), V(30, 10), V(30, 30), V(10, 30)];
            if (reverse) Array.Reverse(points);
            using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
            d.StrokePatternedPolyline(points, 2, StrokePattern.FixedDots(4), Half, true);
            var f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 11.2, 4 * Math.PI);
            Assert.Equal(32, f.Vertices.Length);
        }
    }
    [Theory]
    [InlineData(StrokeSpace.Local)] [InlineData(StrokeSpace.Framebuffer)]
    public void DotMeasurementSpaceUnderShear(StrokeSpace space)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(200, 200);
        d.SetTransform(new float3x2(3, 0, 2, 2, 0, 0));
        d.StrokePatternedSegment(V(10, 10), V(20, 10), 2, StrokePattern.FixedDots(1), Half, space: space);
        var f = d.Finish(); Tiled(f);
        foreach (var v in f.Vertices.ToArray())
        {
            double y = v.Position.Y - 20, x = v.Position.X - 50;
            if (space == StrokeSpace.Local) { y /= 2; x = (x - 2 * y) / 3; }
            Assert.Equal(1, Math.Sqrt(x * x + y * y), 5);
        }
        Assert.InRange(Area(f), space == StrokeSpace.Local ? 17 : 2.8, space == StrokeSpace.Local ? 6 * Math.PI : Math.PI);
    }
    [Fact]
    public void DotsRejectTrueCircularOverlapAndTightTurns()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(10, 10), V(30, 30), 2, StrokePattern.Dots(1.99999), Half));
        // Arc-length spacing is ample; the two endpoint sites approach across a hairpin.
        Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline([V(10, 10), V(30, 10), V(30, 11), V(10, 11)], 2, StrokePattern.Dots(41), Half));
        Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Theory]
    [InlineData(StrokeSpace.Local)] [InlineData(StrokeSpace.Framebuffer)]
    public void CirclePatternsMeasureSampledTransformedContour(StrokeSpace space)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(500, 500);
        d.SetTransform(new float3x2(2, 0, 1, 3, 0, 0));
        d.StrokePatternedCircle(V(30, 30), 20, 2, StrokePattern.FixedDots(8), Half, space: space);
        var f = d.Finish(); Tiled(f);
        Assert.InRange(Area(f), space == StrokeSpace.Local ? 140 : 22, space == StrokeSpace.Local ? 48 * Math.PI : 8 * Math.PI);
        // There is exactly one dot at the circle's input start (right cardinal point).
        Assert.Equal(space == StrokeSpace.Local ? 96 : 64, f.Vertices.Length);
    }
    [Theory]
    [InlineData(LineCap.Butt)] [InlineData(LineCap.Square)] [InlineData(LineCap.Round)]
    public void DashedCircleHasHollowCenterAndNoOverlappingMesh(LineCap cap)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(200, 200);
        d.StrokePatternedCircle(V(50, 50), 20, 2, StrokePattern.FixedDashes(8, .5, .75), Half, cap);
        var f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 120, 170);
        Assert.DoesNotContain(f.Vertices.ToArray(), v => Math.Abs(v.Position.X - 50) < 5 && Math.Abs(v.Position.Y - 50) < 5);
    }
    [Theory]
    [InlineData(StrokeSpace.Local)] [InlineData(StrokeSpace.Framebuffer)]
    public void CircleDotCentersFollowChosenArcLengthNotEllipseParameter(StrokeSpace space)
    {
        using var d = new ShapeDrawList(); d.BeginFrame(500, 500);
        d.SetTransform(new float3x2(2, 0, 1, 3, 0, 0));
        d.StrokePatternedCircle(V(30, 30), 20, 2, StrokePattern.FixedDots(8), Half, space: space);
        var vs = d.Finish().Vertices.ToArray(); int perDot = vs.Length / 8;
        (double x, double y) At(double t) => (90 + 40 * Math.Cos(t) + 20 * Math.Sin(t), 90 + 60 * Math.Sin(t));
        var lengths = new double[20001];
        for (int i = 1; i < lengths.Length; i++)
        {
            var a = At((i - 1) * Math.Tau / 20000); var b = At(i * Math.Tau / 20000);
            lengths[i] = lengths[i - 1] + Math.Sqrt(Math.Pow(b.x - a.x, 2) + Math.Pow(b.y - a.y, 2));
        }
        for (int k = 0; k < 8; k++)
        {
            double angle = k * Math.Tau / 8;
            if (space == StrokeSpace.Framebuffer)
            {
                double target = k * lengths[^1] / 8; int index = Array.BinarySearch(lengths, target);
                if (index < 0) index = ~index;
                angle = index * Math.Tau / 20000;
            }
            var expected = At(angle); var ring = vs.Skip(k * perDot).Take(perDot).ToArray();
            double cx = ring.Average(v => (double)v.Position.X), cy = ring.Average(v => (double)v.Position.Y);
            Assert.InRange(Math.Sqrt(Math.Pow(cx - expected.x, 2) + Math.Pow(cy - expected.y, 2)), 0, .3);
        }
    }
    [Fact]
    public void ZeroGapClosedFramebufferUsesTransformOnce()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(200, 200); d.SetTransform(new float3x2(2, 0, 0, 3, 0, 0));
        d.StrokePatternedPolyline([V(10, 10), V(20, 10), V(20, 20), V(10, 20)], 2, StrokePattern.Dashes(1, 0), Half, true, space: StrokeSpace.Framebuffer);
        var f = d.Finish(); Tiled(f); Assert.Equal(200, Area(f), 5);
        Assert.Equal(19, f.Vertices.ToArray().Min(v => v.Position.X));
        Assert.Equal(29, f.Vertices.ToArray().Min(v => v.Position.Y));
    }
    [Fact]
    public void PrecisionFailureAfterSeveralFragmentsRestoresMergedCommand()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.FillTriangle(V(1, 1), V(2, 1), V(1, 2), Half);
        // Early intervals are legal, later output crosses the coordinate envelope.
        Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(1048500, 10), V(1048600, 10), 2, StrokePattern.Dashes(10, 10), Half));
        var f = d.Finish(); Assert.Equal(3, f.Vertices.Length); Assert.Equal(3, f.Indices.Length);
        Assert.Equal(3, Assert.Single(f.Commands.ToArray()).IndexCount);
    }
    [Fact]
    public void MaximumDotsAndPointLimitsAreBounded()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedSegment(V(0, 10), V(8192, 10), 1, StrokePattern.FixedDots(4096), Half);
        Assert.Equal(4096 * 8, d.Finish().Vertices.Length);
        d.BeginFrame(100, 100);
        var points = Enumerable.Range(0, 1024).Select(i => V(i, 10)).ToArray();
        d.StrokePatternedPolyline(points, 1, StrokePattern.FixedDots(1), Half);
        Assert.Equal(8, d.Finish().Vertices.Length);
        d.BeginFrame(100, 100);
        Assert.Throws<ArgumentException>(() => d.StrokePatternedSegment(V(0, 10), V(100, 10), 1, StrokePattern.Dashes(1e-300, 1e-300), Half));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedCircle(V(0, 0), 1e6f, 1, StrokePattern.FixedDots(1), Half));
        Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Fact]
    public void HiddenDotAndCircleValidationStillRuns()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100); d.PushClip(new(0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokePatternedCircle(V(0, 0), -1, 0, StrokePattern.Dots(1), Half));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokePatternedSegment(V(0, 0), V(10, 0), 0, StrokePattern.Dots(1), Half, (LineCap)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.StrokePatternedCircle(V(0, 0), 1, 0, StrokePattern.Dots(1), Half, space: (StrokeSpace)99));
        d.StrokePatternedCircle(V(0, 0), 1e6f, 1, StrokePattern.Dots(1e-300), Half);
        d.PopClip(); d.SetTransform(new float3x2(0, 0, 0, 0, 0, 0));
        d.StrokePatternedSegment(V(0, 0), V(10, 0), 1, StrokePattern.Dots(1e-300), Half);
        Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Fact]
    public void ClosingDuplicatePreservesPhaseAnchor()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedPolyline([V(10, 10), V(30, 10), V(30, 30), V(10, 30), V(10, 10)], 2, StrokePattern.FixedDots(1), Half, true);
        var f = d.Finish(); Assert.Equal(9, f.Vertices.ToArray().Min(v => v.Position.X));
        Assert.Equal(9, f.Vertices.ToArray().Min(v => v.Position.Y));
    }
    [Fact]
    public void DifferentCallsMayOverlapAndClipsRemainOrdered()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedSegment(V(10, 10), V(30, 10), 2, StrokePattern.FixedDots(1), Half);
        d.PushClip(new(0, 0, 20, 20));
        d.StrokePatternedSegment(V(10, 10), V(30, 10), 2, StrokePattern.FixedDots(1), Half);
        d.PopClip(); var f = d.Finish(); Assert.Equal(2, f.Commands.Length);
        Assert.Equal(new PixelRect(0, 0, 20, 20), f.Commands.Span[1].Clip);
    }
    [Fact]
    public void DotLateBudgetFailureRestoresMergedCommand()
    {
        using var d = new ShapeDrawList(20, 100); d.BeginFrame(100, 100);
        d.FillTriangle(V(1, 1), V(2, 1), V(1, 2), Half);
        Assert.Throws<InvalidOperationException>(() => d.StrokePatternedSegment(V(10, 10), V(40, 10), 2, StrokePattern.Dots(5), Half));
        var f = d.Finish(); Assert.Equal(3, f.Vertices.Length); Assert.Equal(3, f.Indices.Length);
        Assert.Equal(3, Assert.Single(f.Commands.ToArray()).IndexCount);
    }
    [Fact]
    public void TightDashTurnsAndInvalidContoursRejectWithoutPublishing()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline([V(10, 10), V(30, 10), V(30, 11), V(10, 11)], 4, StrokePattern.Dashes(15, 6), Half, cap: LineCap.Round));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline([V(10, 10), V(30, 10), V(20, 20), V(30, 30), V(10, 30)], 2, StrokePattern.Dashes(4, 2), Half, true));
        Assert.Throws<ArgumentException>(() => d.StrokePatternedPolyline([V(0, 0), V(20, 20), V(0, 20), V(20, 0)], 2, StrokePattern.Dashes(4, 2), Half));
        Assert.Empty(d.Finish().Indices.ToArray());
    }
    [Fact]
    public void TinyNegativePhaseWrapsWithoutLosingSite()
    {
        Assert.Equal(StrokePattern.FixedDots(8).Sample(100, 1, true).Sites,
            StrokePattern.FixedDots(8, -double.Epsilon).Sample(100, 1, true).Sites);
        Assert.Throws<ArgumentException>(() => StrokePattern.Dashes(double.Epsilon, 1e300).Sample(100, 1, false));
        Assert.Throws<ArgumentException>(() => StrokePattern.Dashes(1, double.Epsilon).Sample(100, 1, false));
        Assert.Throws<ArgumentException>(() => StrokePattern.Dots(1).Sample(4096, 1, false)); // endpoint would be site 4097
    }
    [Fact]
    public void SingleDashCoveringEntireClosedContourHasNoSeamCaps()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedPolyline([V(10, 10), V(20, 10), V(20, 20), V(10, 20)], 2, StrokePattern.Dashes(100, 20), Half, true, LineCap.Round);
        var f = d.Finish(); Tiled(f); Assert.Equal(80, Area(f), 5);
    }
    [Fact]
    public void ClosedIntervalLimitCountsStitchedSeamOnlyOnce()
    {
        var s = StrokePattern.FixedDashes(4096, .5, .75).Sample(8192, 1, true);
        Assert.Equal(4096, s.Intervals.Count); Assert.Equal(4096, s.Intervals.Sum(i => i.End - i.Start));
        Assert.Throws<ArgumentException>(() => StrokePattern.FixedDashes(4096, .5, .75).Sample(8192, 1, false));
    }
    [Fact]
    public void SampledCircleMayExceedSourcePolylinePointLimit()
    {
        using var d = new ShapeDrawList(); d.BeginFrame(100, 100);
        d.StrokePatternedCircle(V(0, 0), 123000, 2, StrokePattern.FixedDots(1), Half);
        var f = d.Finish(); Tiled(f); Assert.InRange(Area(f), 2.8, Math.PI);
        Assert.Equal(123001, f.Vertices.ToArray().Max(v => v.Position.X));
    }
    [Fact]
    public void InvalidAndExplosiveStylesReject()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokePattern.Dashes(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokePattern.Dashes(1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokePattern.Dots(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokePattern.Dots(1, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokePattern.FixedDots(4097));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokePattern.FixedDashes(2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => default(StrokePattern).Sample(1, 1, false));
        Assert.Throws<ArgumentException>(() => StrokePattern.Dots(1e-300).Sample(100, 1, false));
        Assert.Empty(StrokePattern.Dots(1e-300).Sample(0, 1, false).Sites);
        Assert.Single(StrokePattern.Dashes(1e-300, 0).Sample(100, 1, false).Intervals);
        Assert.Equal(4096, StrokePattern.FixedDots(4096).Sample(100, 1, true).Sites.Count);
    }
}
