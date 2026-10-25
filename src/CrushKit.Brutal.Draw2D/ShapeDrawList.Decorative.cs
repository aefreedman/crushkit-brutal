namespace CrushKit.Brutal.Draw2D;

public sealed partial class ShapeDrawList
{
    // A bounded parameter-space mask, not a path union. Each edge has two affine triangles;
    // shared miter cross-sections carry the same arclength at every transverse position.
    // Clip the stripe in (arclength, signed transverse distance), then map into the ribbon.
    private void EmitDecorativePattern(List<P> points, double[] distances, double width,
        StrokePattern pattern, List<StrokePattern.Interval> intervals, bool closed,
        LinearColor color, StrokeSpace space)
    {
        int edges = distances.Length - 1, work = 0;
        double radius = width / 2, total = distances[^1];
        var normals = new P[edges];
        for (int i = 0; i < edges; i++)
        {
            var d = Unit(points[(i + 1) % points.Count] - points[i]);
            normals[i] = new(-d.Y, d.X);
        }
        var miters = new P[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            if (!closed && i == 0) miters[i] = normals[0];
            else if (!closed && i == points.Count - 1) miters[i] = normals[^1];
            else
            {
                var a = normals[(i + edges - 1) % edges]; var b = normals[i % edges];
                double denominator = 1 + Dot(a, b);
                if (!(denominator > 0)) throw new ArgumentException("Decorative ribbon reversal.");
                miters[i] = (a + b) * (1 / denominator);
            }
        }
        var tiles = new List<PatternTile>();
        var occupied = new bool[intervals.Count];
        var emitted = new bool[intervals.Count];
        var mask = new List<P>(8); var buffer = new List<P>(8);
        for (int edge = 0; edge < edges; edge++)
        {
            double s0 = distances[edge], s1 = distances[edge + 1];
            var a = points[edge]; var b = points[(edge + 1) % points.Count];
            var m0 = miters[edge]; var m1 = miters[(edge + 1) % points.Count];
            P[] uv = [new(s0, -radius), new(s1, -radius), new(s1, radius), new(s0, radius)];
            P[] xy = [a - m0 * radius, b - m1 * radius, b + m1 * radius, a + m0 * radius];
            for (int triangle = 0; triangle < 2; triangle++)
            {
                int j = triangle + 1, k = triangle + 2;
                var u = uv[0]; var v = uv[j]; var w = uv[k];
                var x = xy[0]; var y = xy[j]; var z = xy[k];
                if (!(Cross(y - x, z - x) > 0))
                    throw new ArgumentException("Width/tight turn inverts a decorative ribbon tile.");
                for (int glyph = 0; glyph < intervals.Count; glyph++)
                for (int cycle = closed ? -1 : 0; cycle <= (closed ? 1 : 0); cycle++)
                for (int half = 0; half < (pattern.Shape == DashShape.Chevron ? 2 : 1); half++)
                {
                    if (++work > 4_000_000) throw new ArgumentException("Decorative mask complexity limit.");
                    var interval = intervals[glyph];
                    double start = interval.Start + cycle * total, end = interval.End + cycle * total;
                    double padding = radius * Math.Abs(pattern.Modifier);
                    if (end + padding <= s0 || start - padding >= s1) continue;
                    mask.Clear(); mask.Add(u); mask.Add(v); mask.Add(w);
                    double slope = pattern.Modifier;
                    if (pattern.Shape == DashShape.Chevron)
                    {
                        // Two disjoint half-ribbons retain the concave V notch; never fan across it.
                        ClipDecorativeMask(ref mask, ref buffer, new(0, 1), 0, half == 1);
                        if (half == 0) slope = -slope;
                    }
                    ClipDecorativeMask(ref mask, ref buffer, new(1, -slope), start, true);
                    ClipDecorativeMask(ref mask, ref buffer, new(1, -slope), end, false);
                    if (mask.Count < 3) continue;
                    polygon.Clear(); triangles.Clear();
                    double determinant = Cross(v - u, w - u);
                    foreach (var p in mask)
                    {
                        double bv = Cross(p - u, w - u) / determinant;
                        double bw = Cross(v - u, p - u) / determinant;
                        polygon.Add(p.X == s0 ? a + m0 * p.Y : p.X == s1 ? b + m1 * p.Y :
                            x + (y - x) * bv + (z - x) * bw);
                    }
                    CleanOutline(polygon);
                    if (polygon.Count < 3) continue;
                    for (int i = 1; i + 1 < polygon.Count; i++)
                    {
                        Tri(0, i, i + 1);
                        if (Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]) != 0) occupied[glyph] = true;
                    }
                    int firstIndex = ic;
                    Emit(color, validateOutline: true, framebuffer: space == StrokeSpace.Framebuffer);
                    // As in Emit, zero-area quantized triangles disappear. Reject loss of an entire
                    // occupied glyph, not a vanishing clipping sliver at an edge/half-width boundary.
                    emitted[glyph] |= ic != firstIndex;
                    // Validate *every* actual-float triangle, including pairs within the same glyph.
                    for (int index = firstIndex; index < ic; index += 3)
                    {
                        var p = PatternVertex(index); var q = PatternVertex(index + 1); var r = PatternVertex(index + 2);
                        tiles.Add(new(index, index + 3, Math.Min(p.X, Math.Min(q.X, r.X)), Math.Min(p.Y, Math.Min(q.Y, r.Y)),
                            Math.Max(p.X, Math.Max(q.X, r.X)), Math.Max(p.Y, Math.Max(q.Y, r.Y))));
                    }
                }
            }
        }
        for (int i = 0; i < intervals.Count; i++)
            if (occupied[i] && !emitted[i]) throw new ArgumentException("Framebuffer precision collapses a decorative glyph.");
        ValidatePatternTiles(tiles, work);
    }

    private static void ClipDecorativeMask(ref List<P> input, ref List<P> output, P normal, double limit, bool greater)
    {
        output.Clear();
        if (input.Count == 0) return;
        var previous = input[^1]; double ps = Dot(previous, normal) - limit;
        if (!greater) ps = -ps;
        foreach (var current in input)
        {
            double cs = Dot(current, normal) - limit; if (!greater) cs = -cs;
            if ((cs >= 0) != (ps >= 0))
            {
                // Canonical endpoint order gives shared edges identical intersections. Enforce the
                // clipping equation exactly, avoiding numerical slivers along half-width boundaries.
                var a = previous; var b = current;
                if (a.X > b.X || (a.X == b.X && a.Y > b.Y)) (a, b) = (b, a);
                double da = Dot(a, normal) - limit, db = Dot(b, normal) - limit;
                var intersection = da == 0 ? a : db == 0 ? b : a + (b - a) * (da / (da - db));
                intersection = normal.X == 0 ? new(intersection.X, limit / normal.Y)
                    : new((limit - normal.Y * intersection.Y) / normal.X, intersection.Y);
                output.Add(intersection);
            }
            if (cs >= 0) output.Add(current);
            previous = current; ps = cs;
        }
        (input, output) = (output, input);
    }
}
