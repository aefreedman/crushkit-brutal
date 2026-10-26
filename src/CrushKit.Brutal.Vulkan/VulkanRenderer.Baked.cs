using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using CrushKit.Brutal.Draw2D;
using System.Runtime.InteropServices;

namespace CrushKit.Brutal.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    private sealed class ResidentMesh
    {
        internal BufferEx Vertices,Indices;
        internal int IndexCount;
        internal long Bytes;
        internal void Release()
        {
            List<Action> releases=[];
            if(!Vertices.IsNull())releases.Add(Vertices.Dispose);
            if(!Indices.IsNull())releases.Add(Indices.Dispose);
            ResourceLifetime.ReleaseAll(releases.ToArray());
        }
    }
    // Keys only: GPU eviction never prolongs CPU array/settings/revision ownership.
    private readonly Dictionary<long,ResidentMesh> _bakedMeshes=[];
    private readonly HashSet<long> _bakedActive=[];
    private readonly List<long> _bakedEvictions=[];
    private BufferEx _bakedInstances;
    private VkShaderModule _bakedShader;
    private VkPipeline _bakedPipeline;
    private long _bakedBytes;
    public int BakedMeshUploads { get; private set; }
    public int ResidentBakedMeshCount=>_bakedMeshes.Count;
    public long ResidentBakedMeshBytes=>_bakedBytes;

    /// <summary>Explicit alternate path. Does not change the ordinary triangle Draw default.</summary>
    public bool Draw(OrderedShapeFrame frame,LinearColor background)
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        ValidateFrame(frame.Triangles);ValidateBaked(frame);
        return DrawValidated(frame.Triangles,background,frame);
    }
    private static readonly bool BakedAbiChecked=CheckBakedAbi();
    private static bool CheckBakedAbi()
    {
        if(Marshal.SizeOf<MeshInstance>()!=40 || Marshal.OffsetOf<MeshInstance>(nameof(MeshInstance.Origin))!=0 ||
            Marshal.OffsetOf<MeshInstance>(nameof(MeshInstance.AxisX))!=8 || Marshal.OffsetOf<MeshInstance>(nameof(MeshInstance.AxisY))!=16 ||
            Marshal.OffsetOf<MeshInstance>(nameof(MeshInstance.Color))!=24)throw new InvalidOperationException("Mesh instance ABI mismatch.");
        return true;
    }
    internal static void ValidateBaked(OrderedShapeFrame frame)
    {
        if(frame.Limits is null || frame.Instances.Length>frame.Limits.Instances || frame.Operations.Length>frame.Limits.Operations)
            throw new ArgumentException("Invalid ordered frame limits.");
        _=BakedAbiChecked;
        int triangle=0,instance=0,command=0;
        var commands=frame.Triangles.Commands.Span;
        foreach(var op in frame.Operations.Span)
        {
            var clip=op.Clip;
            if(op.Count<=0 || clip.Left<0 || clip.Top<0 || clip.Right<clip.Left || clip.Bottom<clip.Top || clip.Right>frame.Triangles.Width || clip.Bottom>frame.Triangles.Height)
                throw new ArgumentException("Invalid ordered scissor/range.");
            if(op.Mesh is null)
            {
                if(op.First!=triangle || op.Count%3!=0 || (long)op.First+op.Count>frame.Triangles.Indices.Length)
                    throw new ArgumentException("Invalid ordered triangle coverage.");
                int last=op.First+op.Count;
                while(triangle<last)
                {
                    while(command<commands.Length && commands[command].FirstIndex+commands[command].IndexCount<=triangle)command++;
                    if(command>=commands.Length || commands[command].FirstIndex>triangle || commands[command].Clip!=clip)
                        throw new ArgumentException("Ordered triangle clip/coverage changed.");
                    triangle=Math.Min(last,commands[command].FirstIndex+commands[command].IndexCount);
                }
            }
            else
            {
                if(op.First!=instance || (long)instance+op.Count>frame.Instances.Length)throw new ArgumentException("Invalid ordered instance coverage.");
                double b=op.Mesh.MaximumAbsoluteCoordinate;
                foreach(var value in frame.Instances.Span.Slice(instance,op.Count))
                {
                    var c=value.Color;
                    if(!Finite(value.Origin) || !Finite(value.AxisX) || !Finite(value.AxisY) ||
                        value.AxisX.Y!=0 || value.AxisY.X!=0 || !float.IsNormal(value.AxisX.X) || value.AxisX.X<=0 || value.AxisX.X!=value.AxisY.Y ||
                        Math.Abs((double)value.Origin.X)+b*(Math.Abs((double)value.AxisX.X)+Math.Abs((double)value.AxisY.X))>1_048_576 ||
                        Math.Abs((double)value.Origin.Y)+b*(Math.Abs((double)value.AxisX.Y)+Math.Abs((double)value.AxisY.Y))>1_048_576 ||
                        !float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z) || !float.IsFinite(c.W) ||
                        c.W<0 || c.W>1 || c.X<0 || c.Y<0 || c.Z<0 || c.X>c.W || c.Y>c.W || c.Z>c.W)
                        throw new ArgumentException("Invalid bounded premultiplied mesh instance.");
                }
                instance+=op.Count;
            }
        }
        if(triangle!=frame.Triangles.Indices.Length || instance!=frame.Instances.Length)throw new ArgumentException("Incomplete ordered coverage.");
    }
    private static bool Finite(float2 p)=>float.IsFinite(p.X) && float.IsFinite(p.Y);
    private static VertexInput BakedVertexInput()=>new VertexInput(2,5)
        .AddBinding(0,ByteSize.Of(8),VkVertexInputRate.Vertex).AddBinding(1,ByteSize.Of(40),VkVertexInputRate.Instance)
        .AddAttribute(0,0,VkFormat.R32G32SFloat,ByteSize.Zero).AddAttribute(1,1,VkFormat.R32G32SFloat,ByteSize.Zero)
        .AddAttribute(2,1,VkFormat.R32G32SFloat,ByteSize.Of(8)).AddAttribute(3,1,VkFormat.R32G32SFloat,ByteSize.Of(16))
        .AddAttribute(4,1,VkFormat.R32G32B32A32SFloat,ByteSize.Of(24)).Check();
    private void UploadBaked(OrderedShapeFrame frame)
    {
        _bakedActive.Clear();long required=0;
        foreach(var op in frame.Operations.Span)if(op.Mesh is { } mesh && _bakedActive.Add(mesh.Identity))required=checked(required+mesh.GpuBytes);
        if(required>frame.Limits.GpuBytes || _bakedActive.Count>frame.Limits.MeshCount)throw new ArgumentException("Pinned meshes exceed GPU limits.");
        // Fence has completed. Retain unused geometry until capacity is actually needed.
        long missingBytes=0;int missingCount=0;
        foreach(var op in frame.Operations.Span)
            if(op.Mesh is { } m && !_bakedMeshes.ContainsKey(m.Identity) && _bakedActive.Remove(m.Identity))
            {missingBytes+=m.GpuBytes;missingCount++;}
        _bakedActive.Clear();foreach(var op in frame.Operations.Span)if(op.Mesh is { } m)_bakedActive.Add(m.Identity);
        _bakedEvictions.Clear();long remainingBytes=_bakedBytes;int remainingCount=_bakedMeshes.Count;
        foreach(var pair in _bakedMeshes)
        {
            if(remainingBytes+missingBytes<=frame.Limits.GpuBytes && remainingCount+missingCount<=frame.Limits.MeshCount)break;
            if(_bakedActive.Contains(pair.Key))continue;
            _bakedEvictions.Add(pair.Key);remainingBytes-=pair.Value.Bytes;remainingCount--;
        }
        foreach(long key in _bakedEvictions)
        {var old=_bakedMeshes[key];_bakedMeshes.Remove(key);_bakedBytes-=old.Bytes;old.Release();}
        if(frame.Instances.IsEmpty)return;
        if(_bakedShader.IsNull())
        {
            using var compiler=ShaderC.CreateCompiler();using var options=ShaderC.CreateCompilerOptions();
            _bakedShader=CompileShader(compiler,options,"shapes-instance.vert",ShaderKind.VertexShader);
        }
        if(_bakedPipeline.IsNull())_bakedPipeline=CreateShapePipeline(true);
        UploadBuffer(ref _bakedInstances,frame.Instances.Span,VkBufferUsageFlags.VertexBufferBit);
        foreach(var op in frame.Operations.Span)
        {
            if(op.Mesh is not { } mesh || _bakedMeshes.ContainsKey(mesh.Identity))continue;
            var resident=new ResidentMesh{IndexCount=mesh.IndexCount,Bytes=mesh.GpuBytes};
            try
            {
                UploadBuffer(ref resident.Vertices,mesh.Vertices,VkBufferUsageFlags.VertexBufferBit,true);
                UploadBuffer(ref resident.Indices,mesh.Indices,VkBufferUsageFlags.IndexBufferBit,true);
            }
            catch(Exception error)
            {
                try{resident.Release();}catch(Exception cleanup){throw new AggregateException(error,cleanup);}throw;
            }
            _bakedMeshes.Add(mesh.Identity,resident);_bakedBytes+=resident.Bytes;BakedMeshUploads++;
        }
    }
    private bool RecordBaked(OrderedShapeFrame frame)
    {
        ref var api=ref _device!.PInvoke;
        var viewport=new VkViewport{Width=_extent.Width,Height=_extent.Height,MinDepth=0,MaxDepth=1};
        api.vkCmdSetViewport.Invoke(_command,0,1,&viewport);
        var extent=new float2(_extent.Width,_extent.Height);
        api.vkCmdPushConstants.Invoke(_command,_pipelineLayout,VkShaderStageFlags.VertexBit,0,8,&extent);
        var buffers=stackalloc VkBuffer[2];var offsets=stackalloc ByteSize64[2]{(ByteSize64)0,(ByteSize64)0};
        bool recorded=false;
        foreach(var op in frame.Operations.Span)
        {
            if(op.Clip.IsEmpty)continue;
            var c=op.Clip;var scissor=new VkRect2D{Offset=new VkOffset2D{X=c.Left,Y=c.Top},Extent=new VkExtent2D{Width=c.Right-c.Left,Height=c.Bottom-c.Top}};
            api.vkCmdSetScissor.Invoke(_command,0,1,&scissor);
            if(op.Mesh is { } mesh)
            {
                var resident=_bakedMeshes[mesh.Identity];
                api.vkCmdBindPipeline.Invoke(_command,VkPipelineBindPoint.Graphics,_bakedPipeline);
                buffers[0]=resident.Vertices.VkBuffer;buffers[1]=_bakedInstances.VkBuffer;
                api.vkCmdBindVertexBuffers.Invoke(_command,0,2,buffers,offsets);
                api.vkCmdBindIndexBuffer.Invoke(_command,resident.Indices.VkBuffer,ByteSize.Zero,VkIndexType.UInt32);
                api.vkCmdDrawIndexed.Invoke(_command,resident.IndexCount,op.Count,0,0,op.First);
            }
            else
            {
                api.vkCmdBindPipeline.Invoke(_command,VkPipelineBindPoint.Graphics,_pipeline);buffers[0]=_vertices.VkBuffer;
                api.vkCmdBindVertexBuffers.Invoke(_command,0,1,buffers,offsets);
                api.vkCmdBindIndexBuffer.Invoke(_command,_indices.VkBuffer,ByteSize.Zero,VkIndexType.UInt32);
                api.vkCmdDrawIndexed.Invoke(_command,op.Count,1,op.First,0,0);
            }
            recorded=true;
        }
        return recorded;
    }
    private void DisposeBaked()
    {
        List<Action> releases=[];
        foreach(var mesh in _bakedMeshes.Values)releases.Add(mesh.Release);
        _bakedMeshes.Clear();_bakedActive.Clear();_bakedBytes=0;
        if(!_bakedInstances.IsNull())releases.Add(_bakedInstances.Dispose);
        if(!_bakedShader.IsNull())releases.Add(()=>_device!.DestroyShaderModule(_bakedShader,null));
        if(!_captureBuffer.IsNull())releases.Add(_captureBuffer.Dispose);
        ResourceLifetime.ReleaseAll(releases.ToArray());
    }
}
