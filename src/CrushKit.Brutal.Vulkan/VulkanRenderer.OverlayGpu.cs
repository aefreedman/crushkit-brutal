using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.TextureApi;
using Brutal.TextureApi.Abstractions;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using CrushKit.Brutal.Vulkan;

namespace CrushKit.Brutal.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    private DescriptorSetLayoutEx? _overlayDescriptorLayout;
    private DescriptorPoolEx? _overlayDescriptorPool;
    internal VkSampler _overlaySampler;
    private VkPipelineLayout _overlayPipelineLayout;
    private VkPipeline _overlayPipeline;
    private VkShaderModule _overlayVertexShader, _overlayFragmentShader;
    private OverlayQuadBatch? _overlayBatch;
    private BufferEx _overlayVertices, _overlayIndices;
    private readonly List<(int FirstIndex, VkDescriptorSet Set)> _overlayDraws = [];

    private void EnsureGameOverlay()
    {
        var s = _overlaySettings!;
        _overlayText ??= new OverlayText(s);
        _overlayBatch ??= new OverlayQuadBatch();
        if (_overlayDescriptorLayout == null)
            _overlayDescriptorLayout = _device!.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
            {
                Bindings = new[] { new VkDescriptorSetLayoutBinding { Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler,
                    DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit } },
            }, null);
        if (_overlayDescriptorPool == null)
            _overlayDescriptorPool = _device!.CreateDescriptorPool(new DescriptorPoolEx.CreateInfo
            {
                MaxSets = s.CacheEntries, Flags = VkDescriptorPoolCreateFlags.FreeDescriptorSetBit,
                PoolSizes = new[] { new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = s.CacheEntries } },
            }, null);
        if (_overlaySampler.IsNull())
            _overlaySampler = _device!.CreateSampler(new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear, MinFilter = VkFilter.Linear, MipmapMode = VkSamplerMipmapMode.Nearest,
                AddressModeU = VkSamplerAddressMode.ClampToEdge, AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge, MaxLod = 0,
            }, null);
        if (_overlayPipelineLayout.IsNull())
            _overlayPipelineLayout = _device!.CreatePipelineLayout([_overlayDescriptorLayout.Handle], [new VkPushConstantRange
            { StageFlags = VkShaderStageFlags.VertexBit, Offset = (ByteSize32)0, Size = (ByteSize32)8 }], null);
        if (_overlayVertexShader.IsNull() || _overlayFragmentShader.IsNull())
        {
            using var compiler = ShaderC.CreateCompiler(); using var options = ShaderC.CreateCompilerOptions();
            if (_overlayVertexShader.IsNull()) _overlayVertexShader = CompileShader(compiler, options, "overlay.vert", ShaderKind.VertexShader);
            if (_overlayFragmentShader.IsNull()) _overlayFragmentShader = CompileShader(compiler, options, "overlay.frag", ShaderKind.FragmentShader);
        }
        if (_overlayPipeline.IsNull()) CreateOverlayPipeline();
    }

    private void CreateOverlayPipeline()
    {
        if (Marshal.SizeOf<OverlayVertex>() != 32 || Marshal.OffsetOf<OverlayVertex>(nameof(OverlayVertex.Uv)) != 8 ||
            Marshal.OffsetOf<OverlayVertex>(nameof(OverlayVertex.Color)) != 16) throw new InvalidOperationException("Overlay UV vertex ABI mismatch.");
        var input = new VertexInput(1, 3).AddBinding(0, ByteSize.Of<OverlayVertex>(), VkVertexInputRate.Vertex)
            .AddAttribute(0, 0, VkFormat.R32G32SFloat, ByteSize.Zero)
            .AddAttribute(1, 0, VkFormat.R32G32SFloat, ByteSize.Of(8))
            .AddAttribute(2, 0, VkFormat.R32G32B32A32SFloat, ByteSize.Of(16)).Check();
        var assembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
        var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var raster = new VkPipelineRasterizationStateCreateInfo { PolygonMode = VkPolygonMode.Fill, CullMode = VkCullModeFlags.None, LineWidth = 1 };
        var samples = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = Samples };
        var blend = new VkPipelineColorBlendAttachmentState
        {
            BlendEnable = true, SrcColorBlendFactor = VkBlendFactor.One, DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = VkBlendOp.Add, SrcAlphaBlendFactor = VkBlendFactor.One, DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = VkBlendOp.Add, ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
        };
        var colors = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
        var dynamics = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamics };
        fixed (byte* main = "main\0"u8)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2]
            {
                new() { Stage = VkShaderStageFlags.VertexBit, Module = _overlayVertexShader, Name = main },
                new() { Stage = VkShaderStageFlags.FragmentBit, Module = _overlayFragmentShader, Name = main },
            };
            _overlayPipeline = _device!.CreateGraphicsPipeline(default, new VkGraphicsPipelineCreateInfo
            {
                StageCount = 2, Stages = stages, VertexInputState = input, InputAssemblyState = &assembly,
                ViewportState = &viewport, RasterizationState = &raster, MultisampleState = &samples,
                ColorBlendState = &colors, DynamicState = &dynamicState, Layout = _overlayPipelineLayout, RenderPass = _renderPass, BasePipelineIndex = -1,
            }, null);
        }
    }

    private void RecordOverlay()
    {
        ref var api = ref _device!.PInvoke;
        if (_overlayDraws.Count > 0)
        {
            api.vkCmdBindPipeline.Invoke(_command, VkPipelineBindPoint.Graphics, _overlayPipeline);
            var viewport = new VkViewport { Width = _extent.Width, Height = _extent.Height, MinDepth = 0, MaxDepth = 1 };
            var scissor = new VkRect2D { Extent = _extent };
            api.vkCmdSetViewport.Invoke(_command, 0, 1, &viewport); api.vkCmdSetScissor.Invoke(_command, 0, 1, &scissor);
            var extent = new float2(_extent.Width, _extent.Height);
            api.vkCmdPushConstants.Invoke(_command, _overlayPipelineLayout, VkShaderStageFlags.VertexBit, 0, 8, &extent);
            var vertices = _overlayVertices.VkBuffer; var offset = (ByteSize64)0;
            api.vkCmdBindVertexBuffers.Invoke(_command, 0, 1, &vertices, &offset);
            api.vkCmdBindIndexBuffer.Invoke(_command, _overlayIndices.VkBuffer, ByteSize.Zero, VkIndexType.UInt32);
            foreach (var draw in _overlayDraws)
            {
                var set = draw.Set;
                api.vkCmdBindDescriptorSets.Invoke(_command, VkPipelineBindPoint.Graphics, _overlayPipelineLayout, 0, 1, &set, 0, null);
                api.vkCmdDrawIndexed.Invoke(_command, 6, 1, draw.FirstIndex, 0, 0);
                _recordedLabels++; _recordedLabelIndices += 6;
            }
        }
        if (_recordedUiIndices > 0) _inspector!.Record(this);
    }

    internal sealed record OverlayGpuTexture(ImageEx Image, ImageViewEx View, VkDescriptorSet Set, int Width, int Height, bool HasCoverage);
    private void ReleaseOverlayTexture(string key)
    {
        var texture = _overlayTextures[key]; _overlayTextures.Remove(key); ReleaseGpuTexture(texture);
    }
    internal void ReleaseGpuTexture(OverlayGpuTexture texture) => ResourceLifetime.ReleaseAll(
        () => { if (!texture.Set.IsNull()) _device!.FreeDescriptorSet(_overlayDescriptorPool!, texture.Set); }, texture.View.Dispose, texture.Image.Dispose);

    internal OverlayGpuTexture UploadOverlayTexture(ITexture pixels, bool gameDescriptor)
    {
        OverlayPixels.ValidateRgba(pixels);
        ImageEx image = default; ImageViewEx view = default; BufferEx staging = default; VkDescriptorSet set = default;
        try
        {
            var plan = UploadRegions.Build(pixels);
            var copies = OverlayTextureUpload.BuildCopies(pixels, plan);
            image = _allocator!.CreateImage(new ImageEx.CreateInfo
            {
                Name = "", ImageType = VkImageType._2D, ImageFormat = VkFormat.R8G8B8A8UNorm,
                ImageExtent = new VkExtent3D { Width = pixels.Extent.X, Height = pixels.Extent.Y, Depth = pixels.Extent.Z },
                ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = VkSampleCountFlags._1Bit,
                ImageTiling = VkImageTiling.Optimal, ImageUsage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit,
                ImageSharingMode = VkSharingMode.Exclusive, AllocPreference = MemoryPreference.PreferGpu,
            });
            staging = _allocator!.CreateBuffer(new BufferEx.CreateInfo
            {
                Name = "", BufferSize = plan.RequiredBufferSizeBytes, BufferUsage = VkBufferUsageFlags.TransferSrcBit,
                BufferSharingMode = VkSharingMode.Exclusive, AllocPreference = MemoryPreference.PreferCpu, AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit,
            });
            using (var mapping = staging.Map())
            {
                OverlayTextureUpload.CopyStaging(pixels, plan, mapping.AsSpan<byte>());
                Check(_allocator!.FlushAllocation(new VmaAllocation(staging.BufferAllocationInfo.AllocationInfo.AllocHandle), ByteSize.Zero, plan.RequiredBufferSizeBytes), "Flush overlay staging");
            }
            var range = image.GetFullRange(VkImageAspectFlags.ColorBit);
            var barrier = new VkImageMemoryBarrier
            {
                OldLayout = VkImageLayout.Undefined, NewLayout = VkImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                Image = image.VkImage, SubresourceRange = range, DstAccessMask = VkAccessFlags.TransferWriteBit,
            };
            ref var api = ref _device!.PInvoke;
            api.vkCmdPipelineBarrier.Invoke(_command, VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);
            fixed (VkBufferImageCopy* copy = copies)
                api.vkCmdCopyBufferToImage.Invoke(_command, staging.VkBuffer, image.VkImage, VkImageLayout.TransferDstOptimal, copies.Length, copy);
            barrier.OldLayout = VkImageLayout.TransferDstOptimal; barrier.NewLayout = VkImageLayout.ShaderReadOnlyOptimal;
            barrier.SrcAccessMask = VkAccessFlags.TransferWriteBit; barrier.DstAccessMask = VkAccessFlags.ShaderReadBit;
            api.vkCmdPipelineBarrier.Invoke(_command, VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &barrier);
            view = image.CreateImageView(VkImageViewType._2D, range, null);
            if (gameDescriptor)
            {
                set = _device.AllocateDescriptorSet(_overlayDescriptorPool!, _overlayDescriptorLayout!);
                var info = new VkDescriptorImageInfo { Sampler = _overlaySampler, ImageView = view.VkImageView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                var write = new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &info };
                api.vkUpdateDescriptorSets.Invoke(_device.Handle, 1, &write, 0, null);
            }
            var result = new OverlayGpuTexture(image, view, set, pixels.Extent.X, pixels.Extent.Y, OverlayPixels.HasCoverage(pixels));
            _overlayStaging.Add(staging); // Retain until the submission fence, not just the recording call.
            return result;
        }
        catch
        {
            ResourceLifetime.ReleaseAll(
                () => { if (!set.IsNull()) _device!.FreeDescriptorSet(_overlayDescriptorPool!, set); },
                () => { if (!view.IsNull()) view.Dispose(); }, () => { if (!image.IsNull()) image.Dispose(); }, () => { if (!staging.IsNull()) staging.Dispose(); });
            throw;
        }
    }

    private void DisposeOverlayGpu()
    {
        List<Action> releases = [];
        if (!_overlayVertices.IsNull()) { var b = _overlayVertices; _overlayVertices = default; releases.Add(b.Dispose); }
        if (!_overlayIndices.IsNull()) { var b = _overlayIndices; _overlayIndices = default; releases.Add(b.Dispose); }
        if (!_overlayPipelineLayout.IsNull()) { var layout = _overlayPipelineLayout; _overlayPipelineLayout = default; releases.Add(() => _device!.DestroyPipelineLayout(layout, null)); }
        if (!_overlayVertexShader.IsNull()) { var shader = _overlayVertexShader; _overlayVertexShader = default; releases.Add(() => _device!.DestroyShaderModule(shader, null)); }
        if (!_overlayFragmentShader.IsNull()) { var shader = _overlayFragmentShader; _overlayFragmentShader = default; releases.Add(() => _device!.DestroyShaderModule(shader, null)); }
        if (!_overlaySampler.IsNull()) { var sampler = _overlaySampler; _overlaySampler = default; releases.Add(() => _device!.DestroySampler(sampler, null)); }
        if (_overlayDescriptorPool != null) { var pool = _overlayDescriptorPool; _overlayDescriptorPool = null; releases.Add(pool.Dispose); }
        if (_overlayDescriptorLayout != null) { var layout = _overlayDescriptorLayout; _overlayDescriptorLayout = null; releases.Add(layout.Dispose); }
        if (_overlayBatch != null) { var batch = _overlayBatch; _overlayBatch = null; releases.Add(batch.Dispose); }
        ResourceLifetime.ReleaseAll(releases.ToArray());
    }
}
