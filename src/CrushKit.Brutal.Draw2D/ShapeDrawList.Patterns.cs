using Brutal.Numerics;

namespace CrushKit.Brutal.Draw2D;

public sealed partial class ShapeDrawList
{
    /// <summary>Dash lengths are occupied centerline intervals. Square/round caps extend half a width
    /// beyond each interval; overlapping fragments reject, rather than blending twice.</summary>
    public void StrokePatternedSegment(float2 a, float2 b, float width, StrokePattern pattern,
        LinearColor color, LineCap cap = LineCap.Butt, StrokeSpace space = StrokeSpace.Local) =>
        StrokePatternedPolyline([a, b], width, pattern, color, false, cap, space);

    /// <summary>Continuous arc-length phase, with bevel outer joins and miter inner joins on open
    /// fragments. Closed contours must be convex (either winding); seam fragments are stitched.
    /// Crossing/touching open centerlines and overlapping fragment geometry are unsupported.
    /// Maximum 1024 source points and 4096 intervals/sites. Pairwise overlap validation has a
    /// 4,000,000-candidate work budget (including broad-phase tests); excessive complexity rejects.
    /// Nonzero decorative shapes use a piecewise-affine miter ribbon, not Basic's bevel joins.
    /// Their shifted glyphs may extend beyond occupied intervals, but clip to open path endpoints
    /// (no extension). Closed glyphs wrap across the seam. Tight/inverted/overlapping ribbons reject.
    /// Exactly zero modifier and zero-gap patterns use Basic geometry, including its joins.
    /// Dots ignore cap appearance, but still validate its enum. General self-crossing unions and
    /// open end-to-end snapping are unsupported. Zero-length paths are empty, even with round caps.</summary>
    public void StrokePatternedPolyline(ReadOnlySpan<float2> points, float width, StrokePattern pattern,
        LinearColor color, bool closed = false, LineCap cap = LineCap.Butt, StrokeSpace space = StrokeSpace.Local)
    {
        StartPattern(width, pattern, color, cap, space);
        if (points.Length > 1024) throw new ArgumentOutOfRangeException(nameof(points));
        foreach (var point in points) Input(point);
        if (NoOutput(color) || width == 0) return;
        var centerline = new List<P>(points.Length);
        foreach (var point in points) centerline.Add(Input(point));
        PatternPath(centerline, width, pattern, color, closed, cap, space);
    }

    /// <summary>Measures the sampled circle/ellipse with the existing screen-space curve tolerance.
    /// Basic round-capped dashes in circular measurement spaces use independently sampled true arcs
    /// and caps, avoiding vanishing wedges where a dash cut meets a sampled corner. The outer stroke
    /// and caps obey the same curve tolerance. Framebuffer ellipses and other styles keep the general
    /// sampled-path route and its precision-rejection limits; their arc spacing is not treated as circular.
    /// Dots are circular in measurement space, hence may become elliptical after a Local transform.</summary>
    public void StrokePatternedCircle(float2 center, float radius, float width, StrokePattern pattern,
        LinearColor color, LineCap cap = LineCap.Butt, StrokeSpace space = StrokeSpace.Local)
    {
        StartPattern(width, pattern, color, cap, space);
        var c = Input(center); Nonnegative(radius);
        if (NoOutput(color) || radius == 0 || width == 0) return;
        var centerline = new List<P>();
        CircleBoundary(centerline, c, radius, RoundResolution(radius, StrokeSpace.Local));
        PatternPath(centerline, width, pattern, color, true, cap, space,
            cap == LineCap.Round && !pattern.IsDots && !pattern.IsDecorative ? CircularMeasure(c, radius, space) : null);
    }

    private void StartPattern(float width, StrokePattern pattern, LinearColor color, LineCap cap, StrokeSpace space)
    {
        Start(color); Nonnegative(width); pattern.Validate();
        if (!Enum.IsDefined(cap)) throw new ArgumentOutOfRangeException(nameof(cap));
        if (!Enum.IsDefined(space)) throw new ArgumentOutOfRangeException(nameof(space));
        if (pattern.Shape != DashShape.Basic && cap != LineCap.Butt)
            throw new ArgumentException("Decorative dashes own their ends and require Butt caps.", nameof(cap));
    }

