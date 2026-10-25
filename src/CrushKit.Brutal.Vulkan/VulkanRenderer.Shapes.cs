using System.Runtime.InteropServices;
using System.Text;
using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using CrushKit.Brutal.Draw2D;

namespace CrushKit.Brutal.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    private const VkSampleCountFlags Samples = VkSampleCountFlags._4Bit;
    private VmaAllocator? _allocator;
    private BufferEx _vertices, _indices;
    private VkShaderModule _vertexShader, _fragmentShader;
    private VkPipelineLayout _pipelineLayout;
    private VkPipeline _pipeline;
    internal VkRenderPass _renderPass;
    private VkImageView[] _views = [], _multisampleViews = [];
    private ImageEx[] _multisampleImages = [];
    private VkFramebuffer[] _framebuffers = [];

    private void InitializeShapes()
    {
        if (Marshal.SizeOf<ShapeVertex>() != 24 || Marshal.OffsetOf<ShapeVertex>(nameof(ShapeVertex.Color)) != 8)
            throw new InvalidOperationException("ShapeVertex GPU ABI must be position@0, color@8, stride 24.");
        _allocator = _device!.CreateVmaAllocator(new VmaAllocatorCreateInfo
        {
            PhysicalDevice = _physicalDevice.Handle, Device = _device!.Handle,
            VulkanApiVersion = Vulkan13,
        });
        // Compile once, keeping modules across swapchain recreation.
        try
        {
            using var compiler = ShaderC.CreateCompiler();
            using var options = ShaderC.CreateCompilerOptions();
            _vertexShader = CompileShader(compiler, options, "shapes.vert", ShaderKind.VertexShader);
            _fragmentShader = CompileShader(compiler, options, "shapes.frag", ShaderKind.FragmentShader);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("Shape shader startup failed. Check embedded GLSL diagnostics and the ShaderC native runtime installation.", error);
        }
        _pipelineLayout = _device.CreatePipelineLayout([], [new VkPushConstantRange
        {
            StageFlags = VkShaderStageFlags.VertexBit, Offset = (ByteSize32)0, Size = (ByteSize32)8,
        }], null);
    }

    private VkShaderModule CompileShader(Compiler compiler, CompileOptions options, string name, ShaderKind kind)
    {
        try
        {
            using var stream = typeof(VulkanRenderer).Assembly.GetManifestResourceStream($"CrushKit.Brutal.Vulkan.Shaders.{name}")
                ?? throw new InvalidOperationException($"Missing embedded shader {name}.");
            using var reader = new StreamReader(stream);
            using var result = compiler.Compile(Encoding.UTF8.GetBytes(reader.ReadToEnd()), kind, Encoding.UTF8.GetBytes(name + "\0"), "main"u8, options);
            result.ThrowIfFailed();
            return ShaderModuleExtensions.FromSpirv(_device!, new ReadOnlySpan<byte>(result.GetBytes(), checked((int)result.GetLength())), null);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"Startup shader compilation failed for {name}. Check GLSL diagnostics and ShaderC native runtime availability.", error);
        }
    }

    private static VkSurfaceFormatKHR SelectSurfaceFormat(VkSurfaceFormatKHR[] formats)
    {
        foreach (var desired in new[] { VkFormat.B8G8R8A8SRGB, VkFormat.R8G8B8A8SRGB })
            foreach (var format in formats)
                if (format.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR &&
                    (format.Format == desired || format.Format == VkFormat.Undefined))
                    return new VkSurfaceFormatKHR { Format = desired, ColorSpace = format.ColorSpace };
        throw new NotSupportedException("Shape rendering requires an sRGB swapchain attachment (BGRA8/RGBA8 SRGB, SRGBNonlinear color space).");
    }

    private void RequireMultisampling(VkFormat format)
    {
        var properties = _physicalDevice.GetProperties();
        var features = _physicalDevice.GetFormatProperties(format).OptimalTilingFeatures;
        if (!properties.Limits.FramebufferColorSampleCounts.HasFlag(Samples) ||
            !features.HasFlag(VkFormatFeatureFlags.ColorAttachmentBit | VkFormatFeatureFlags.ColorAttachmentBlendBit))
            throw new NotSupportedException($"{format} must support blendable color attachments and 4x MSAA.");
        VkImageFormatProperties imageProperties = default;
        try
        {
            _physicalDevice.GetImageFormatProperties(format, VkImageType._2D, VkImageTiling.Optimal,
                VkImageUsageFlags.ColorAttachmentBit, 0, ref imageProperties);
        }
        catch (Exception error)
        {
            throw new NotSupportedException($"Cannot query 4x MSAA color image support for {format}.", error);
        }
        if (!imageProperties.SampleCounts.HasFlag(Samples) ||
            imageProperties.MaxExtent.Width < _extent.Width || imageProperties.MaxExtent.Height < _extent.Height)
            throw new NotSupportedException($"4x MSAA color image {format} at {_extent.Width}x{_extent.Height} is unsupported.");
    }

    private void CreateAttachments(VkFormat format)
    {
        var device = _device!;
        _renderPass = CreateShapeRenderPass(format);
        CreateShapePipeline();
        _views = new VkImageView[_images.Length];
        _multisampleViews = new VkImageView[_images.Length];
        _multisampleImages = new ImageEx[_images.Length];
        _framebuffers = new VkFramebuffer[_images.Length];
        var attachments = stackalloc VkImageView[2];
        for (var i = 0; i < _images.Length; i++)
        {
            _views[i] = CreateColorView(_images[i], format);
            _multisampleImages[i] = _allocator!.CreateImage(new ImageEx.CreateInfo
            {
                Name = "", ImageType = VkImageType._2D, ImageFormat = format,
                ImageExtent = new VkExtent3D { Width = _extent.Width, Height = _extent.Height, Depth = 1 },
                ImageMipLevels = 1, ImageArrayLayers = 1, ImageSamples = Samples,
                ImageTiling = VkImageTiling.Optimal, ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = VkSharingMode.Exclusive, AllocPreference = MemoryPreference.PreferGpu,
            });
            _multisampleViews[i] = CreateColorView(_multisampleImages[i].VkImage, format);
            attachments[0] = _multisampleViews[i];
            attachments[1] = _views[i];
            _framebuffers[i] = device.CreateFramebuffer(new VkFramebufferCreateInfo
            {
                RenderPass = _renderPass, AttachmentCount = 2, Attachments = attachments,
                Width = _extent.Width, Height = _extent.Height, Layers = 1,
            }, null);
        }
    }

    private VkImageView CreateColorView(VkImage image, VkFormat format) => _device!.CreateImageView(new VkImageViewCreateInfo
    {
        Image = image, Format = format, ViewType = VkImageViewType._2D,
        SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
    }, null);

    private VkRenderPass CreateShapeRenderPass(VkFormat format)
    {
        // Published CreateDefaultRenderPass hardcodes ShaderReadOnlyOptimal for resolve,
        // with no final-layout parameter. A presentable ColorAttachment-only image needs
        // PresentSrcKHR instead; this is the narrow reason for explicit renderpass setup.
        var attachments = stackalloc VkAttachmentDescription[2]
        {
            new() { Format = format, Samples = Samples, LoadOp = VkAttachmentLoadOp.Clear,
                StoreOp = VkAttachmentStoreOp.DontCare, StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare, InitialLayout = VkImageLayout.Undefined,
                FinalLayout = VkImageLayout.ColorAttachmentOptimal },
            new() { Format = format, Samples = VkSampleCountFlags._1Bit, LoadOp = VkAttachmentLoadOp.DontCare,
                StoreOp = VkAttachmentStoreOp.Store, StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare, InitialLayout = VkImageLayout.Undefined,
                FinalLayout = VkImageLayout.PresentSrcKHR },
        };
        var color = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var resolve = new VkAttachmentReference { Attachment = 1, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription { PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1, ColorAttachments = &color, ResolveAttachments = &resolve };
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL, DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit,
        };
        return _device!.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = 2, Attachments = attachments, SubpassCount = 1, Subpasses = &subpass,
            DependencyCount = 1, Dependencies = &dependency,
        }, null);
    }

    private void CreateShapePipeline()
    {
        var vertexInput = new VertexInput(1, 2).AddBinding(0, ByteSize.Of(24), VkVertexInputRate.Vertex)
            .AddAttribute(0, 0, VkFormat.R32G32SFloat, ByteSize.Zero)
            .AddAttribute(1, 0, VkFormat.R32G32B32A32SFloat, ByteSize.Of(8)).Check();
        var assembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
        var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
        var raster = new VkPipelineRasterizationStateCreateInfo
        {
            PolygonMode = VkPolygonMode.Fill, CullMode = VkCullModeFlags.None, LineWidth = 1,
        };
        var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = Samples };
        var blend = new VkPipelineColorBlendAttachmentState
        {
            BlendEnable = true, SrcColorBlendFactor = VkBlendFactor.One, DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = VkBlendOp.Add, SrcAlphaBlendFactor = VkBlendFactor.One, DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = VkBlendOp.Add, ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit |
                VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
        };
        var colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blend };
        var dynamics = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
        var dynamicState = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamics };
        fixed (byte* main = "main\0"u8)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[2]
            {
                new() { Stage = VkShaderStageFlags.VertexBit, Module = _vertexShader, Name = main },
                new() { Stage = VkShaderStageFlags.FragmentBit, Module = _fragmentShader, Name = main },
            };
            _pipeline = _device!.CreateGraphicsPipeline(default, new VkGraphicsPipelineCreateInfo
            {
                StageCount = 2, Stages = stages, VertexInputState = vertexInput, InputAssemblyState = &assembly,
                ViewportState = &viewport, RasterizationState = &raster, MultisampleState = &multisample,
                ColorBlendState = &colorBlend, DynamicState = &dynamicState, Layout = _pipelineLayout, RenderPass = _renderPass,
                BasePipelineIndex = -1,
            }, null);
        }
    }

    private static void ValidateFrame(ShapeFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0)
            throw new ArgumentException("Shape frame dimensions must be positive.", nameof(frame));
        _ = checked(frame.Vertices.Length * 24);
        _ = checked(frame.Indices.Length * sizeof(uint));
        foreach (var vertex in frame.Vertices.Span)
        {
            var p = vertex.Position;
            var c = vertex.Color;
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || MathF.Abs(p.X) > 1_048_576 || MathF.Abs(p.Y) > 1_048_576 ||
                !float.IsFinite(c.X) || !float.IsFinite(c.Y) || !float.IsFinite(c.Z) || !float.IsFinite(c.W) ||
                c.W < 0 || c.W > 1 || c.X < 0 || c.X > c.W || c.Y < 0 || c.Y > c.W || c.Z < 0 || c.Z > c.W)
                throw new ArgumentException("Shape vertices require finite framebuffer coordinates and premultiplied linear colors.", nameof(frame));
        }
        foreach (var index in frame.Indices.Span)
            if (index >= frame.Vertices.Length)
                throw new ArgumentException("Shape index exceeds the vertex buffer.", nameof(frame));
        foreach (var command in frame.Commands.Span)
        {
            var clip = command.Clip;
            if (command.FirstIndex < 0 || command.IndexCount < 0 || command.IndexCount % 3 != 0 ||
                (long)command.FirstIndex + command.IndexCount > frame.Indices.Length ||
                clip.Left < 0 || clip.Top < 0 || clip.Right < clip.Left || clip.Bottom < clip.Top ||
                clip.Right > frame.Width || clip.Bottom > frame.Height)
                throw new ArgumentException("Shape command has an invalid triangle range or framebuffer scissor.", nameof(frame));
        }
    }

    private void Upload(ShapeFrame frame)
    {
        UploadBuffer(ref _vertices, frame.Vertices.Span, VkBufferUsageFlags.VertexBufferBit);
        UploadBuffer(ref _indices, frame.Indices.Span, VkBufferUsageFlags.IndexBufferBit);
    }

    private void UploadBuffer<T>(ref BufferEx buffer, ReadOnlySpan<T> data, VkBufferUsageFlags usage) where T : unmanaged
    {
        if (data.IsEmpty) return;
        var bytes = checked(data.Length * sizeof(T));
        if (buffer.IsNull() || buffer.BufferSize < ByteSize.Of(bytes))
        {
            // Called only after the shared frame fence. Allocate first to preserve ownership on failure.
            var replacement = _allocator!.CreateBuffer(new BufferEx.CreateInfo
            {
                Name = "", BufferSize = ByteSize.Of(bytes), BufferUsage = usage,
                BufferSharingMode = VkSharingMode.Exclusive, AllocPreference = MemoryPreference.PreferCpu,
                AllocRequiredProperties = VkMemoryPropertyFlags.HostVisibleBit,
            });
            var previous = buffer;
            buffer = replacement;
            if (!previous.IsNull()) previous.Dispose();
        }
        using var mapping = buffer.Map();
        data.CopyTo(mapping.AsSpan<T>());
        // MappedMemory.Flush uses raw device ranges without VMA allocation offsets or
        // nonCoherentAtomSize alignment. VMA's flush helper handles both correctly.
        Check(_allocator!.FlushAllocation(new VmaAllocation(buffer.BufferAllocationInfo.AllocationInfo.AllocHandle),
            ByteSize.Zero, ByteSize.Of(bytes)), "Flush shape upload");
    }

    private bool RecordShapes(int imageIndex, ShapeFrame? frame, LinearColor background)
    {
        var geometryRecorded = false;
        ref var api = ref _device!.PInvoke;
        Check(api.vkResetCommandBuffer.Invoke(_command, 0), "Reset command buffer");
        var begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
        Check(api.vkBeginCommandBuffer.Invoke(_command, &begin), "Begin command buffer");
        PrepareOverlay();
        var clear = new VkClearValue { Color = new VkClearColorValue { Float32 = new float4(background.R, background.G, background.B, background.A) } };
        var pass = new VkRenderPassBeginInfo
        {
            RenderPass = _renderPass, Framebuffer = _framebuffers[imageIndex],
            RenderArea = new VkRect2D { Extent = _extent }, ClearValueCount = 1, ClearValues = &clear,
        };
        api.vkCmdBeginRenderPass.Invoke(_command, &pass, VkSubpassContents.Inline);
        if (frame is { } shapes && !shapes.Indices.IsEmpty && !shapes.Commands.IsEmpty)
        {
            api.vkCmdBindPipeline.Invoke(_command, VkPipelineBindPoint.Graphics, _pipeline);
            var viewport = new VkViewport { Width = _extent.Width, Height = _extent.Height, MinDepth = 0, MaxDepth = 1 };
            api.vkCmdSetViewport.Invoke(_command, 0, 1, &viewport);
            var extent = new float2(_extent.Width, _extent.Height);
            api.vkCmdPushConstants.Invoke(_command, _pipelineLayout, VkShaderStageFlags.VertexBit, 0, 8, &extent);
            var vertices = _vertices.VkBuffer;
            var offset = (ByteSize64)0;
            api.vkCmdBindVertexBuffers.Invoke(_command, 0, 1, &vertices, &offset);
            api.vkCmdBindIndexBuffer.Invoke(_command, _indices.VkBuffer, ByteSize.Zero, VkIndexType.UInt32);
            foreach (var command in shapes.Commands.Span)
            {
                if (command.IndexCount == 0 || command.Clip.IsEmpty) continue;
                var clip = command.Clip;
                var scissor = new VkRect2D
                {
                    Offset = new VkOffset2D { X = clip.Left, Y = clip.Top },
                    Extent = new VkExtent2D { Width = clip.Right - clip.Left, Height = clip.Bottom - clip.Top },
                };
                api.vkCmdSetScissor.Invoke(_command, 0, 1, &scissor);
                api.vkCmdDrawIndexed.Invoke(_command, command.IndexCount, 1, command.FirstIndex, 0, 0);
                geometryRecorded = true;
            }
        }
        RecordOverlay();
        api.vkCmdEndRenderPass.Invoke(_command);
        Check(api.vkEndCommandBuffer.Invoke(_command), "End command buffer");
        return geometryRecorded;
    }

    private List<Action> SwapchainReleases()
    {
        var device = _device!;
        List<Action> releases = [ReleaseOverlaySwapchain];
        foreach (var framebuffer in _framebuffers)
            if (!framebuffer.IsNull()) releases.Add(() => device.DestroyFramebuffer(framebuffer, null));
        if (!_pipeline.IsNull()) { var pipeline = _pipeline; releases.Add(() => device.DestroyPipeline(pipeline, null)); }
        if (!_renderPass.IsNull()) { var pass = _renderPass; releases.Add(() => device.DestroyRenderPass(pass, null)); }
        foreach (var view in _multisampleViews.Concat(_views))
            if (!view.IsNull()) releases.Add(() => device.DestroyImageView(view, null));
        foreach (var image in _multisampleImages)
            if (!image.IsNull()) releases.Add(image.Dispose);
        foreach (var semaphore in _presentReady)
            if (!semaphore.IsNull()) releases.Add(() => device.DestroySemaphore(semaphore, null));
        if (!_swapchain.IsNull()) { var swapchain = _swapchain; releases.Add(() => device.DestroySwapchainKHR(swapchain, null)); }
        _framebuffers = []; _pipeline = default; _renderPass = default;
        _multisampleViews = []; _views = []; _multisampleImages = [];
        _presentReady = []; _images = []; _swapchain = default;
        return releases;
    }
}
