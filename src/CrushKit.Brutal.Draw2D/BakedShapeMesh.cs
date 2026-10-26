using Brutal.Numerics;
using System.Threading;

namespace CrushKit.Brutal.Draw2D;

/// <summary>Local axis-aligned rectangle endpoints. Compound input order is retained.</summary>
public readonly record struct MeshRectangle(double Left, double Top, double Right, double Bottom);

/// <summary>Owned immutable local geometry. Device buffers are owned separately by the renderer.</summary>
public sealed class BakedShapeMesh
{
    private static long nextIdentity;
    private readonly float2[] vertices;
    private readonly uint[] indices;
    public long Identity { get; } = Interlocked.Increment(ref nextIdentity);
    public ReadOnlySpan<float2> Vertices => vertices;
    public ReadOnlySpan<uint> Indices => indices;
    public int VertexCount => vertices.Length;
    public int IndexCount => indices.Length;
    // Includes private arrays, object/certificate and cache/map entry allowance, not only GPU payload.
    public long CpuCharge => checked(512L + vertices.Length * 8L + indices.Length * 4L);
    public long GpuBytes => checked(vertices.Length * 8L + indices.Length * 4L);
    public double MaximumAbsoluteCoordinate => Bound;
    internal double Bound { get; }
    internal double MinimumArea { get; }
    internal double MaximumEdgeSum { get; }
    internal double SourceError { get; }
    internal double FeatureSeparation { get; }
    internal bool IsDisc { get; }
    internal int Resolution { get; }
    internal double UnitCurveError { get; }

