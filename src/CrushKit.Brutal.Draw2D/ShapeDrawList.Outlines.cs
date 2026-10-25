using Brutal.Numerics;

namespace CrushKit.Brutal.Draw2D;

public sealed partial class ShapeDrawList
{
    private readonly List<P> outlineBoundary = [], outlineOuter = [], outlineInner = [], outlineClip = [];

    private void StartOutline(float width, LinearColor color, StrokeAlignment alignment, StrokeSpace space)
    {
        Start(color);
        Nonnegative(width);
        if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
        if (!Enum.IsDefined(space)) throw new ArgumentOutOfRangeException(nameof(space));
        outlineBoundary.Clear(); outlineOuter.Clear(); outlineInner.Clear();
    }

    /// <summary>Annular outline; a consumed hole becomes solid. Framebuffer strokes offset the
    /// transformed sampled contour, not an inflated scalar radius. Zero radius is empty.</summary>
    public void StrokeCircle(float2 center, float radius, float width, LinearColor color,
        StrokeAlignment alignment = StrokeAlignment.Center, StrokeSpace space = StrokeSpace.Local)
    {
        StartOutline(width, color, alignment, space);
        var c = Input(center); Nonnegative(radius);
        if (NoOutput(color) || width == 0 || radius == 0) return;
        var (outside, inside) = Distances(width, alignment);
        int n = RoundResolution(space == StrokeSpace.Local ? radius + outside : radius * Scale() + outside, space);
        if (space == StrokeSpace.Framebuffer)
        {
            CircleBoundary(outlineBoundary, c, radius, n);
            StrokeBoundary(width, color, alignment, space);
        }
        else
        {
            CircleBoundary(outlineOuter, c, radius + outside, n);
            if (radius > inside) CircleBoundary(outlineInner, c, radius - inside, n);
            EmitOutline(color, space);
        }
    }

    /// <summary>Radius clamps to half the shorter extent. Local offsets adjust corner radii,
    /// clamping eroded radii to zero; zero input radius retains rectangular miter corners.
    /// Framebuffer offsets operate on the transformed sampled boundary.</summary>
    public void StrokeRoundedRect(float2 origin, float2 size, float radius, float width, LinearColor color,
        StrokeAlignment alignment = StrokeAlignment.Center, StrokeSpace space = StrokeSpace.Local)
    {
        StartOutline(width, color, alignment, space);
        var o = Input(origin); var s = Size(size); Nonnegative(radius);
        if (NoOutput(color) || width == 0 || s.X == 0 || s.Y == 0) return;
        double r = Math.Min(radius, Math.Min(s.X, s.Y) / 2);
        var (outside, inside) = Distances(width, alignment);
        // A zero-radius boundary has miter corners only; width does not introduce arcs.
        int n = r == 0 ? 8 : RoundResolution(space == StrokeSpace.Local ? r + outside : r * Scale() + outside, space);
        if (space == StrokeSpace.Framebuffer)
        {
            RoundedBoundary(outlineBoundary, o, s, r, n / 4);
            StrokeBoundary(width, color, alignment, space);
        }
        else
        {
            RoundedBoundary(outlineOuter, o - new P(outside, outside), s + new P(2 * outside, 2 * outside), r == 0 ? 0 : r + outside, n / 4);
            if (s.X > 2 * inside && s.Y > 2 * inside)
                RoundedBoundary(outlineInner, o + new P(inside, inside), s - new P(2 * inside, 2 * inside), Math.Max(0, r - inside), n / 4);
            EmitOutline(color, space);
        }
    }

    private int RoundResolution(double radius, StrokeSpace space) => (Resolution(radius, space == StrokeSpace.Framebuffer) + 3) / 4 * 4;

