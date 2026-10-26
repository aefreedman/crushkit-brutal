using Brutal;
using Brutal.Collections;
using Brutal.Numerics;
using Brutal.Render.Mesh;

namespace CrushKit.Brutal.Draw2D;

/// <summary>Owns reusable native storage; ProcGenMesh itself only borrows the lists.</summary>
internal sealed class DiskMeshAdapter : IDisposable
{
    private readonly NativeStrideList positions = new(ByteSize.Of<float3>());
    private readonly NativeStrideList indices = new(ByteSize.Of<uint>());

    private int? cachedResolution;
    private bool disposed;

    public ReadOnlySpan<float3> Positions { get { Alive(); return positions.ToSpan<float3>(); } }
    public ReadOnlySpan<uint> Indices { get { Alive(); return indices.ToSpan<uint>(); } }

    public void Generate(int resolution)
    {
        Alive();
        if (cachedResolution == resolution) return;
        // Invalidate before touching borrowed storage, including when generation fails.
        cachedResolution = null;
        positions.Clear();
        indices.Clear();
        var mesh = new ProcGenMesh(positions, indices);
        ProcGenMeshLibrary.GenerateDisk(in mesh, new float3(0, 0, 0), new float3(0, 0, 1), 1, resolution, false);
        // Published 2026.10.2 emits (rim0, rim1, center) triangles. Float sin(Tau) is
        // not zero: weld only the closing endpoint to prevent a tiny overlapping seam.
        var span = positions.ToSpan<float3>();
        span[^2] = span[0];
        cachedResolution = resolution;
    }

    private void Alive() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cachedResolution = null;
        positions.Dispose();
        indices.Dispose();
    }
}