    private BakedShapeMesh(float2[] points, uint[] triangles, bool disc, int resolution, double sourceError, double separation)
    {
        vertices = points; indices = triangles; IsDisc = disc; Resolution = resolution;
        SourceError = sourceError; FeatureSeparation = separation;
        double bound = 0, area = double.PositiveInfinity, edges = 0, curveError=0;
        int winding=0;float2 firstRim=default,previousRim=default;
        foreach (var p in points)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y)) throw new ArgumentException("Nonfinite local mesh.");
            bound = Math.Max(bound, Math.Max(Math.Abs((double)p.X), Math.Abs((double)p.Y)));
        }
        for (int i = 0; i < triangles.Length; i += 3)
        {
            if (triangles[i] >= points.Length || triangles[i+1] >= points.Length || triangles[i+2] >= points.Length)
                throw new ArgumentException("Local mesh index out of range.");
            var a = points[triangles[i]]; var b = points[triangles[i+1]]; var c = points[triangles[i+2]];
            double x = (double)b.X-a.X, y = (double)b.Y-a.Y, z = (double)c.X-a.X, w = (double)c.Y-a.Y;
            double cross = Math.Abs(x*w-y*z);
            if (!(cross > 0)) throw new ArgumentException("Degenerate local mesh.");
            if(disc)
            {
                double radialCross=(double)a.X*b.Y-(double)a.Y*b.X;
                if(c.X!=0 || c.Y!=0 || !(radialCross<0) || (i>0 && !a.Equals(previousRim)))throw new ArgumentException($"Disk fan mismatch: triangle={i/3}; a={a}; b={b}; c={c}; radialCross={radialCross:R}; previous={previousRim}.");
                if(i==0)firstRim=a;previousRim=b;
                if(a.Y<=0 && b.Y>0)winding++;
                double chord=Math.Sqrt(x*x+y*y);
                double distance=Math.Abs(radialCross)/chord;
                double ra=Math.Sqrt((double)a.X*a.X+(double)a.Y*a.Y),rb=Math.Sqrt((double)b.X*b.X+(double)b.Y*b.Y);
                // Absolute margin dominates double products/subtraction/division/sqrt on this unit fan.
                curveError=Math.Max(curveError,Math.Max(1-distance,Math.Max(ra,rb)-1)+1e-12);
            }
            double magnitude=Math.Max(Math.Max(Math.Abs((double)a.X),Math.Abs((double)a.Y)),Math.Max(Math.Max(Math.Abs((double)b.X),Math.Abs((double)b.Y)),Math.Max(Math.Abs((double)c.X),Math.Abs((double)c.Y))));
            double lower=Math.Max(0,Math.BitDecrement(cross-64*2.220446049250313e-16*magnitude*magnitude));
            area = Math.Min(area, lower); edges = Math.Max(edges, Math.BitIncrement((Math.Abs(x)+Math.Abs(y)+Math.Abs(z)+Math.Abs(w))*(1+8*2.220446049250313e-16)));
        }
        if(disc && (winding!=1 || !previousRim.Equals(firstRim)))throw new ArgumentException("Disk fan must close exactly once.");
        UnitCurveError=Math.BitIncrement(curveError);
        Bound = Math.BitIncrement(bound);
        MinimumArea = Math.BitDecrement(area);
        MaximumEdgeSum = Math.BitIncrement(edges);
    }

    public static long DiscCharge(int resolution)
    {
        if (resolution is < 8 or > 4096) throw new ArgumentOutOfRangeException(nameof(resolution));
        // Reserve bounded generation/copy scratch as well as the retained mesh; release surplus after bake.
        return checked(4096L + (resolution+2L)*512);
    }
    public static BakedShapeMesh Disc(int resolution)
    {
        _ = DiscCharge(resolution);
        using var disk = new DiskMeshAdapter(); disk.Generate(resolution);
        var points = new float2[disk.Positions.Length];
        for (int i = 0; i < points.Length; i++) points[i] = new(disk.Positions[i].X, disk.Positions[i].Y);
        return new(points, disk.Indices.ToArray(), true, resolution, 0, 0);
    }

    public static long RectanglesCharge(int count)
    {
        if (count is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(count));
        return checked(1024L + count*128L);
    }
    public static BakedShapeMesh Rectangles(ReadOnlySpan<MeshRectangle> rectangles)
    {
        _ = RectanglesCharge(rectangles.Length);
        var points = new float2[rectangles.Length*4]; var triangles = new uint[rectangles.Length*6];
        double error = 0, separation = double.PositiveInfinity;
        Span<double> xs = stackalloc double[8]; Span<double> ys = stackalloc double[8];
        for (int i = 0; i < rectangles.Length; i++)
        {
            var r = rectangles[i];
            if (!double.IsFinite(r.Left) || !double.IsFinite(r.Right) || !double.IsFinite(r.Top) || !double.IsFinite(r.Bottom) || !(r.Left < r.Right) || !(r.Top < r.Bottom))
                throw new ArgumentException("Rectangles require finite ordered endpoints.");
            for (int j = 0; j < i; j++)
            {
                var q = rectangles[j];
                if (r.Left < q.Right && q.Left < r.Right && r.Top < q.Bottom && q.Top < r.Bottom)
                    throw new ArgumentException("Compound rectangles may touch but must not overlap.");
            }
            float l=(float)r.Left, t=(float)r.Top, h=(float)r.Right, b=(float)r.Bottom;
            if (!(l<h) || !(t<b)) throw new ArgumentException("Local rectangle collapses in FLOAT32.");
            points[i*4]=new(l,t); points[i*4+1]=new(h,t); points[i*4+2]=new(h,b); points[i*4+3]=new(l,b);
            uint v=(uint)(i*4); int k=i*6;
            triangles[k]=v; triangles[k+1]=v+1; triangles[k+2]=v+2;
            triangles[k+3]=v; triangles[k+4]=v+2; triangles[k+5]=v+3;
            xs[i*2]=r.Left; xs[i*2+1]=r.Right; ys[i*2]=r.Top; ys[i*2+1]=r.Bottom;
            error=Math.Max(error,Math.Max(Math.Max(Math.Abs(l-r.Left),Math.Abs(h-r.Right)),Math.Max(Math.Abs(t-r.Top),Math.Abs(b-r.Bottom))));
        }
        for (int i=0;i<rectangles.Length*2;i++) for(int j=0;j<i;j++)
        {
            double dx=Math.Abs(xs[i]-xs[j]), dy=Math.Abs(ys[i]-ys[j]);
            if(dx>0) separation=Math.Min(separation,dx);
            if(dy>0) separation=Math.Min(separation,dy);
        }
        return new(points,triangles,false,0,Math.BitIncrement(error),Math.BitDecrement(separation));
    }
}