    private void CircleBoundary(List<P> target, P center, double radius, int n)
    {
        // Reuse the published disk adapter's rim and seam policy; cardinal points are exact.
        disk.Generate(n);
        var rim = disk.Positions;
        for (int i = 0; i < n; i++)
        {
            var direction = i % (n / 4) == 0 ? new P(i == 0 ? 1 : i == n / 2 ? -1 : 0, i == n / 4 ? 1 : i == 3 * n / 4 ? -1 : 0) : new P(rim[3 * ((n / 2 - i + n) % n)].X, rim[3 * ((n / 2 - i + n) % n)].Y);
            target.Add(center + direction * radius);
        }
    }

    private static void RoundedBoundary(List<P> target, P o, P s, double r, int q)
    {
        if (r == 0)
        {
            target.Add(o); target.Add(o + new P(s.X, 0)); target.Add(o + s); target.Add(o + new P(0, s.Y));
            return;
        }
        for (int corner = 0; corner < 4; corner++)
        {
            var c = o + new P(corner is 0 or 3 ? r : s.X - r, corner < 2 ? r : s.Y - r);
            for (int j = 0; j <= q; j++)
            {
                double angle = Math.PI + corner * Math.PI / 2 + j * Math.PI / (2 * q);
                var direction = j == 0 ? Cardinal(corner) : j == q ? Cardinal((corner + 1) % 4) : new P(Math.Cos(angle), Math.Sin(angle));
                target.Add(c + direction * r);
            }
        }
        CleanOutline(target);
    }

    /// <summary>Convex outline with miter corners. Zero extent is empty.</summary>
    public void StrokeRect(float2 origin, float2 size, float width, LinearColor color,
        StrokeAlignment alignment = StrokeAlignment.Center, StrokeSpace space = StrokeSpace.Local)
    {
        StartOutline(width, color, alignment, space);
        var o = Input(origin); var s = Size(size);
        if (NoOutput(color) || width == 0 || s.X == 0 || s.Y == 0) return;
        Rect(o, s); outlineBoundary.AddRange(polygon); polygon.Clear();
        StrokeBoundary(width, color, alignment, space);
    }

    /// <summary>Convex outline with miter corners. Collinear triangles are empty.</summary>
    public void StrokeTriangle(float2 a, float2 b, float2 c, float width, LinearColor color,
        StrokeAlignment alignment = StrokeAlignment.Center, StrokeSpace space = StrokeSpace.Local)
    {
        StartOutline(width, color, alignment, space);
        var p = Input(a); var q = Input(b); var r = Input(c);
        if (NoOutput(color) || width == 0 || Cross(q - p, r - p) == 0) return;
        outlineBoundary.Add(p); outlineBoundary.Add(q); outlineBoundary.Add(r);
        StrokeBoundary(width, color, alignment, space);
    }

    /// <summary>Simple convex closed contours only, at most 1024 supplied points. Uses miter corners;
    /// duplicate and forward-collinear points are removed. Nonconvex/crossing contours reject visibly.
    /// Erosion can remove edges or consume the entire hole, leaving a solid outer silhouette.</summary>
    public void StrokeClosedPolyline(ReadOnlySpan<float2> points, float width, LinearColor color,
        StrokeAlignment alignment = StrokeAlignment.Center, StrokeSpace space = StrokeSpace.Local)
    {
        StartOutline(width, color, alignment, space);
        if (points.Length > 1024) throw new ArgumentOutOfRangeException(nameof(points));
        foreach (var point in points) Input(point);
        if (NoOutput(color) || width == 0) return;
        foreach (var point in points) outlineBoundary.Add(Input(point));
        CleanOutline(outlineBoundary);
        if (outlineBoundary.Count < 3) return;
        StrokeBoundary(width, color, alignment, space);
    }