    private void PatternCap(P center, P direction, double radius, bool framebuffer)
    {
        int q = (Resolution(radius, framebuffer) + 1) / 2;
        double angle = Math.Atan2(direction.Y, direction.X) + Math.PI / 2;
        // Endpoints already belong to the side chains; only insert interior arc vertices.
        for (int j = 1; j < q; j++)
        {
            double a = angle - j * Math.PI / q;
            polygon.Add(center + new P(Math.Cos(a), Math.Sin(a)) * radius);
        }
    }

    private static void CleanPatternPath(List<P> points, bool closed)
    {
        if (closed)
        {
            while (points.Count > 1 && points[^1] == points[0]) points.RemoveAt(points.Count - 1);
            // Preserve the supplied phase anchor, even when it lies on a straight edge.
            for (int i = 1; i < points.Count;)
            {
                var a = points[i - 1]; var b = points[i]; var c = points[(i + 1) % points.Count];
                if (b == a || (Cross(b - a, c - b) == 0 && Dot(b - a, c - b) >= 0)) points.RemoveAt(i);
                else i++;
            }
            return;
        }
        for (int i = 1; i < points.Count;)
        {
            if (points[i] == points[i - 1]) { points.RemoveAt(i); continue; }
            if (i > 1 && Cross(points[i - 1] - points[i - 2], points[i] - points[i - 1]) == 0)
            {
                if (Dot(points[i - 1] - points[i - 2], points[i] - points[i - 1]) < 0)
                    throw new ArgumentException("Polyline reversal.");
                points.RemoveAt(i - 1); i--; continue;
            }
            i++;
        }
    }

