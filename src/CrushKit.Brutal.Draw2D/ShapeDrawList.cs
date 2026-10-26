using Brutal.Numerics;

namespace CrushKit.Brutal.Draw2D;

/// <summary>Reusable, synchronous triangle producer. Not thread safe. Open polyline outlines must be simple:
/// reversals, crossing, touching or overlapping boundaries are rejected (including excessively wide tight turns).
/// Every supplied component and scalar constraint is validated first. Known no-output cases (zero alpha,
/// empty clip, zero extent, or exactly singular transform) skip generated-geometry and output-budget checks.
/// Finite nonsingular transforms can exceed FLOAT32 representability: inverted quantized tiles are rejected
/// transactionally rather than submitted with translucent overlap. No arbitrary determinant cutoff is used.
/// Borrowed frame buffers become invalid at the next BeginFrame or disposal.</summary>
public sealed partial class ShapeDrawList : IDisposable
{
    private readonly int maxVertices, maxIndices;
    private ShapeVertex[] vertices = [];
    private uint[] indices = [];
    private ShapeCommand[] commands = [];
    private int vc, ic, cc, width, height;
    private bool active, disposed;
    private float3x2 world, local;
    private readonly List<PixelRect> clips = [];
    private readonly List<P> polygon = [], path = [], left = [], right = [], scratch = [], precise = [];
    private readonly List<int> triangles = [];
    private int[] previous = [], next = [];
    private bool[] live = [], ear = [];
    private readonly Queue<int> candidates = new();
    private readonly DiskMeshAdapter disk = new();
    private readonly record struct P(double X, double Y)
    {
        public static P operator +(P a, P b) => new(a.X + b.X, a.Y + b.Y);
        public static P operator -(P a, P b) => new(a.X - b.X, a.Y - b.Y);
        public static P operator *(P a, double b) => new(a.X * b, a.Y * b);
    }
    /// <summary>Actual indices emitted so far in the active frame, independent of vertex colors.</summary>
    public int IndexCount { get { State(); return ic; } }

