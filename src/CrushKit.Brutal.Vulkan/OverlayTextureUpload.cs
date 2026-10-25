using Brutal.TextureApi;
using Brutal.TextureApi.Abstractions;
using Brutal.VulkanApi;
using CrushKit.Brutal.Vulkan;

namespace CrushKit.Brutal.Vulkan;

/// <summary>Narrow BRUTAL upload-plan → Vulkan bridge for the overlay's single RGBA image, not another layout planner.</summary>
public static class OverlayTextureUpload
{
    public static void CopyStaging(ITexture texture, StageUploadRegions plan, Span<byte> destination)
    {
        OverlayPixels.ValidateRgba(texture);
        if (destination.Length < checked((int)plan.RequiredBufferSizeBytes)) throw new ArgumentException("Staging buffer too small.");
        foreach (var region in plan.StageBufferRegions)
            texture.Data.Slice(checked((int)region.SourceStartOffset), checked((int)region.SizeInBytes))
                .CopyTo(destination.Slice(checked((int)region.DestinationOffset), checked((int)region.SizeInBytes)));
    }

    public static VkBufferImageCopy[] BuildCopies(ITexture texture, StageUploadRegions plan)
    {
        OverlayPixels.ValidateRgba(texture);
        var copies = new List<VkBufferImageCopy>();
        foreach (var region in plan.CopyToImageRegions)
        {
            if (region.BaseLevel != 0 || region.BaseLayer != 0 || region.LayerCount != 1) throw new InvalidDataException("Unsupported overlay upload region.");
            var pitch = checked((int)texture.RowPitch(region.BaseLevel));
            if (pitch % 4 != 0) throw new InvalidDataException("RGBA pitch must be a whole number of texels.");
            copies.Add(new VkBufferImageCopy
            {
                BufferOffset = region.BufferOffset, BufferRowLength = pitch / 4,
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit,
                    MipLevel = region.BaseLevel, BaseArrayLayer = region.BaseLayer, LayerCount = region.LayerCount },
                ImageExtent = new VkExtent3D { Width = texture.Extent.X, Height = texture.Extent.Y, Depth = texture.Extent.Z },
            });
        }
        if (copies.Count == 0) throw new InvalidDataException("Empty overlay upload plan.");
        return copies.ToArray();
    }
}