/// <summary>Explicit immutable bounds. No defaults or process-global cache.</summary>
public sealed class BakedMeshLimits
{
    public int MeshCount { get; }
    public long CpuBytes { get; }
    public long GpuBytes { get; }
    public int Instances { get; }
    public int Operations { get; }
    public BakedMeshLimits(int meshCount,long cpuBytes,long gpuBytes,int instances,int operations)
    {
        if(meshCount<1 || cpuBytes<1 || gpuBytes<cpuBytes || instances<1 || operations<1 || cpuBytes>int.MaxValue || gpuBytes>int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(meshCount));
        MeshCount=meshCount; CpuBytes=cpuBytes; GpuBytes=gpuBytes; Instances=instances; Operations=operations;
    }
}

/// <summary>Frame-pinned, bounded CPU owner cache. Keys must include every geometry input/revision.</summary>
public sealed class BakedMeshCache : IDisposable
{
    private sealed record Entry(long Key,BakedShapeMesh Mesh) { public long Stamp; public bool Pinned; }
    private readonly List<Entry> entries = [];
    public BakedMeshLimits Limits { get; }
    public long RetainedBytes { get; private set; }
    private long externalReservation;
    public void ReserveExternalCpuBytes(long bytes)
    {
        Alive();if(entries.Count!=0 || externalReservation!=0 || bytes<0 || bytes>Limits.CpuBytes)throw new ArgumentOutOfRangeException(nameof(bytes));
        externalReservation=bytes;RetainedBytes=bytes;
    }
    public int Count => entries.Count;
    public int Bakes { get; private set; }
    private long stamp;
    private bool disposed;
    public BakedMeshCache(BakedMeshLimits limits) => Limits=limits ?? throw new ArgumentNullException(nameof(limits));
    /// <summary>Only after previous borrowed frame consumption; caller clears its operation references first.</summary>
    public void BeginFrame() { Alive(); foreach(var e in entries) e.Pinned=false; }
    public BakedShapeMesh? Find(long key)
    {
        Alive(); foreach(var e in entries) if(e.Key==key) {e.Pinned=true;e.Stamp=++stamp;return e.Mesh;} return null;
    }
    public BakedShapeMesh? Get(long key,long reservation,Func<BakedShapeMesh> bake)
    {
        Alive(); ArgumentNullException.ThrowIfNull(bake);
        if(reservation<1) throw new ArgumentOutOfRangeException(nameof(reservation));
        var existing=Find(key);if(existing is not null)return existing;
        if(reservation>Limits.CpuBytes) return null;
        while(entries.Count>=Limits.MeshCount || RetainedBytes+reservation>Limits.CpuBytes)
        {
            Entry? victim=null;
            foreach(var e in entries) if(!e.Pinned && (victim is null || e.Stamp<victim.Stamp)) victim=e;
            if(victim is null) return null;
            entries.Remove(victim); RetainedBytes-=victim.Mesh.CpuCharge;
        }
        // Reservation includes staging: no other owner is retained while this factory runs synchronously.
        var mesh=bake();
        if(mesh.CpuCharge>reservation) throw new InvalidOperationException("Mesh exceeded its CPU reservation.");
        entries.Add(new(key,mesh){Pinned=true,Stamp=++stamp}); RetainedBytes+=mesh.CpuCharge; Bakes++;
        return mesh;
    }
    public void Invalidate(long key)
    {
        Alive();for(int i=0;i<entries.Count;i++)if(entries[i].Key==key)
        {if(entries[i].Pinned)throw new InvalidOperationException("Cannot invalidate a frame-pinned mesh.");RetainedBytes-=entries[i].Mesh.CpuCharge;entries.RemoveAt(i);return;}
    }
    public void Clear() { Alive(); entries.Clear(); RetainedBytes=externalReservation; }
    private void Alive()=>ObjectDisposedException.ThrowIf(disposed,this);
    public void Dispose() { if(disposed)return; Clear();externalReservation=RetainedBytes=0;disposed=true; }
}