    public ShapeDrawList(int maxVertices = 1_000_000, int maxIndices = 3_000_000)
    {
        if (maxVertices < 0 || maxIndices < 0) throw new ArgumentOutOfRangeException(nameof(maxVertices));
        this.maxVertices = maxVertices;
        this.maxIndices = maxIndices;
    }
    /// <summary>Starts a new frame, invalidating prior borrowed buffers. Frame dimensions must be strictly positive.</summary>
    public void BeginFrame(int width, int height) => BeginFrame(width, height, float3x2.Identity);
    public void BeginFrame(int width, int height, float3x2 worldToPixel)
    {
        Alive();
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Matrix(worldToPixel);
        this.width = width;
        this.height = height;
        world = worldToPixel;
        local = float3x2.Identity;
        vc = ic = cc = 0;
        bakedVertices = bakedIndices = 0;
        clips.Clear();
        clips.Add(new(0, 0, width, height));
        active = true;
    }
    public void SetTransform(float3x2 localToWorld)
    {
        State();
        Matrix(localToWorld);
        local = localToWorld;
    }
    public void PushClip(PixelRect clip)
    {
        State();
        if (clip.Right < clip.Left || clip.Bottom < clip.Top) throw new ArgumentException("Inverted clip.");
        var p = clips[^1];
        int l = Math.Max(p.Left, clip.Left), t = Math.Max(p.Top, clip.Top);
        int r = Math.Min(p.Right, clip.Right), b = Math.Min(p.Bottom, clip.Bottom);
        // Canonical empty intersection stays within its parent/framebuffer.
        l = Math.Min(l, p.Right);
        t = Math.Min(t, p.Bottom);
        r = Math.Max(l, r);
        b = Math.Max(t, b);
        clips.Add(new(l, t, r, b));
    }
    public void PopClip()
    {
        State();
        if (clips.Count == 1) throw new InvalidOperationException("Clip underflow.");
        clips.RemoveAt(clips.Count - 1);
    }
    public ShapeFrame Finish()
    {
        State();
        if (clips.Count != 1) throw new InvalidOperationException("Unbalanced clips.");
        active = false;
        return new(width, height, vertices.AsMemory(0, vc), indices.AsMemory(0, ic), commands.AsMemory(0, cc));
    }
    public void FillTriangle(float2 a, float2 b, float2 c, LinearColor color)
    {
        Start(color);
        var p = Input(a);
        var q = Input(b);
        var r = Input(c);
        if (NoOutput(color) || Cross(q - p, r - p) == 0) return;
        polygon.Add(p);
        polygon.Add(q);
        polygon.Add(r);
        Convex(color);
    }
    public void FillRect(float2 origin, float2 size, LinearColor color)
    {
        Start(color);
        var o = Input(origin);
        var s = Size(size);
        if (NoOutput(color) || s.X == 0 || s.Y == 0) return;
        Rect(o, s);
        Convex(color);
    }
    /// <summary>Local vertical gradient, premultiplied linear endpoints interpolated by the GPU.
    /// Uses the same transactional precision, transform, clip and budget contract as FillRect.</summary>
    public void FillVerticalGradientRect(float2 origin, float2 size, LinearColor top, LinearColor bottom)
    {
        Start(top);
        bottom.Validate();
        var o = Input(origin);
        var s = Size(size);
        if ((top.A == 0 && bottom.A == 0) || clips[^1].IsEmpty || Singular(local) || Singular(world) || s.X == 0 || s.Y == 0) return;
        Rect(o, s);
        int start = vc;
        // Choose an emitting endpoint even when the other endpoint is transparent.
        Convex(top.A != 0 ? top : bottom);
        if (vc == start) return;
        vertices[start] = new(vertices[start].Position, top.Premultiplied);
        vertices[start + 1] = new(vertices[start + 1].Position, top.Premultiplied);
        vertices[start + 2] = new(vertices[start + 2].Position, bottom.Premultiplied);
        vertices[start + 3] = new(vertices[start + 3].Position, bottom.Premultiplied);
    }
    private void Rect(P o, P s)
    {
        polygon.Add(o);
        polygon.Add(o + new P(s.X, 0));
        polygon.Add(o + s);
        polygon.Add(o + new P(0, s.Y));
    }
    public void FillDisc(float2 center, float radius, LinearColor color)
    {
        Start(color);
        var c = Input(center);
        Nonnegative(radius);
        if (NoOutput(color) || radius == 0) return;
        int n = Resolution(radius);
        disk.Generate(n);
        var ps = disk.Positions;
        var ids = disk.Indices;
        foreach (var p in ps) polygon.Add(c + new P(p.X, p.Y) * radius);
        foreach (var i in ids) triangles.Add(checked((int)i));
        Emit(color);
    }
    public void FillRing(float2 center, float innerRadius, float outerRadius, LinearColor color)
    {
        Start(color);
        var c = Input(center);
        Nonnegative(innerRadius);
        Nonnegative(outerRadius);
        if (innerRadius > outerRadius) throw new ArgumentException("Inner radius exceeds outer radius.");
        if (NoOutput(color) || innerRadius == outerRadius) return;
        int n = Resolution(outerRadius);
        for (int i = 0; i < n; i++)
        {
            double a = i * Math.Tau / n;
            var d = new P(Math.Cos(a), Math.Sin(a));
            polygon.Add(c + d * outerRadius);
            polygon.Add(c + d * innerRadius);
        }
        for (int i = 0; i < n; i++)
        {
            int a = 2 * i, b = 2 * ((i + 1) % n);
            Tri(a, b, a + 1);
            Tri(a + 1, b, b + 1);
        }
        Emit(color);
    }
    public void FillRoundedRect(float2 origin, float2 size, float radius, LinearColor color)
    {
        Start(color);
        var o = Input(origin);
        var s = Size(size);
        Nonnegative(radius);
        if (NoOutput(color) || s.X == 0 || s.Y == 0) return;
        double r = Math.Min(radius, Math.Min(s.X, s.Y) / 2);
        if (r == 0)
        {
            Rect(o, s);
            Convex(color);
            return;
        }
        int q = (Resolution(r) + 3) / 4;
        for (int corner = 0; corner < 4; corner++)
        {
            var c = o + new P(corner is 0 or 3 ? r : s.X - r, corner < 2 ? r : s.Y - r);
            for (int j = 0; j <= q; j++)
            {
                double a = Math.PI + corner * Math.PI / 2 + j * Math.PI / (2 * q);
                P direction = j == 0 ? Cardinal(corner) : j == q ? Cardinal((corner + 1) % 4) : new P(Math.Cos(a), Math.Sin(a));
                polygon.Add(c + direction * r);
            }
        }
        Convex(color);
    }
    private static P Cardinal(int corner) => corner switch { 0 => new(-1, 0), 1 => new(0, -1), 2 => new(1, 0), _ => new(0, 1) };
    public void StrokeSegment(float2 a, float2 b, float width, LineCap cap, LinearColor color)
    {
        Start(color);
        var p = Input(a);
        var end = Input(b);
        Nonnegative(width);
        if (!Enum.IsDefined(cap)) throw new ArgumentOutOfRangeException(nameof(cap));
        if (NoOutput(color) || width == 0) return;
        var d = end - p;
        double len = Length(d), r = width / 2.0;
        if (len == 0)
        {
            if (cap == LineCap.Round) FillDisc(a, (float)r, color);
            return;
        }
        d = d * (1 / len);
        var n = new P(-d.Y, d.X);
        if (cap != LineCap.Round)
        {
            if (cap == LineCap.Square)
            {
                p -= d * r;
                end += d * r;
            }
            polygon.Add(p + n * r);
            polygon.Add(p - n * r);
            polygon.Add(end - n * r);
            polygon.Add(end + n * r);
        }
        else
        {
            int q = (Resolution(r) + 1) / 2;
            double angle = Math.Atan2(d.Y, d.X);
            for (int j = 0; j <= q; j++)
            {
                double t = angle + Math.PI / 2 + j * Math.PI / q;
                polygon.Add(p + new P(Math.Cos(t), Math.Sin(t)) * r);
            }
            for (int j = 0; j <= q; j++)
            {
                double t = angle - Math.PI / 2 + j * Math.PI / q;
                polygon.Add(end + new P(Math.Cos(t), Math.Sin(t)) * r);
            }
        }
        Convex(color);
    }
    public void StrokePolyline(ReadOnlySpan<float2> points, float width, LinearColor color)
    {
        Start(color);
        Nonnegative(width);
        if (points.Length > 1024) throw new ArgumentOutOfRangeException(nameof(points));
        path.Clear();
        foreach (var v in points) Input(v);
        if (NoOutput(color) || width == 0) return;
        foreach (var v in points)
        {
            var p = Input(v);
            if (path.Count > 0 && p == path[^1]) continue;
            while (path.Count >= 2)
            {
                var a = path[^1] - path[^2];
                var b = p - path[^1];
                if (Cross(a, b) != 0) break;
                if (Dot(a, b) < 0) throw new ArgumentException("Polyline reversal.");
                path.RemoveAt(path.Count - 1);
            }
            path.Add(p);
        }
        if (path.Count < 2 || width == 0) return;
        StrokeOpenPath(width, color);
    }
    private void StrokeOpenPath(double width, LinearColor color, LineCap cap = LineCap.Butt, bool framebuffer = false, bool quantizedEars = false)
    {
        left.Clear();
        right.Clear();
        double r = width / 2.0;
        if (cap == LineCap.Square)
        {
            path[0] -= Unit(path[1] - path[0]) * r;
            path[^1] += Unit(path[^1] - path[^2]) * r;
        }
        for (int i = 0; i < path.Count; i++)
        {
            var p = path[i];
            var before = i == 0 ? Unit(path[1] - p) : Unit(p - path[i - 1]);
            var after = i == path.Count - 1 ? before : Unit(path[i + 1] - p);
            var n0 = new P(-before.Y, before.X);
            var n1 = new P(-after.Y, after.X);
            if (i == 0 || i == path.Count - 1)
            {
                left.Add(p + n0 * r);
                right.Add(p - n0 * r);
                continue;
            }
            double turn = Cross(before, after);
            var m = (n0 + n1) * (r / (1 + Dot(before, after)));
            if (!double.IsFinite(m.X) || !double.IsFinite(m.Y)) throw new ArgumentException("Polyline reversal.");
            if (turn > 0)
            {
                left.Add(p + m);
                right.Add(p - n0 * r);
                right.Add(p - n1 * r);
            }
            else
            {
                left.Add(p + n0 * r);
                left.Add(p + n1 * r);
                right.Add(p - m);
            }
        }
        polygon.AddRange(left);
        if (cap == LineCap.Round) PatternCap(path[^1], Unit(path[^1] - path[^2]), r, framebuffer);
        for (int i = right.Count - 1; i >= 0; i--) polygon.Add(right[i]);
        if (cap == LineCap.Round) PatternCap(path[0], Unit(path[0] - path[1]), r, framebuffer);
        SimpleTriangulate(framebuffer, quantizedEars);
        Emit(color, validateOutline: true, framebuffer: framebuffer);
    }
    private bool NoOutput(LinearColor color) => color.A == 0 || clips[^1].IsEmpty || Singular(local) || Singular(world);
    private void Start(LinearColor color)
    {
        State();
        color.Validate();
        polygon.Clear();
        triangles.Clear();
    }
    private static double Cross(P a, P b) => a.X * b.Y - a.Y * b.X;
    private static double Dot(P a, P b) => a.X * b.X + a.Y * b.Y;
    private static double Length(P a) => Math.Sqrt(Dot(a, a));
    private static P Unit(P a) => a * (1 / Length(a));
    private static P Input(float2 p)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) throw new ArgumentException("Nonfinite point.");
        return new(p.X, p.Y);
    }
    private static P Size(float2 p)
    {
        var s = Input(p);
        if (s.X < 0 || s.Y < 0) throw new ArgumentOutOfRangeException(nameof(p));
        return s;
    }
    private static void Nonnegative(float x)
    {
        if (!float.IsFinite(x) || x < 0) throw new ArgumentOutOfRangeException(nameof(x));
    }
    private static void Matrix(float3x2 m)
    {
        if (!float.IsFinite(m.M11) || !float.IsFinite(m.M12) || !float.IsFinite(m.M21) || !float.IsFinite(m.M22) || !float.IsFinite(m.M31) || !float.IsFinite(m.M32)) throw new ArgumentException("Nonfinite transform.");
    }
    private static P Transform(P p, float3x2 m) => new(p.X * m.M11 + p.Y * m.M21 + m.M31, p.X * m.M12 + p.Y * m.M22 + m.M32);
    private P Pixel(P p) => Transform(Transform(p, local), world);
    private double Scale()
    {
        var a = new P((double)local.M11 * world.M11 + (double)local.M12 * world.M21, (double)local.M11 * world.M12 + (double)local.M12 * world.M22);
        var b = new P((double)local.M21 * world.M11 + (double)local.M22 * world.M21, (double)local.M21 * world.M12 + (double)local.M22 * world.M22);
        double x = Dot(a, a), y = Dot(b, b), z = Dot(a, b);
        return Math.Sqrt((x + y + Math.Sqrt((x - y) * (x - y) + 4 * z * z)) / 2);
    }
    private int Resolution(double radius, bool framebuffer = false)
    {
        double r = framebuffer ? radius : radius * Scale();
        // Reserve for float framebuffer quantization and the published generator's float trig.
        double tolerance = 0.25 - 0.09 - r * 0.000001;
        if (r == 0) return 8;
        if (tolerance <= 0) throw new ArgumentException("Curve precision limit.");
        double angle = 2 * Math.Asin(Math.Sqrt(Math.Min(1, tolerance / (2 * r))));
        double n = Math.Ceiling(Math.PI / angle);
        if (n > 4096) throw new ArgumentException("Curve tessellation exceeds 4096 segments.");
        return Math.Max(8, (int)n);
    }
    private void Tri(int a, int b, int c)
    {
        triangles.Add(a);
        triangles.Add(b);
        triangles.Add(c);
    }
    private void Convex(LinearColor color)
    {
        for (int i = 1; i + 1 < polygon.Count; i++) Tri(0, i, i + 1);
        Emit(color);
    }
    private static bool On(P a, P b, P p) => Cross(b - a, p - a) == 0 && p.X >= Math.Min(a.X, b.X) && p.X <= Math.Max(a.X, b.X) && p.Y >= Math.Min(a.Y, b.Y) && p.Y <= Math.Max(a.Y, b.Y);
    private static bool Intersects(P a, P b, P c, P d)
    {
        double x = Cross(b - a, c - a), y = Cross(b - a, d - a), z = Cross(d - c, a - c), w = Cross(d - c, b - c);
        return (Math.Sign(x) * Math.Sign(y) < 0 && Math.Sign(z) * Math.Sign(w) < 0) || On(a, b, c) || On(a, b, d) || On(c, d, a) || On(c, d, b);
    }
    private static void CleanOutline(List<P> outline)
    {
        // Remove exact duplicate/collinear outline vertices before simplicity checks and ear clipping.
        bool changed = true;
        while (changed && outline.Count >= 3)
        {
            changed = false;
            for (int i = 0; i < outline.Count; i++)
            {
                var a = outline[(i + outline.Count - 1) % outline.Count];
                var b = outline[i];
                var c = outline[(i + 1) % outline.Count];
                if (b == a || (Cross(b - a, c - b) == 0 && Dot(b - a, c - b) >= 0))
                {
                    outline.RemoveAt(i);
                    changed = true;
                    break;
                }
            }
        }
    }
    private static void ValidateSimpleOutline(List<P> outline, string message)
    {
        int n = outline.Count;
        for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++)
        {
            if (j == i + 1 || (i == 0 && j == n - 1)) continue;
            if (Intersects(outline[i], outline[(i + 1) % n], outline[j], outline[(j + 1) % n])) throw new ArgumentException(message);
        }
    }
    private void SimpleTriangulate(bool framebuffer = false, bool quantizedEars = false)
    {
        CleanOutline(polygon);
        ValidateSimpleOutline(polygon, "Self-intersecting/overlapping stroke outline.");
        double orientation = 1;
        if (quantizedEars)
        {
            // Animated pattern cuts can leave an arbitrarily short edge beside a sampled corner.
            // Triangulate the same unmoved outline using ears valid in BOTH source and FLOAT32 output.
            // This avoids ill-conditioned diagonals; Emit still rejects inverted tiles/invalid outlines.
            scratch.Clear();
            foreach (var p in polygon)
            {
                var q = framebuffer ? p : Pixel(p);
                if (!double.IsFinite(q.X) || !double.IsFinite(q.Y) || Math.Abs(q.X) > 1048576 || Math.Abs(q.Y) > 1048576)
                    throw new ArgumentException("Framebuffer coordinate envelope exceeded.");
                scratch.Add(new((float)q.X, (float)q.Y));
            }
            if (!framebuffer)
                orientation = Math.Sign((double)local.M11 * local.M22 - (double)local.M12 * local.M21) *
                    Math.Sign((double)world.M11 * world.M22 - (double)world.M12 * world.M21);
        }
        EarTriangulate(quantized: quantizedEars ? scratch : null, orientation: orientation);
    }
    private void EarTriangulate(bool hole = false, List<P>? quantized = null, double orientation = 1)
    {
        int n = polygon.Count;
        double area = 0;
        for (int i = 1; i + 1 < n; i++) area += Cross(polygon[i] - polygon[0], polygon[i + 1] - polygon[0]);
        if (area == 0) return;
        double sign = Math.Sign(area);
        Grow(ref previous, n, hole ? 16384 : 4096); Grow(ref next, n, hole ? 16384 : 4096);
        Grow(ref live, n, hole ? 16384 : 4096); Grow(ref ear, n, hole ? 16384 : 4096);
        candidates.Clear();
        for (int i = 0; i < n; i++)
        {
            previous[i] = (i + n - 1) % n; next[i] = (i + 1) % n; live[i] = true;
        }
        // An existing ear remains an ear when unrelated vertices disappear. Only the
        // two neighbors of a removed ear need recomputing: bounded quadratic work.
        for (int i = 0; i < n; i++) UpdateEar(i, n, sign, hole, quantized, sign * orientation);
        int remaining = n;
        while (remaining > 3)
        {
            int b;
            do
            {
                if (!candidates.TryDequeue(out b)) throw new ArgumentException("Stroke outline cannot be triangulated.");
            } while (!live[b] || !ear[b]);
            int a = previous[b], c = next[b];
            Tri(a, b, c); live[b] = false; ear[b] = false;
            next[a] = c; previous[c] = a; remaining--;
            UpdateEar(a, n, sign, hole, quantized, sign * orientation); UpdateEar(c, n, sign, hole, quantized, sign * orientation);
        }
        for (int i = 0; i < n; i++) if (live[i]) { Tri(previous[i], i, next[i]); break; }
    }
    private void UpdateEar(int b, int n, double sign, bool hole = false, List<P>? quantized = null, double outputSign = 1)
    {
        int a = previous[b], c = next[b];
        ear[b] = false;
        if (sign * Cross(polygon[b] - polygon[a], polygon[c] - polygon[b]) <= 0) return;
        // Zero-area output ears remain removable (Emit already drops them); inverted ears do not.
        double outputArea = quantized is null ? 0 : outputSign * Cross(quantized[b] - quantized[a], quantized[c] - quantized[a]);
        if (outputArea < 0) return;
        for (int j = 0; j < n; j++)
        {
            if (!live[j] || j == a || j == b || j == c) continue;
            if (hole && (polygon[j] == polygon[a] || polygon[j] == polygon[b] || polygon[j] == polygon[c])) continue;
            if (sign * Cross(polygon[b] - polygon[a], polygon[j] - polygon[a]) >= 0 &&
                sign * Cross(polygon[c] - polygon[b], polygon[j] - polygon[b]) >= 0 &&
                sign * Cross(polygon[a] - polygon[c], polygon[j] - polygon[c]) >= 0) return;
            if (quantized is not null && outputArea > 0 &&
                outputSign * Cross(quantized[b] - quantized[a], quantized[j] - quantized[a]) >= 0 &&
                outputSign * Cross(quantized[c] - quantized[b], quantized[j] - quantized[b]) >= 0 &&
                outputSign * Cross(quantized[a] - quantized[c], quantized[j] - quantized[c]) >= 0) return;
        }
        ear[b] = true; candidates.Enqueue(b);
    }
    private void Emit(LinearColor color, bool validateOutline = false, bool framebuffer = false)
    {
        scratch.Clear(); precise.Clear();
        foreach (var p in polygon)
        {
            var q = framebuffer ? p : Pixel(p);
            if (!double.IsFinite(q.X) || !double.IsFinite(q.Y) || Math.Abs(q.X) > 1048576 || Math.Abs(q.Y) > 1048576) throw new ArgumentException("Framebuffer coordinate envelope exceeded.");
            // Validate and remove degeneracies in actual float output, not just double construction.
            precise.Add(q);
            scratch.Add(new((float)q.X, (float)q.Y));
        }
        int count = 0;
        for (int i = 0; i < triangles.Count; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            double area = Cross(scratch[b] - scratch[a], scratch[c] - scratch[a]);
            if (area == 0) continue;
            double sourceArea = Cross(precise[b] - precise[a], precise[c] - precise[a]);
            if (Math.Sign(area) != Math.Sign(sourceArea))
                throw new ArgumentException(FormattableString.Invariant($"Framebuffer FLOAT32 precision inverts a geometry tile. triangle={i / 3}; sourceArea={sourceArea:R}; outputArea={area:R}."));
            triangles[count++] = a;
            triangles[count++] = b;
            triangles[count++] = c;
        }
        if (count == 0 || color.A == 0 || clips[^1].IsEmpty || Singular(local) || Singular(world)) return;
        if (validateOutline)
        {
            right.Clear(); right.AddRange(scratch); CleanOutline(right);
            ValidateSimpleOutline(right, "Framebuffer FLOAT32 precision breaks the simple stroke outline.");
        }
        if (polygon.Count > maxVertices - vc - bakedVertices || count > maxIndices - ic - bakedIndices) throw new InvalidOperationException("Frame geometry budget exceeded.");
        Grow(ref vertices, vc + polygon.Count, maxVertices);
        Grow(ref indices, ic + count, maxIndices);
        Grow(ref commands, cc + 1, Math.Max(1, maxIndices / 3));
        var rgba = color.Premultiplied;
        int start = vc;
        foreach (var p in scratch) vertices[vc++] = new(new float2((float)p.X, (float)p.Y), rgba);
        for (int i = 0; i < count; i++) indices[ic + i] = (uint)(start + triangles[i]);
        var clip = clips[^1];
        if (cc > 0 && commands[cc - 1].Clip == clip)
            commands[cc - 1] = commands[cc - 1] with { IndexCount = commands[cc - 1].IndexCount + count };
        else commands[cc++] = new(ic, count, clip);
        ic += count;
    }
    private static bool Singular(float3x2 m) => (double)m.M11 * m.M22 - (double)m.M12 * m.M21 == 0;
    private static void Grow<T>(ref T[] array, int needed, int limit)
    {
        if (array.Length < needed) Array.Resize(ref array, (int)Math.Min(limit, Math.Max((long)needed, Math.Max(16L, array.Length * 2L))));
    }
    private void Alive()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    private void State()
    {
        Alive();
        if (!active) throw new InvalidOperationException("BeginFrame required.");
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        active = false;
        disk.Dispose();
        vertices = [];
        indices = [];
        commands = [];
    }
}