    // These contour/offset operations intentionally stay independent of public shape entry points,
    // so a subsequent patterned-path sampler can reuse the selected measurement-space boundary.
    private void PrepareBoundary(StrokeSpace space)
    {
        CleanOutline(outlineBoundary);
        ValidateSimpleOutline(outlineBoundary, "Closed stroke requires a simple convex contour.");
        double area = 0;
        for (int i = 1; i + 1 < outlineBoundary.Count; i++)
            area += Cross(outlineBoundary[i] - outlineBoundary[0], outlineBoundary[i + 1] - outlineBoundary[0]);
        if (area == 0) throw new ArgumentException("Closed stroke requires a nondegenerate convex contour.");
        if (area < 0) outlineBoundary.Reverse();
        for (int i = 0; i < outlineBoundary.Count; i++)
        {
            var a = outlineBoundary[i]; var b = outlineBoundary[(i + 1) % outlineBoundary.Count];
            var c = outlineBoundary[(i + 2) % outlineBoundary.Count];
            if (Cross(b - a, c - b) <= 0) throw new ArgumentException("Closed stroke requires a convex contour.");
        }
        if (space == StrokeSpace.Framebuffer)
        {
            for (int i = 0; i < outlineBoundary.Count; i++) outlineBoundary[i] = Pixel(outlineBoundary[i]);
            if (Cross(outlineBoundary[1] - outlineBoundary[0], outlineBoundary[2] - outlineBoundary[1]) < 0)
                outlineBoundary.Reverse();
        }
    }

    private static (double outside, double inside) Distances(float width, StrokeAlignment alignment) =>
        alignment switch { StrokeAlignment.Inside => (0, width), StrokeAlignment.Outside => (width, 0), _ => (width / 2.0, width / 2.0) };

    private void StrokeBoundary(float width, LinearColor color, StrokeAlignment alignment, StrokeSpace space)
    {
        PrepareBoundary(space);
        var (outside, inside) = Distances(width, alignment);
        OffsetOuter(outside);
        ErodeInner(inside);
        EmitOutline(color, space);
    }

    private void OffsetOuter(double distance)
    {
        int n = outlineBoundary.Count;
        for (int i = 0; i < n; i++)
        {
            var p = outlineBoundary[i];
            if (distance == 0) { outlineOuter.Add(p); continue; }
            var a = Unit(p - outlineBoundary[(i + n - 1) % n]);
            var b = Unit(outlineBoundary[(i + 1) % n] - p);
            // Intersect shifted support lines. Cross formulation avoids 1+dot cancellation at acute tips.
            var na = new P(a.Y, -a.X); var nb = new P(b.Y, -b.X);
            double turn = Cross(a, b);
            var q = p + na * distance + a * (Cross((nb - na) * distance, b) / turn);
            if (!double.IsFinite(q.X) || !double.IsFinite(q.Y)) throw new ArgumentException("Invalid stroke offset.");
            outlineOuter.Add(q);
        }
    }

    private void ErodeInner(double distance)
    {
        outlineInner.AddRange(outlineBoundary);
        if (distance == 0) return;
        // Intersection of inward shifted halfplanes, rather than intersecting only adjacent lines:
        // narrow edges may vanish before the hole is fully consumed.
        for (int edge = 0; edge < outlineBoundary.Count && outlineInner.Count > 0; edge++)
        {
            var a = outlineBoundary[edge]; var d = Unit(outlineBoundary[(edge + 1) % outlineBoundary.Count] - a);
            double Side(P p) => Cross(d, p - a) - distance;
            outlineClip.Clear();
            var previousPoint = outlineInner[^1]; double ps = Side(previousPoint);
            foreach (var current in outlineInner)
            {
                double cs = Side(current);
                if ((cs > 0) != (ps > 0)) outlineClip.Add(previousPoint + (current - previousPoint) * (ps / (ps - cs)));
                if (cs > 0) outlineClip.Add(current);
                previousPoint = current; ps = cs;
            }
            outlineInner.Clear(); outlineInner.AddRange(outlineClip);
        }
        CleanOutline(outlineInner);
        if (outlineInner.Count < 3) { outlineInner.Clear(); return; }
        // At exact consumption, support-line normalization/intersections can leave a tiny
        // residual polygon (e.g. the 6/8/10 triangle at inset 2). Discard only a double-roundoff
        // sized remnant, not a pixel-sized or user-scale epsilon; real thin holes still validate.
        double magnitude = distance, area2 = 0, perimeter = 0;
        foreach (var p in outlineBoundary) magnitude = Math.Max(magnitude, Length(p - outlineBoundary[0]));
        for (int i = 0; i < outlineInner.Count; i++)
        {
            var a = outlineInner[i]; var b = outlineInner[(i + 1) % outlineInner.Count];
            area2 += Cross(a - outlineInner[0], b - outlineInner[0]);
            perimeter += Length(b - a);
        }
        if (Math.Abs(area2) <= perimeter * magnitude * (64 * 2.2204460492503131e-16)) outlineInner.Clear();
    }

