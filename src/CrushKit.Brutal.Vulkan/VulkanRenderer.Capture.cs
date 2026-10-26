using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using System.Runtime.CompilerServices;

[assembly:InternalsVisibleTo("BrutalBreakout.Performance")]
[assembly:InternalsVisibleTo("Vulkan.Tests")]

namespace CrushKit.Brutal.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    private bool _captureEnabled;
    private BufferEx _captureBuffer;
    private VkFormat _captureFormat;
    private VkImageUsageFlags _captureSupportedUsage;
    private int _captureBytes;
    internal byte[]? CapturedRgba { get; private set; }
    internal object CaptureFacts => new { width=_extent.Width,height=_extent.Height,format=_captureFormat.ToString(),
        samples=Samples.ToString(),supportedUsage=_captureSupportedUsage.ToString(),resolveImages=_images.Length,multisampleImages=_multisampleImages.Length,capture=_captureEnabled,
        meshIds=_bakedMeshes.Keys.ToArray(),meshBytes=_bakedBytes,meshUploads=BakedMeshUploads,
        instanceCapacity=_bakedInstances.IsNull()?0:checked((int)_bakedInstances.BufferSize),
        vertexCapacity=_vertices.IsNull()?0:checked((int)_vertices.BufferSize),indexCapacity=_indices.IsNull()?0:checked((int)_indices.BufferSize) };
    internal void PrepareCaptureTarget()
    {
        RequireOwner();var size=_window.FramebufferSize;
        if(_recreate || size.X!=_extent.Width || size.Y!=_extent.Height)RecreateSwapchain(size);
    }
    /// <summary>Diagnostic startup-only. No capture allocation/transfer/wait on ordinary rendering.</summary>
    internal void EnableCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed,this);
        if(!_swapchain.IsNull())throw new InvalidOperationException("Enable capture before first swapchain.");
        _captureEnabled=true;
    }
    private VkImageUsageFlags CaptureUsage(VkSurfaceCapabilitiesKHR caps)
    {
        _captureSupportedUsage=caps.SupportedUsageFlags;
        var usage=VkImageUsageFlags.ColorAttachmentBit;
        if(_captureEnabled)usage|=VkImageUsageFlags.TransferSrcBit;
        if(!caps.SupportedUsageFlags.HasFlag(usage))throw new NotSupportedException("Matching-target swapchain transfer-source capture is unsupported.");
        return usage;
    }
    private void PrepareCapture()
    {
        if(!_captureEnabled)return;
        _captureBytes=checked(_extent.Width*_extent.Height*4);
        if(_captureBuffer.IsNull() || _captureBuffer.BufferSize<ByteSize.Of(_captureBytes))
        {
            var replacement=_allocator!.CreateBuffer(new BufferEx.CreateInfo
            {
                Name="",BufferSize=ByteSize.Of(_captureBytes),BufferUsage=VkBufferUsageFlags.TransferDstBit,
                BufferSharingMode=VkSharingMode.Exclusive,AllocPreference=MemoryPreference.PreferCpu,
                AllocRequiredProperties=VkMemoryPropertyFlags.HostVisibleBit,
            });
            var previous=_captureBuffer;_captureBuffer=replacement;if(!previous.IsNull())previous.Dispose();
        }
    }
    private void RecordCapture(int imageIndex)
    {
        if(!_captureEnabled)return;
        ref var api=ref _device!.PInvoke;
        var image=new VkImageMemoryBarrier
        {
            Image=_images[imageIndex],OldLayout=VkImageLayout.PresentSrcKHR,NewLayout=VkImageLayout.TransferSrcOptimal,
            SrcAccessMask=VkAccessFlags.ColorAttachmentWriteBit,DstAccessMask=VkAccessFlags.TransferReadBit,
            SrcQueueFamilyIndex=-1,DstQueueFamilyIndex=-1,
            SubresourceRange=new VkImageSubresourceRange{AspectMask=VkImageAspectFlags.ColorBit,LevelCount=1,LayerCount=1},
        };
        api.vkCmdPipelineBarrier.Invoke(_command,VkPipelineStageFlags.ColorAttachmentOutputBit,VkPipelineStageFlags.TransferBit,0,0,null,0,null,1,&image);
        var region=new VkBufferImageCopy
        {
            ImageSubresource=new VkImageSubresourceLayers{AspectMask=VkImageAspectFlags.ColorBit,LayerCount=1},
            ImageExtent=new VkExtent3D{Width=_extent.Width,Height=_extent.Height,Depth=1},
        };
        api.vkCmdCopyImageToBuffer.Invoke(_command,_images[imageIndex],VkImageLayout.TransferSrcOptimal,_captureBuffer.VkBuffer,1,&region);
        var host=new VkBufferMemoryBarrier
        {
            Buffer=_captureBuffer.VkBuffer,SrcAccessMask=VkAccessFlags.TransferWriteBit,DstAccessMask=VkAccessFlags.HostReadBit,
            SrcQueueFamilyIndex=-1,DstQueueFamilyIndex=-1,Offset=(ByteSize64)0,Size=(ByteSize64)_captureBytes,
        };
        api.vkCmdPipelineBarrier.Invoke(_command,VkPipelineStageFlags.TransferBit,VkPipelineStageFlags.HostBit,0,0,null,1,&host,0,null);
        image.OldLayout=VkImageLayout.TransferSrcOptimal;image.NewLayout=VkImageLayout.PresentSrcKHR;
        image.SrcAccessMask=VkAccessFlags.TransferReadBit;image.DstAccessMask=0;
        api.vkCmdPipelineBarrier.Invoke(_command,VkPipelineStageFlags.TransferBit,VkPipelineStageFlags.BottomOfPipeBit,0,0,null,0,null,1,&image);
    }
    private void CompleteCapture()
    {
        if(!_captureEnabled)return;
        Check(_device!.WaitForFences([_frameFence],true,-1),"Capture fence");
        using var mapping=_captureBuffer.Map();
        Check(_allocator!.InvalidateAllocation(new VmaAllocation(_captureBuffer.BufferAllocationInfo.AllocationInfo.AllocHandle),ByteSize.Zero,ByteSize.Of(_captureBytes)),"Invalidate readback");
        var pixels=mapping.AsSpan<byte>()[.._captureBytes].ToArray();
        if(_captureFormat==VkFormat.B8G8R8A8SRGB)
            for(int i=0;i<pixels.Length;i+=4)(pixels[i],pixels[i+2])=(pixels[i+2],pixels[i]);
        CapturedRgba=pixels;
    }
}
