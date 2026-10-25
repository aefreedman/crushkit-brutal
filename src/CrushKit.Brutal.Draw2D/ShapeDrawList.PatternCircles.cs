namespace CrushKit.Brutal.Draw2D;

public sealed partial class ShapeDrawList
{
    private readonly record struct CircleMeasure(P Center, P X, P Y, double Radius);

    private CircleMeasure? CircularMeasure(P center, double radius, StrokeSpace space)
    {
        if (space == StrokeSpace.Local) return new(center, new(1, 0), new(0, 1), radius);
        // Framebuffer arc length is circular only for an exactly conformal composed linear map.
        // A framebuffer ellipse keeps the general sampled-path route; do not pretend its spacing is angular.
        var x = new P((double)local.M11 * world.M11 + (double)local.M12 * world.M21,
            (double)local.M11 * world.M12 + (double)local.M12 * world.M22);
        var y = new P((double)local.M21 * world.M11 + (double)local.M22 * world.M21,
            (double)local.M21 * world.M12 + (double)local.M22 * world.M22);
        double length2 = Dot(x, x);
        if (length2 == 0 || length2 != Dot(y, y) || Dot(x, y) != 0) return null;
        double scale = Math.Sqrt(length2);
        return new(Pixel(center), x * (1 / scale), y * (1 / scale), radius * scale);
    }

    /// <summary>Per-dash analytic circular bodies avoid vanishing cut/corner wedges of a clipped polyline.
    /// Pattern.Sample still owns interval fitting/spacing/seams; only the curve construction changes.</summary>
    private void EmitCircularDashes(CircleMeasure circle, double width, List<StrokePattern.Interval> intervals,
        double measuredLength, LinearColor color, StrokeSpace space, List<PatternTile> fragments)
    {
        bool framebuffer = space == StrokeSpace.Framebuffer;
        double half = width / 2, inner = circle.Radius - half, outer = circle.Radius + half;
        if (inner <= 0) throw new ArgumentException("Circular patterned stroke has no representable inner boundary.");
        int fullResolution = RoundResolution(outer, space);
        int capSegments = (Resolution(half, framebuffer) + 1) / 2;
        P Direction(double angle) => circle.X * Math.Cos(angle) + circle.Y * Math.Sin(angle);
        P Tangent(double angle) => circle.X * -Math.Sin(angle) + circle.Y * Math.Cos(angle);
        foreach (var interval in intervals)
        {
            double start = interval.Start / measuredLength * Math.Tau;
            double sweep = (interval.End - interval.Start) / measuredLength * Math.Tau;
            int segments = Math.Max(1, (int)Math.Ceiling(fullResolution * (sweep / Math.Tau)));
            double end = start + sweep;
            var first = Direction(start); var last = Direction(end);
            var firstOuter = circle.Center + first * outer; var firstInner = circle.Center + first * inner;
            var lastOuter = circle.Center + last * outer; var lastInner = circle.Center + last * inner;
            polygon.Clear(); triangles.Clear();
            for (int i = 0; i <= segments; i++)
            {
                var direction = i == 0 ? first : i == segments ? last : Direction(start + sweep * i / segments);
                polygon.Add(i == 0 ? firstOuter : i == segments ? lastOuter : circle.Center + direction * outer);
                // The midpoint is also the cap fan's center. Sharing BOTH half-edges is essential:
                // separately rounded outer/middle/inner vertices need not stay exactly collinear.
                polygon.Add(circle.Center + direction * circle.Radius);
                polygon.Add(i == 0 ? firstInner : i == segments ? lastInner : circle.Center + direction * inner);
                if (i == 0) continue;
                int a = 3 * (i - 1), b = 3 * i;
                for (int row = 0; row < 2; row++)
                { Tri(a + row, b + row, a + row + 1); Tri(a + row + 1, b + row, b + row + 1); }
            }
            int firstVertex = vc, firstIndex = ic;
            Emit(color, framebuffer: framebuffer);
            AddPatternTile(fragments, firstVertex, firstIndex);
            // Separate body/cap tiles share exactly the same endpoint coordinates. Existing tile SAT
            // validation checks caps against the body and each other as well as neighboring dashes.
            Cap(circle.Center + last * circle.Radius, last, Tangent(end), lastOuter, lastInner);
            Cap(circle.Center + first * circle.Radius, first * -1, Tangent(start) * -1, firstInner, firstOuter);
        }

        void Cap(P center, P radial, P outward, P from, P to)
        {
            polygon.Clear(); triangles.Clear(); polygon.Add(center); polygon.Add(from);
            for (int i = 1; i < capSegments; i++)
            {
                double a = Math.PI * i / capSegments;
                polygon.Add(center + (radial * Math.Cos(a) + outward * Math.Sin(a)) * half);
            }
            polygon.Add(to);
            for (int i = 1; i + 1 < polygon.Count; i++) Tri(0, i, i + 1);
            int firstVertex = vc, firstIndex = ic;
            Emit(color, framebuffer: framebuffer);
            AddPatternTile(fragments, firstVertex, firstIndex);
        }
    }
}