    private static void ValidatePatternPath(List<P> points, bool closed)
    {
        if (closed)
        {
            ValidateSimpleOutline(points, "Closed patterned contour must be simple and convex.");
            double sign = 0;
            for (int i = 0; i < points.Count && sign == 0; i++)
                sign = Math.Sign(Cross(points[(i + 1) % points.Count] - points[i], points[(i + 2) % points.Count] - points[(i + 1) % points.Count]));
            if (sign == 0) throw new ArgumentException("Degenerate closed patterned contour.");
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[(i + 1) % points.Count] - points[i];
                var b = points[(i + 2) % points.Count] - points[(i + 1) % points.Count];
                if (sign * Cross(a, b) < 0 || (Cross(a, b) == 0 && Dot(a, b) < 0))
                    throw new ArgumentException("Closed patterned contour must be convex.");
            }
        }
        else
        {
            for (int i = 0; i + 1 < points.Count; i++) for (int j = i + 2; j + 1 < points.Count; j++)
                if (Intersects(points[i], points[i + 1], points[j], points[j + 1]))
                    throw new ArgumentException("Open patterned centerline must be simple.");
        }
    }

    private void PatternPath(List<P> centerline, float width, StrokePattern pattern, LinearColor color,
        bool closed, LineCap cap, StrokeSpace space, CircleMeasure? circle = null)
    {
        CleanPatternPath(centerline, closed);
        if (centerline.Count < (closed ? 3 : 2)) return;
        ValidatePatternPath(centerline, closed);
        if (space == StrokeSpace.Framebuffer)
            for (int i = 0; i < centerline.Count; i++) centerline[i] = Pixel(centerline[i]);
        // Keep input start and winding: normalizing winding here would change phase anchoring.
        int edges = centerline.Count - (closed ? 0 : 1);
        var distances = new double[edges + 1];
        for (int i = 0; i < edges; i++)
        {
            double length = Length(centerline[(i + 1) % centerline.Count] - centerline[i]);
            distances[i + 1] = distances[i] + length;
            if (!(distances[i + 1] > distances[i])) throw new ArgumentException("Centerline precision collapses an edge.");
        }
        double total = distances[^1];
        var samples = pattern.Sample(total, width, closed,
            pattern.IsDecorative && !closed ? width / 2.0 * Math.Abs(pattern.Modifier) : 0);
        int oldVc = vc, oldIc = ic, oldCc = cc;
        var last = cc == 0 ? default : commands[cc - 1];
        var fragments = new List<PatternTile>();
        try
        {
            if (pattern.IsDots)
            {
                var centers = samples.Sites.Select(site => PatternPoint(centerline, distances, site)).ToList();
                ValidateDotSpacing(centers, width);
                int n = RoundResolution(width / 2.0, space);
                foreach (var center in centers)
                {
                    polygon.Clear(); triangles.Clear();
                    CircleBoundary(polygon, center, width / 2.0, n);
                    ValidatePixelLoop(polygon, space);
                    for (int i = 1; i + 1 < polygon.Count; i++) Tri(0, i, i + 1);
                    int firstVertex = vc, firstIndex = ic;
                    Emit(color, validateOutline: true, framebuffer: space == StrokeSpace.Framebuffer);
                    AddPatternTile(fragments, firstVertex, firstIndex);
                }
            }
            else if (samples.Solid && closed)
            {
                outlineBoundary.Clear(); outlineOuter.Clear(); outlineInner.Clear();
                outlineBoundary.AddRange(centerline);
                PrepareBoundary(StrokeSpace.Local);
                OffsetOuter(width / 2.0); ErodeInner(width / 2.0);
                EmitOutline(color, space);
            }
            else if (pattern.IsDecorative && !samples.Solid)
            {
                EmitDecorativePattern(centerline, distances, width, pattern, samples.Intervals, closed, color, space);
            }
            // The annular primitive requires a positive inner radius. Other domains retain the
            // original general stroke route and its existing support/rejection policy.
            else if (circle is { } circular && circular.Radius > width / 2.0)
            {
                EmitCircularDashes(circular, width, samples.Intervals, total, color, space, fragments);
            }
            else foreach (var interval in samples.Intervals)
            {
                BuildPatternFragment(centerline, distances, interval.Start, interval.End);
                polygon.Clear(); triangles.Clear();
                int firstVertex = vc, firstIndex = ic;
                StrokeOpenPath(width, color, cap, space == StrokeSpace.Framebuffer, quantizedEars: true);
                AddPatternTile(fragments, firstVertex, firstIndex);
            }
            ValidatePatternTiles(fragments);
        }
        catch
        {
            vc = oldVc; ic = oldIc; cc = oldCc;
            if (cc > 0) commands[cc - 1] = last;
            throw;
        }
    }

    private static P PatternPoint(List<P> points, double[] distances, double distance)
    {
        double total = distances[^1];
        if (distance == total && distances.Length == points.Count) return points[^1];
        if (distance >= total) distance -= total;
        int edge = Array.BinarySearch(distances, distance);
        if (edge < 0) edge = ~edge - 1;
        edge = Math.Min(edge, distances.Length - 2);
        double t = (distance - distances[edge]) / (distances[edge + 1] - distances[edge]);
        return points[edge] + (points[(edge + 1) % points.Count] - points[edge]) * t;
    }

    private void BuildPatternFragment(List<P> points, double[] distances, double start, double end)
    {
        path.Clear(); path.Add(PatternPoint(points, distances, start));
        double total = distances[^1];
        // An interval spans at most one seam and at most one entire contour.
        for (int cycle = 0; cycle <= (end > total ? 1 : 0); cycle++)
            for (int i = 1; i < distances.Length; i++)
            {
                double d = distances[i] + cycle * total;
                if (d > start && d < end) path.Add(points[i % points.Count]);
            }
        // An open far endpoint must not wrap to the first point.
        path.Add(end == total && distances.Length == points.Count ? points[^1] : PatternPoint(points, distances, end));
        CleanPatternPath(path, false);
    }

    private static void ValidateDotSpacing(List<P> centers, double diameter)
    {
        if (centers.Count < 2) return;
        bool xAxis = centers.Max(p => p.X) - centers.Min(p => p.X) >= centers.Max(p => p.Y) - centers.Min(p => p.Y);
        // Sort a copy: emission order remains periodic path order.
        var sorted = centers.OrderBy(p => xAxis ? p.X : p.Y).ToArray();
        int work = 0;
        for (int i = 0; i < sorted.Length; i++) for (int j = i + 1; j < sorted.Length; j++)
        {
            if (++work > 4_000_000) throw new ArgumentException("Dot spacing-validation complexity limit.");
            var delta = sorted[j] - sorted[i];
            if ((xAxis ? delta.X : delta.Y) >= diameter) break;
            if (Dot(delta, delta) < diameter * diameter) throw new ArgumentException("Circular dots overlap.");
        }
    }

    private sealed record PatternTile(int FirstIndex, int EndIndex, double MinX, double MinY, double MaxX, double MaxY);
    private void AddPatternTile(List<PatternTile> tiles, int firstVertex, int firstIndex)
    {
        if (ic == firstIndex) throw new ArgumentException("Framebuffer precision collapses a pattern fragment.");
        double minX = double.PositiveInfinity, minY = minX, maxX = double.NegativeInfinity, maxY = maxX;
        for (int i = firstVertex; i < vc; i++)
        {
            var p = vertices[i].Position;
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        tiles.Add(new(firstIndex, ic, minX, minY, maxX, maxY));
    }

    // Sweep the more discriminating axis. Bound *all* candidate work (including AABB tests),
    // not only positive overlaps. This is a bounded rejection policy, not an arbitrary path union.
    private void ValidatePatternTiles(List<PatternTile> tiles, int work = 0)
    {
        if (tiles.Count < 2) return;
        bool xAxis = tiles.Max(t => t.MaxX) - tiles.Min(t => t.MinX) >= tiles.Max(t => t.MaxY) - tiles.Min(t => t.MinY);
        tiles.Sort((a, b) => (xAxis ? a.MinX : a.MinY).CompareTo(xAxis ? b.MinX : b.MinY));
        void Step() { if (++work > 4_000_000) throw new ArgumentException("Pattern overlap-validation complexity limit."); }
        for (int i = 0; i < tiles.Count; i++) for (int j = i + 1; j < tiles.Count; j++)
        {
            var a = tiles[i]; var b = tiles[j]; Step();
            if ((xAxis ? b.MinX >= a.MaxX : b.MinY >= a.MaxY)) break;
            if (b.MinX >= a.MaxX || a.MinX >= b.MaxX || b.MinY >= a.MaxY || a.MinY >= b.MaxY) continue;
            for (int u = a.FirstIndex; u < a.EndIndex; u += 3) for (int v = b.FirstIndex; v < b.EndIndex; v += 3)
            {
                Step();
                if (PatternTrianglesOverlap(u, v)) throw new ArgumentException("Pattern fragments overlap (caps, spacing or tight turns).");
            }
        }
    }
    private P PatternVertex(int index) { var p = vertices[indices[index]].Position; return new(p.X, p.Y); }
    private bool PatternTrianglesOverlap(int a, int b)
    {
        // Strict separating axes on actual float output. Shared edges/vertices are allowed.
        for (int side = 0; side < 2; side++)
        {
            int first = side == 0 ? a : b;
            for (int edge = 0; edge < 3; edge++)
            {
                var p = PatternVertex(first + edge); var q = PatternVertex(first + (edge + 1) % 3);
                var n = new P(p.Y - q.Y, q.X - p.X);
                double amin = double.PositiveInfinity, amax = double.NegativeInfinity, bmin = amin, bmax = amax;
                for (int k = 0; k < 3; k++)
                {
                    double x = Dot(PatternVertex(a + k) - p, n), y = Dot(PatternVertex(b + k) - p, n);
                    amin = Math.Min(amin, x); amax = Math.Max(amax, x); bmin = Math.Min(bmin, y); bmax = Math.Max(bmax, y);
                }
                if (amax <= bmin || bmax <= amin) return false;
            }
        }
        return true;
    }
}
