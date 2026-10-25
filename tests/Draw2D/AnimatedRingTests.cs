using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;
using Xunit;

namespace Shapes.Tests;

public class AnimatedRingTests
{
    private static readonly LinearColor Half = new(1, 1, 1, .5f);
    private static double Cross(float2 a, float2 b, float2 c) =>
        ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);

    [Theory]
    [InlineData(.21176131405483992)] // Prior dual-ear fix left an inverted final triangle.
    [InlineData(.09412413327681093)] // Prior dual-ear fix found no common triangulation.
    public void DefaultUnchangedWindowNoLongerClipsTinyCircleCornerWedges(double phase)
    {
        using var draw = new ShapeDrawList();
        draw.BeginFrame(1280, 720);
        draw.StrokePatternedCircle(new(640, 612), 144, 14.4f,
            StrokePattern.FixedDashes(16, .5, phase), Half, LineCap.Round, StrokeSpace.Framebuffer);
        var frame = draw.Finish();
        Validate(frame, 144, 14.4);
        AssertNoOverlap(frame);
        // Analytic caps/body cells have finite angular extent independent of phase: no tiny corner ears.
        var ids = frame.Indices.Span; var vertices = frame.Vertices.Span;
        for (int i = 0; i < ids.Length; i += 3)
            Assert.True(Math.Abs(Cross(vertices[(int)ids[i]].Position, vertices[(int)ids[i + 1]].Position,
                vertices[(int)ids[i + 2]].Position)) > 10);
    }

    [Theory]
    [InlineData(640f, 612f)]
    [InlineData(589.7623f, 585.4201f)]
    [InlineData(111.2345f, 654.321f)]
    public void OrdinaryClockPhasesAndCaughtCentersDoNotDependOnGlobalCircleGrid(float x, float y)
    {
        using var draw = new ShapeDrawList();
        var random = new Random(57321);
        int? indexCount = null;
        for (int i = 0; i < 1024; i++)
        {
            double phase = i < 256 ? i / 256d : random.NextDouble();
            draw.BeginFrame(1280, 720);
            draw.StrokePatternedCircle(new(x, y), 144, 14.4f,
                StrokePattern.FixedDashes(16, .5, phase), Half, LineCap.Round, StrokeSpace.Framebuffer);
            var frame = draw.Finish();
            Validate(frame, 144, 14.4);
            indexCount ??= frame.Indices.Length;
            Assert.Equal(indexCount.Value, frame.Indices.Length);
        }
    }

    [Theory]
    [InlineData(StrokeSpace.Local)]
    [InlineData(StrokeSpace.Framebuffer)]
    public void AnalyticCircleKeepsThinCollapseAndCapOverlapRejectionTransactional(StrokeSpace space)
    {
        using var draw = new ShapeDrawList();
        draw.BeginFrame(1280, 720);
        draw.FillTriangle(new(0, 0), new(10, 0), new(0, 10), Half);
        Assert.Throws<ArgumentException>(() => draw.StrokePatternedCircle(new(1000, 1000), 20, .000001f,
            StrokePattern.FixedDashes(16, .5), Half, LineCap.Round, space));
        Assert.Throws<ArgumentException>(() => draw.StrokePatternedCircle(new(640, 612), 144, 14.4f,
            StrokePattern.FixedDashes(1, .00001), Half, LineCap.Round, space));
        var frame = draw.Finish();
        Assert.Equal(3, frame.Indices.Length); Assert.Equal(3, frame.Vertices.Length);
        Assert.Equal(3, Assert.Single(frame.Commands.ToArray()).IndexCount);
    }

    [Theory]
    [InlineData(0, StrokeSpace.Local)] [InlineData(0, StrokeSpace.Framebuffer)]
    [InlineData(1, StrokeSpace.Local)] [InlineData(1, StrokeSpace.Framebuffer)]
    [InlineData(2, StrokeSpace.Local)] [InlineData(2, StrokeSpace.Framebuffer)]
    public void AnalyticCircleRetainsPatternSamplingPhaseSpacingSeamsAndReflections(int kind, StrokeSpace space)
    {
        const float radius = 80, width = 3, scale = 1.25f;
        var pattern = kind switch
        {
            0 => StrokePattern.Dashes(17, 23, -.37),
            1 => StrokePattern.RelativeDashes(5, 7, .93, true),
            _ => StrokePattern.FixedDashes(8, .5, .93)
        };
        // Reconstruct only the existing published sampled rim's measurement for the expected Sample result.
        double r = radius * (double)scale;
        int n = Math.Max(8, (int)Math.Ceiling(Math.PI / (2 * Math.Asin(Math.Sqrt((.25 - .09 - r * .000001) / (2 * r))))));
        n = (n + 3) / 4 * 4;
        using var disk = new DiskMeshAdapter(); disk.Generate(n);
        var rim = disk.Positions;
        var points = new double2[n];
        for (int i = 0; i < n; i++)
        {
            var p = rim[3 * ((n / 2 - i + n) % n)];
            double x = i % (n / 4) == 0 ? (i == 0 ? 1 : i == n / 2 ? -1 : 0) : p.X;
            double y = i % (n / 4) == 0 ? (i == n / 4 ? 1 : i == 3 * n / 4 ? -1 : 0) : p.Y;
            points[i] = new(300 + x * radius, 300 + y * radius);
            if (space == StrokeSpace.Framebuffer) points[i] = new(800 - points[i].X * scale, points[i].Y * scale);
        }
        double total = 0;
        for (int i = 0; i < n; i++) { var d = points[(i + 1) % n] - points[i]; total += Math.Sqrt(d.X * d.X + d.Y * d.Y); }
        var samples = pattern.Sample(total, width, true);
        using var draw = new ShapeDrawList();
        draw.BeginFrame(1280, 720); draw.SetTransform(new(-scale, 0, 0, scale, 800, 0));
        draw.StrokePatternedCircle(new(300, 300), radius, width, pattern, Half, LineCap.Round, space);
        var frame = draw.Finish();
        AssertNoOverlap(frame);
        double actualArea = 0;
        var ids = frame.Indices.Span; var vertices = frame.Vertices.Span;
        for (int i = 0; i < ids.Length; i += 3)
            actualArea += Math.Abs(Cross(vertices[(int)ids[i]].Position, vertices[(int)ids[i + 1]].Position, vertices[(int)ids[i + 2]].Position)) / 2;
        double measurementRadius = space == StrokeSpace.Local ? radius : radius * (double)scale;
        double expectedArea = Math.Tau * measurementRadius * width * samples.Intervals.Sum(s => s.End - s.Start) / total +
            samples.Intervals.Count * Math.PI * width * width / 4;
        if (space == StrokeSpace.Local) expectedArea *= scale * scale;
        Assert.InRange(actualArea, expectedArea * .98, expectedArea * 1.001);
        foreach (var interval in samples.Intervals)
        {
            double angle = (interval.Start + interval.End) / (2 * total) * Math.Tau;
            var point = new float2((float)(800 - (300 + radius * Math.Cos(angle)) * scale), (float)((300 + radius * Math.Sin(angle)) * scale));
            bool covered = false;
            for (int i = 0; i < ids.Length && !covered; i += 3)
            {
                var a = vertices[(int)ids[i]].Position; var b = vertices[(int)ids[i + 1]].Position; var c = vertices[(int)ids[i + 2]].Position;
                double x = Cross(a, b, point), y = Cross(b, c, point), z = Cross(c, a, point);
                covered = (x >= 0 && y >= 0 && z >= 0) || (x <= 0 && y <= 0 && z <= 0);
            }
            Assert.True(covered, $"Missing occupied interval {interval}");
        }
    }

    [Theory]
    [InlineData(0d)] [InlineData(.09412413327681093)] [InlineData(.21176131405483992)] [InlineData(.99999999999999)]
    public void WholeStrokedArcsAndCapsStayInsideExistingQuarterPixelCurveError(double phase)
    {
        const double radius = 144;
        double half = 14.4f / 2d, outer = radius + half, inner = radius - half;
        static int Resolution(double r) => Math.Max(8, (int)Math.Ceiling(Math.PI /
            (2 * Math.Asin(Math.Sqrt((.25 - .09 - r * .000001) / (2 * r))))));
        int full = (Resolution(outer) + 3) / 4 * 4;
        int steps = (int)Math.Ceiling(full / 32d), cap = (Resolution(half) + 1) / 2;
        int bodyVertices = 3 * (steps + 1), capVertices = cap + 2;
        using var draw = new ShapeDrawList(); draw.BeginFrame(1280, 720);
        draw.StrokePatternedCircle(new(640, 612), 144, 14.4f,
            StrokePattern.FixedDashes(16, .5, phase), Half, LineCap.Round, StrokeSpace.Framebuffer);
        var vertices = draw.Finish().Vertices.ToArray();
        Assert.Equal(16 * (bodyVertices + 2 * capVertices), vertices.Length);
        static double Distance(float2 p, float2 c) => Math.Sqrt(Math.Pow((double)p.X - c.X, 2) + Math.Pow((double)p.Y - c.Y, 2));
        for (int dash = 0; dash < 16; dash++)
        {
            int first = dash * (bodyVertices + 2 * capVertices);
            for (int i = 0; i < steps; i++)
            {
                foreach (int row in new[] { 0, 2 })
                {
                    var a = vertices[first + 3 * i + row].Position; var b = vertices[first + 3 * (i + 1) + row].Position;
                    var midpoint = new float2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    Assert.InRange(Math.Abs(Distance(midpoint, new(640, 612)) - (row == 0 ? outer : inner)), 0, .25);
                }
            }
            for (int side = 0; side < 2; side++)
            {
                int start = first + bodyVertices + side * capVertices;
                var center = vertices[start].Position;
                for (int i = 1; i + 1 < capVertices; i++)
                {
                    var a = vertices[start + i].Position; var b = vertices[start + i + 1].Position;
                    var midpoint = new float2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                    Assert.InRange(Math.Abs(Distance(midpoint, center) - half), 0, .25);
                }
            }
        }
        Assert.All(vertices, v => Assert.InRange(Distance(v.Position, new(640, 612)), inner - .25, outer + .25));
    }

    [Theory]
    [InlineData(.09412413327681093)] [InlineData(.21176131405483992)] [InlineData(.99999999999999)]
    public void LocalAnalyticCircleKeepsAffineShearAndReflectionWithoutFramebufferAngularAssumptions(double phase)
    {
        using var draw = new ShapeDrawList(); draw.BeginFrame(1800, 1800);
        draw.SetTransform(new(-2, 0, 1, 3, 1000, 0));
        draw.StrokePatternedCircle(new(300, 300), 80, 3, StrokePattern.FixedDashes(8, .5, phase),
            Half, LineCap.Round, StrokeSpace.Local);
        var frame = draw.Finish();
        AssertNoOverlap(frame);
        double area = 0, sign = 0;
        var vertices = frame.Vertices.Span; var ids = frame.Indices.Span;
        for (int i = 0; i < ids.Length; i += 3)
        {
            double a = Cross(vertices[(int)ids[i]].Position, vertices[(int)ids[i + 1]].Position, vertices[(int)ids[i + 2]].Position);
            Assert.NotEqual(0, a); if (sign == 0) sign = Math.Sign(a); Assert.Equal(sign, Math.Sign(a)); area += Math.Abs(a) / 2;
        }
        double ideal = (Math.Tau * 80 * .5 * 3 + 8 * Math.PI * 9 / 4) * 6;
        Assert.InRange(area, ideal * .98, ideal * 1.001);
        foreach (var vertex in vertices)
        {
            double y = vertex.Position.Y / 3d, x = (1000 + y - vertex.Position.X) / 2;
            Assert.InRange(Math.Sqrt((x - 300) * (x - 300) + (y - 300) * (y - 300)), 78.5 - .25, 81.5 + .25);
        }
    }

    [Fact]
    public void DefaultHeldRingPreviouslyInvertedAnEarAtThisPhase()
    {
        using var draw = new ShapeDrawList();
        draw.BeginFrame(1280, 720);
        // Red on the original implementation: source ear area -0.000672 becomes +0.000293 in FLOAT32.
        draw.StrokePatternedCircle(new(640, 612), 144, 14.4f,
            StrokePattern.FixedDashes(16, .5, 0.11764605882352941), Half, LineCap.Round, StrokeSpace.Framebuffer);
        var frame = draw.Finish();
        Validate(frame, 144, 14.4);
        AssertNoOverlap(frame);
    }

    [Theory]
    [InlineData(1280, 720, 0f)]
    [InlineData(960, 540, .123f)]
    [InlineData(720, 1280, .456f)]
    [InlineData(2000, 1800, .789f)]
    public void AnimationBoundaryAndSeamPhasesSurviveResizeLetterboxAndSubpixelCenters(int w, int h, float fraction)
    {
        using var draw = new ShapeDrawList();
        float scale = MathF.Min(w / 1280f, h / 720f);
        float radius = (float)(Math.Min(w, h) * .2 / scale);
        float width = (float)(Math.Min(w, h) * .02);
        // Cover ordinary phases, and cuts immediately around every sampled-circle boundary.
        // The source tolerance formula defines the deterministic tessellation fixture, not a tunable default.
        double r = radius * (double)scale;
        int segments = Math.Max(8, (int)Math.Ceiling(Math.PI / (2 * Math.Asin(Math.Sqrt((.25 - .09 - r * .000001) / (2 * r))))));
        segments = (segments + 3) / 4 * 4;
        var boundaryPhases = Enumerable.Range(0, segments / 4).SelectMany(i =>
            new[] { 0d, -.5 }.SelectMany(endpoint =>
                new[] { -1e-6, -1e-8, 0d, 1e-8, 1e-6 }.Select(e => i * 16d / segments + endpoint + e)));
        var phases = Enumerable.Range(0, 65).Select(i => i / 64d).Concat(boundaryPhases);
        foreach (double phase in phases)
        {
            draw.BeginFrame(w, h);
            draw.SetTransform(new(scale, 0, 0, scale, (w - 1280 * scale) / 2, (h - 720 * scale) / 2));
            var error = Record.Exception(() => draw.StrokePatternedCircle(new(640 + fraction, 612 - fraction), radius, width,
                StrokePattern.FixedDashes(16, .5, phase), Half, LineCap.Round, StrokeSpace.Framebuffer));
            Assert.True(error is null, $"phase={phase:R}, viewport={w}x{h}, fraction={fraction:R}: {error}");
            Validate(draw.Finish(), r, width);
        }
    }

    [Theory]
    [InlineData(StrokeSpace.Local, false)]
    [InlineData(StrokeSpace.Local, true)]
    [InlineData(StrokeSpace.Framebuffer, false)]
    [InlineData(StrokeSpace.Framebuffer, true)]
    public void QuantizedEarPredicatesRespectMeasurementSpaceAndReflectedTransforms(StrokeSpace space, bool reflected)
    {
        using var draw = new ShapeDrawList();
        foreach (double phase in new[] { 0d, .11764605882352941, .11764705882352941, .11764805882352941, .99999999 })
        {
            draw.BeginFrame(1280, 720, new(reflected ? -1 : 1, 0, 0, 1, reflected ? 1280 : 0, 0));
            draw.StrokePatternedCircle(new(640.125f, 612.375f), 144, 14.4f,
                StrokePattern.FixedDashes(16, .5, phase), Half, LineCap.Round, space);
            var frame = draw.Finish();
            Validate(frame, 144, 14.4);
            AssertNoOverlap(frame);
        }
    }

    [Fact]
    public void AnalyticCircleBudgetFailureRestoresPreviouslyMergedCommandAndBuffers()
    {
        using var draw = new ShapeDrawList(20, 1000); draw.BeginFrame(1280, 720);
        draw.FillTriangle(new(0, 0), new(10, 0), new(0, 10), Half);
        Assert.Throws<InvalidOperationException>(() => draw.StrokePatternedCircle(new(640, 612), 144, 14.4f,
            StrokePattern.FixedDashes(16, .5, .21176131405483992), Half, LineCap.Round, StrokeSpace.Framebuffer));
        var frame = draw.Finish();
        Assert.Equal(3, frame.Vertices.Length); Assert.Equal(new uint[] { 0, 1, 2 }, frame.Indices.ToArray());
        Assert.Equal(3, Assert.Single(frame.Commands.ToArray()).IndexCount);
        Assert.Equal(new float2(10, 0), frame.Vertices.Span[1].Position);
    }

    [Fact]
    public void GenuineCollapsedPatternStillRejectsAndRestoresPriorOutput()
    {
        using var draw = new ShapeDrawList();
        draw.BeginFrame(1280, 720);
        draw.FillTriangle(new(0, 0), new(10, 0), new(0, 10), Half);
        draw.SetTransform(new(1, 0, 0, 1, 1000, 1000));
        Assert.Throws<ArgumentException>(() => draw.StrokePatternedSegment(new(0, 0), new(20, 0), .000001f,
            StrokePattern.Dashes(2, 2), Half, LineCap.Round));
        var frame = draw.Finish();
        Assert.Equal(3, frame.Indices.Length);
        Assert.Equal(3, frame.Vertices.Length);
        Assert.Equal(3, Assert.Single(frame.Commands.ToArray()).IndexCount);
    }

    private static void Validate(ShapeFrame frame, double radius, double width)
    {
        var vertices = frame.Vertices.Span;
        var ids = frame.Indices.Span;
        Assert.NotEmpty(frame.Indices.ToArray());
        Assert.Equal(0, ids.Length % 3);
        double sign = 0, area = 0;
        foreach (var vertex in vertices)
        {
            Assert.True(float.IsFinite(vertex.Position.X) && float.IsFinite(vertex.Position.Y));
            Assert.Equal(new float4(.5f), vertex.Color);
        }
        for (int i = 0; i < ids.Length; i += 3)
        {
            for (int j = 0; j < 3; j++) Assert.InRange(ids[i + j], 0u, (uint)vertices.Length - 1);
            double cross = Cross(vertices[(int)ids[i]].Position, vertices[(int)ids[i + 1]].Position, vertices[(int)ids[i + 2]].Position);
            Assert.NotEqual(0, cross);
            if (sign == 0) sign = Math.Sign(cross);
            Assert.Equal(sign, Math.Sign(cross));
            area += Math.Abs(cross) / 2;
        }
        // Half-duty centerline stroke plus 16 pairs of round caps. Sampling reserves the existing curve error.
        double ideal = Math.Tau * radius * .5 * width + 16 * Math.PI * width * width / 4;
        Assert.InRange(area, ideal * .98, ideal * 1.02);
    }

    private static void AssertNoOverlap(ShapeFrame frame)
    {
        var vertices = frame.Vertices.ToArray(); var ids = frame.Indices.ToArray();
        for (int i = 0; i < ids.Length; i += 3) for (int j = i + 3; j < ids.Length; j += 3)
        {
            bool separated = false;
            for (int side = 0; side < 2 && !separated; side++)
            {
                int first = side == 0 ? i : j;
                for (int edge = 0; edge < 3; edge++)
                {
                    var a = vertices[ids[first + edge]].Position;
                    var b = vertices[ids[first + (edge + 1) % 3]].Position;
                    double amin = double.PositiveInfinity, amax = double.NegativeInfinity, bmin = amin, bmax = amax;
                    for (int k = 0; k < 3; k++)
                    {
                        double x = Cross(a, b, vertices[ids[i + k]].Position), y = Cross(a, b, vertices[ids[j + k]].Position);
                        amin = Math.Min(amin, x); amax = Math.Max(amax, x); bmin = Math.Min(bmin, y); bmax = Math.Max(bmax, y);
                    }
                    if (amax <= bmin || bmax <= amin) { separated = true; break; }
                }
            }
            Assert.True(separated, $"Positive-area overlap between triangles {i / 3}/{j / 3}.");
        }
    }
}
