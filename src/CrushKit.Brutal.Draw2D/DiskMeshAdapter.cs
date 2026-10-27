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

    private float3[] rim = [];
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
        if (resolution % 4 == 0)
        {
            // Published indexed triangles duplicate rimA/rimB/center. Read the rim via
            // indices, never assume consecutive unique positions. Scratch is owner-private.
            if (rim.Length < resolution + 1) Array.Resize(ref rim, resolution + 1);
            var ids = indices.ToSpan<uint>();
            for (int j = 0; j < resolution; j++) rim[j] = span[(int)ids[3*j]];
            int q = resolution / 4;
            for (int j = 0; j <= q / 2; j++)
            {
                var p = j == 0 ? new float3(-1, 0, 0) : rim[j];
                if (2 * j == q) p.Y = -p.X;
                rim[j] = p;
                rim[q - j] = new(-p.Y, -p.X, 0);
            }
            for (int j = 0; j < q; j++)
            {
                var p = rim[j];
                rim[q + j] = new(p.Y, -p.X, 0);
                rim[2 * q + j] = new(-p.X, -p.Y, 0);
                rim[3 * q + j] = new(-p.Y, p.X, 0);
            }
            // Canonical positive zeros at all cardinals (including the closing weld).
            rim[0] = new(-1, 0, 0); rim[q] = new(0, 1, 0);
            rim[2*q] = new(1, 0, 0); rim[3*q] = new(0, -1, 0);
            rim[resolution] = rim[0];
            for (int j = 0; j < resolution; j++)
            {
                span[(int)ids[3*j]] = rim[j];
                span[(int)ids[3*j+1]] = rim[j+1];
            }
        }
        span[^2] = span[0];
        cachedResolution = resolution;
    }

    private void Alive() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        cachedResolution = null;
        rim = [];
        positions.Dispose();
        indices.Dispose();
    }
}
