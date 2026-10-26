using Brutal.Numerics;
using System.Runtime.InteropServices;

namespace CrushKit.Brutal.Draw2D;

/// <summary>GPU instance ABI: origin@0, axes@8/16, premultiplied linear color@24; 40 bytes.</summary>
[StructLayout(LayoutKind.Sequential,Pack=4)]
public readonly struct MeshInstance(float2 origin,float2 axisX,float2 axisY,float4 color)
{
    public readonly float2 Origin=origin, AxisX=axisX, AxisY=axisY;
    public readonly float4 Color=color;
}
public readonly record struct MeshOperation(BakedShapeMesh? Mesh,int First,int Count,PixelRect Clip);

/// <summary>Borrowed until the ordered producer's next BeginFrame/disposal, and triangle producer reuse.</summary>
public readonly struct OrderedShapeFrame
{
    public ShapeFrame Triangles { get; }
    public ReadOnlyMemory<MeshInstance> Instances { get; }
    public ReadOnlyMemory<MeshOperation> Operations { get; }
    public BakedMeshLimits Limits { get; }
    internal OrderedShapeFrame(ShapeFrame triangles,ReadOnlyMemory<MeshInstance> instances,ReadOnlyMemory<MeshOperation> operations,BakedMeshLimits limits)
    { Triangles=triangles;Instances=instances;Operations=operations;Limits=limits; }
}

/// <summary>Ordered mesh insertion into an ordinary dynamic triangle list; no sorting or retained snapshots.</summary>
public sealed class OrderedShapeDrawList : IDisposable
{
    private readonly MeshInstance[] instances;
    private readonly MeshOperation[] operations;
    private readonly BakedMeshCache cache;
    private int instanceCount,operationCount,triangleCursor,commandCursor;
    private bool disposed,active;
    public BakedMeshLimits Limits=>cache.Limits;
    public int MeshBakes=>cache.Bakes;
    public int CpuMeshCount=>cache.Count;
    public long CpuMeshBytes=>cache.RetainedBytes;
    public int Fallbacks { get; private set; }
    public OrderedShapeDrawList(BakedMeshLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cache=new(limits); instances=new MeshInstance[limits.Instances]; operations=new MeshOperation[limits.Operations];
    }
    public void BeginFrame()
    {
        Alive(); Array.Clear(operations,0,operationCount); // Release all borrowed CPU owners before unpinning.
        instanceCount=operationCount=triangleCursor=commandCursor=Fallbacks=0; cache.BeginFrame();active=true;
    }
    /// <summary>Only at an owner boundary after previous frame consumption; no old revision references retained.</summary>
    public void ClearMeshes() { Alive(); if(operationCount!=0) throw new InvalidOperationException("BeginFrame before clearing geometry.");cache.Clear(); }
    public void InvalidateMesh(long key) { Alive();cache.Invalidate(key); }
    /// <summary>Charge bounded application geometry selection/revision storage to the same CPU budget before baking.</summary>
    public void ReserveExternalCpuBytes(long bytes) { Alive();cache.ReserveExternalCpuBytes(bytes); }
    public void Disc(ShapeDrawList draw,float2 center,float radius,LinearColor color)
    {
        State();
        if(draw.TryBakedDisc(cache,center,radius,color,out var mesh,out var instance))
        { if(mesh is not null) Append(draw,mesh,instance); }
        else { Fallbacks++;draw.FillDisc(center,radius,color); }
    }
    /// <summary>The sourceEndpointError bounds game-specific source construction before the draw transform.</summary>
    public bool TryRectangles(ShapeDrawList draw,long key,ReadOnlySpan<MeshRectangle> rectangles,float2 position,
        double sourceEndpointError,LinearColor color)
    {
        State();
        if(!draw.PrepareBaked(position,color)) return true;
        _=BakedShapeMesh.RectanglesCharge(rectangles.Length);
        var mesh=cache.Find(key) ?? cache.GetRectangles(key,rectangles);
        if(mesh is null || !draw.TryBakedInstance(mesh,position,1,sourceEndpointError,color,out var instance)) {Fallbacks++;return false;}
        Append(draw,mesh,instance);return true;
    }
    private void Append(ShapeDrawList draw,BakedShapeMesh mesh,MeshInstance instance)
    {
        Ensure(draw);draw.CommitBaked(mesh);FlushTriangles(draw);
        int first=instanceCount;instances[instanceCount++]=instance;
        var clip=draw.BakedClip;
        if(operationCount>0 && operations[operationCount-1] is var previous && ReferenceEquals(previous.Mesh,mesh) && previous.Clip==clip && previous.First+previous.Count==first)
            operations[operationCount-1]=previous with {Count=previous.Count+1};
        else operations[operationCount++]=new(mesh,first,1,clip);
    }
    private void Ensure(ShapeDrawList draw)
    {
        int pending=0,end=draw.IndexCount;var commands=draw.BakedCommands;
        for(int i=commandCursor;i<commands.Length;i++)
        {var c=commands[i];if(c.FirstIndex>=end)break;if(c.FirstIndex+c.IndexCount>triangleCursor)pending++;}
        if(instanceCount>=instances.Length || (long)operationCount+pending+1>operations.Length)throw new InvalidOperationException("Ordered frame capacity exceeded.");
    }
    private void FlushTriangles(ShapeDrawList draw)
    {
        int end=draw.IndexCount;var commands=draw.BakedCommands;
        while(triangleCursor<end)
        {
            while(commandCursor<commands.Length && commands[commandCursor].FirstIndex+commands[commandCursor].IndexCount<=triangleCursor)commandCursor++;
            if(commandCursor>=commands.Length)throw new InvalidOperationException("Triangle checkpoint not covered.");
            var c=commands[commandCursor];int last=Math.Min(end,c.FirstIndex+c.IndexCount);
            if(operationCount==operations.Length)throw new InvalidOperationException("Ordered operation capacity exceeded.");
            operations[operationCount++]=new(null,triangleCursor,last-triangleCursor,c.Clip);triangleCursor=last;
        }
        // The last command may subsequently merge across this insertion checkpoint.
        if(commandCursor==commands.Length && commandCursor>0)commandCursor--;
    }
    public OrderedShapeFrame Finish(ShapeDrawList draw)
    {
        State();FlushTriangles(draw);var triangles=draw.Finish();active=false;
        return new(triangles,instances.AsMemory(0,instanceCount),operations.AsMemory(0,operationCount),Limits);
    }
    private void Alive()=>ObjectDisposedException.ThrowIf(disposed,this);
    private void State(){Alive();if(!active)throw new InvalidOperationException("BeginFrame required for ordered drawing.");}
    public void Dispose(){if(disposed)return;Array.Clear(operations);cache.Dispose();disposed=true;}
}