    private void ValidatePixelLoop(List<P> loop, StrokeSpace space)
    {
        outlineClip.Clear();
        foreach (var p in loop)
        {
            var q = space == StrokeSpace.Framebuffer ? p : Pixel(p);
            if (!double.IsFinite(q.X) || !double.IsFinite(q.Y) || Math.Abs(q.X) > 1048576 || Math.Abs(q.Y) > 1048576)
                throw new ArgumentException("Framebuffer coordinate envelope exceeded.");
            outlineClip.Add(new((float)q.X, (float)q.Y));
        }
        CleanOutline(outlineClip);
        if (outlineClip.Count < 3) throw new ArgumentException("Framebuffer FLOAT32 precision collapses stroke boundary.");
        ValidateSimpleOutline(outlineClip, "Framebuffer FLOAT32 precision breaks stroke boundary.");
        double sign = Math.Sign(Cross(outlineClip[1] - outlineClip[0], outlineClip[2] - outlineClip[1]));
        if (sign == 0) throw new ArgumentException("Framebuffer FLOAT32 precision collapses stroke boundary.");
        for (int i = 0; i < outlineClip.Count; i++)
            if (sign * Cross(outlineClip[(i + 1) % outlineClip.Count] - outlineClip[i], outlineClip[(i + 2) % outlineClip.Count] - outlineClip[(i + 1) % outlineClip.Count]) < 0)
                throw new ArgumentException("Framebuffer FLOAT32 precision breaks convex stroke boundary.");
    }

    private void EmitOutline(LinearColor color, StrokeSpace space)
    {
        CleanOutline(outlineOuter); CleanOutline(outlineInner);
        ValidatePixelLoop(outlineOuter, space);
        left.Clear(); left.AddRange(outlineClip);
        if (outlineInner.Count == 0)
        {
            polygon.AddRange(outlineOuter);
            for (int i = 1; i + 1 < polygon.Count; i++) Tri(0, i, i + 1);
        }
        else
        {
            ValidatePixelLoop(outlineInner, space);
            double sign = Math.Sign(Cross(left[1] - left[0], left[2] - left[1]));
            foreach (var p in outlineClip)
                for (int i = 0; i < left.Count; i++)
                    if (sign * Cross(left[(i + 1) % left.Count] - left[i], p - left[i]) <= 0)
                        throw new ArgumentException("Framebuffer FLOAT32 precision closes or crosses the stroke hole.");
            // Bridge the rightmost vertices. Its open segment lies strictly to the right of the
            // convex hole, and within the convex outer contour. Duplicate endpoints represent a
            // topological cut, not overlapping geometry. All tiles use only boundary vertices.
            int oi = Rightmost(outlineOuter), ii = Rightmost(outlineInner);
            for (int i = 0; i < outlineOuter.Count; i++) polygon.Add(outlineOuter[(oi + i) % outlineOuter.Count]);
            polygon.Add(outlineOuter[oi]);
            for (int i = 0; i < outlineInner.Count; i++) polygon.Add(outlineInner[(ii - i + outlineInner.Count) % outlineInner.Count]);
            polygon.Add(outlineInner[ii]);
            EarTriangulate(hole: true);
        }
        Emit(color, framebuffer: space == StrokeSpace.Framebuffer);
    }

    private static int Rightmost(List<P> loop)
    {
        int best = 0;
        for (int i = 1; i < loop.Count; i++)
            if (loop[i].X > loop[best].X || (loop[i].X == loop[best].X && loop[i].Y < loop[best].Y)) best = i;
        return best;
    }
}
