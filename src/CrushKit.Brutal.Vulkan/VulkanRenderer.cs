using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using CrushKit.Brutal.Draw2D;
using Brutal.VulkanApi.Vma;
using VulkanQueue = Brutal.VulkanApi.Queue;

namespace CrushKit.Brutal.Vulkan;

/// <summary>
/// Owns Vulkan presentation and synchronous consumption of borrowed shape frames.
/// All methods are called on the window's owning thread.
/// </summary>
public sealed unsafe partial class VulkanRenderer : IDisposable
{
    private const int Vulkan13 = (1 << 22) | (3 << 12);
    internal readonly GlfwWindow _window;
    private Instance? _instance;
    private PhysicalDevice _physicalDevice = null!;
    internal Device? _device;
    internal VulkanQueue _queue = null!;
    private VkSurfaceKHR _surface;
    private VkDebugUtilsMessengerEXT _messenger;
    private VkSwapchainKHR _swapchain;
    private VkCommandPool _commandPool;
    internal VkCommandBuffer _command;
    private VkFence _frameFence;
    private VkSemaphore _imageAvailable;
    // Presentation waits can outlive a submission fence. Index these by swapchain image,
    // not frame, so reacquisition guarantees the previous presentation wait completed.
    private VkSemaphore[] _presentReady = [];
    internal VkImage[] _images = [];
    private VkExtent2D _extent;
    private int _queueFamily;
    private bool _recreate = true;
    private bool _disposed;
    private static int _validationErrors;
    private static int _validationWarnings;

    public static int ValidationErrors => Volatile.Read(ref _validationErrors);
    public static int ValidationWarnings => Volatile.Read(ref _validationWarnings);
    public int PresentedFrames { get; private set; }

    private VulkanRenderer(GlfwWindow window) => _window = window;

    public static VulkanRenderer Create(GlfwWindow window, bool validation)
    {
        var renderer = new VulkanRenderer(window);
        ResourceLifetime.OnFailure(() => renderer.Initialize(validation), renderer.Dispose);
        return renderer;
    }

    private void Initialize(bool validation)
    {
        if (!Glfw.VulkanSupported())
            throw new InvalidOperationException("GLFW could not find a Vulkan loader and driver.");

        var extensions = new HashSet<string>();
        var names = Glfw.GetRequiredInstanceExtensions(out var count);
        if (names == null || count == 0)
            throw new InvalidOperationException("GLFW could not determine the Vulkan surface extensions.");
        for (var i = 0; i < count; i++)
            extensions.Add(Marshal.PtrToStringUTF8((nint)names[i])!);
        if (validation)
            extensions.Add("VK_EXT_debug_utils");

        var debug = new VkDebugUtilsMessengerCreateInfoEXT
        {
            MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
            MessageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT |
                          VkDebugUtilsMessageTypeFlagsEXT.PerformanceBitEXT,
            PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(&DebugCallback),
        };
        fixed (byte* appName = "CrushKit\0"u8)
        {
            _instance = global::Brutal.VulkanApi.Vulkan.CreateInstance(new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApplicationName = appName, ApiVersion = Vulkan13 },
                EnabledExtensions = extensions,
                EnabledLayers = validation ? ["VK_LAYER_KHRONOS_validation"] : [],
                // Also capture messages during instance creation/destruction.
                Next = validation ? &debug : null,
            });
        }
        if (validation)
            _messenger = _instance.CreateDebugUtilsMessengerEXT(debug, null);

        Check((VkResult)Glfw.CreateWindowSurface(_instance.Handle.VkHandle, _window, out var surface), "Create window surface");
        _surface = new VkSurfaceKHR((nint)surface);
        SelectDevice();

        var priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            QueuePriorities = &priority,
        };
        _device = _physicalDevice.CreateDevice(new DeviceEx.CreateInfo
        {
            Extensions = ["VK_KHR_swapchain"],
            QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&queueInfo, 1),
        });
        _queue = _device.GetQueue(_queueFamily, 0);
        InitializeShapes();
        _commandPool = _device.CreateCommandPool(new VkCommandPoolCreateInfo
        {
            QueueFamilyIndex = _queueFamily,
            Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit,
        }, null);
        var allocate = new VkCommandBufferAllocateInfo
        {
            CommandPool = _commandPool,
            Level = VkCommandBufferLevel.Primary,
        };
        _command = _device.AllocateCommandBuffer(allocate).Handle;
        _frameFence = _device.CreateFence(new VkFenceCreateInfo { Flags = VkFenceCreateFlags.SignaledBit }, null);
        _imageAvailable = _device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        Console.WriteLine($"Vulkan 1.3 initialized; validation {(validation ? "enabled" : "disabled")}.");
    }

    private void SelectDevice()
    {
        foreach (var candidate in _instance!.PhysicalDevices)
        {
            var properties = candidate.GetProperties();
            if (properties.ApiVersion < Vulkan13 || !candidate.ExtensionNames.Contains("VK_KHR_swapchain"))
                continue;
            var families = new VkQueueFamilyProperties[candidate.GetQueueFamilyProperties_Count()];
            candidate.GetQueueFamilyProperties_Write(families);
            for (var i = 0; i < families.Length; i++)
            {
                // One graphics + present family keeps ownership transfers out of this first backend.
                if (families[i].QueueCount == 0 || !families[i].QueueFlags.HasFlag(VkQueueFlags.GraphicsBit) ||
                    !candidate.GetSurfaceSupportKHR(i, _surface))
                    continue;
                if (candidate.GetSurfaceFormatsKHR_Count(_surface) == 0 ||
                    candidate.GetSurfacePresentModesKHR_Count(_surface) == 0)
                    continue;
                var capabilities = candidate.GetSurfaceCapabilitiesKHR(_surface);
                if (!capabilities.SupportedUsageFlags.HasFlag(VkImageUsageFlags.ColorAttachmentBit))
                    continue;
                _physicalDevice = candidate;
                _queueFamily = i;
                Console.WriteLine($"GPU: {Marshal.PtrToStringUTF8((nint)properties.DeviceName)}; queue family {i}.");
                return;
            }
        }
        throw new InvalidOperationException("No Vulkan 1.3 device with a combined graphics/present queue and color-attachment swapchain support was found.");
    }

    /// <returns>True only when indexed geometry from this frame was successfully presented.</returns>
    public bool Draw(ShapeFrame? frame, LinearColor background)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame is { } supplied) ValidateFrame(supplied);
        return DrawValidated(frame, background);
    }

    private bool DrawValidated(ShapeFrame? frame, LinearColor background)
    {
        var size = _window.FramebufferSize;
        if (_window.IsIconified || size.X <= 0 || size.Y <= 0)
        {
            _recreate = true;
            // Stay responsive without spinning while minimized; no async work exists yet.
            return false;
        }
        if (_recreate || size.X != _extent.Width || size.Y != _extent.Height)
            RecreateSwapchain(size);

        var device = _device!;
        Check(device.WaitForFences([_frameFence], true, -1), "Wait for frame fence");
        // A resize may invalidate a producer's pixel coordinates. Clear this frame rather
        // than stretch geometry/scissors; the caller should rebuild at the new extent.
        if (frame is { } sized && (sized.Width != _extent.Width || sized.Height != _extent.Height))
        {
            Console.WriteLine($"Shape frame {sized.Width}x{sized.Height} skipped after resize; rebuild at {_extent.Width}x{_extent.Height}.");
            frame = null;
        }
        if (frame is { } upload) Upload(upload);
        var acquired = AcquireImage(out var index);
        if (acquired == VkResult.ErrorOutOfDateKHR)
        {
            _recreate = true;
            return false;
        }
        if (acquired is VkResult.Timeout or VkResult.NotReady)
            return false; // Return to the OS event pump instead of hanging indefinitely in acquire.
        if (acquired != VkResult.SuboptimalKHR)
            Check(acquired, "Acquire image");

        var geometryRecorded = RecordShapes(index, frame, background);
        device.ResetFences([_frameFence]); // Only reset once there will actually be a submission.
        var wait = _imageAvailable;
        var signal = _presentReady[index];
        var stage = VkPipelineStageFlags.ColorAttachmentOutputBit;
        var command = _command;
        var submit = new VkSubmitInfo
        {
            WaitSemaphoreCount = 1, WaitSemaphores = &wait, WaitDstStageMask = &stage,
            CommandBufferCount = 1, CommandBuffers = &command,
            SignalSemaphoreCount = 1, SignalSemaphores = &signal,
        };
        _queue.Submit(new Span<VkSubmitInfo>(&submit, 1), _frameFence);
        var swapchain = _swapchain;
        var presented = PresentImage(new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1, WaitSemaphores = &signal,
            SwapchainCount = 1, Swapchains = &swapchain, ImageIndices = &index,
        });
        if (presented is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            _recreate = true;
        else
            Check(presented, "Present image");
        if (acquired == VkResult.SuboptimalKHR)
            _recreate = true;
        if (presented is VkResult.Success or VkResult.SuboptimalKHR)
        {
            PresentedFrames++;
            CountOverlayPresentation();
        }
        return geometryRecorded && presented is VkResult.Success or VkResult.SuboptimalKHR;
    }

    // Published BRUTAL wrappers return Suboptimal/Timeout/NotReady, but throw for
    // OutOfDate. Normalize only that recoverable exception; fatal driver errors retain
    // their original exception. In particular, do not reset the fence on failed acquire
    // or count an out-of-date presentation as a successfully presented frame.
    private VkResult AcquireImage(out int index)
    {
        try
        {
            return _device!.AcquireNextImageKHR(_swapchain, 100_000_000, _imageAvailable, default, out index);
        }
        catch (VkResultError error) when (error.Result == VkResult.ErrorOutOfDateKHR)
        {
            index = default; // Not used: Draw returns before indexing any per-image resource.
            return error.Result;
        }
    }

    private VkResult PresentImage(VkPresentInfoKHR present)
    {
        try
        {
            return _queue.PresentKHR(present);
        }
        catch (VkResultError error) when (error.Result == VkResult.ErrorOutOfDateKHR)
        {
            // Submission has already signaled its fence. Recreation waits device idle
            // before releasing this swapchain's presentation semaphores and attachments.
            return error.Result;
        }
    }

    private void RecreateSwapchain(int2 size)
    {
        var device = _device!;
        device.WaitIdle(); // Simple lifecycle boundary; never used for normal per-frame synchronization.
        DestroySwapchain();
        var caps = _physicalDevice.GetSurfaceCapabilitiesKHR(_surface);
        if (!caps.SupportedUsageFlags.HasFlag(VkImageUsageFlags.ColorAttachmentBit))
            throw new NotSupportedException("The surface no longer supports color-attachment swapchain images.");
        _extent = caps.CurrentExtent.Width != -1 ? caps.CurrentExtent : new VkExtent2D
        {
            Width = Math.Clamp(size.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
            Height = Math.Clamp(size.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height),
        };
        var formats = new VkSurfaceFormatKHR[_physicalDevice.GetSurfaceFormatsKHR_Count(_surface)];
        Check(_physicalDevice.GetSurfaceFormatsKHR_Write(_surface, formats), "Enumerate surface formats");
        var format = SelectSurfaceFormat(formats);
        RequireMultisampling(format.Format);
        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, caps.MaxImageCount);
        var alpha = new[] { VkCompositeAlphaFlagsKHR.OpaqueBitKHR, VkCompositeAlphaFlagsKHR.PreMultipliedBitKHR,
            VkCompositeAlphaFlagsKHR.PostMultipliedBitKHR, VkCompositeAlphaFlagsKHR.InheritBitKHR }
            .First(value => caps.SupportedCompositeAlpha.HasFlag(value));
        _swapchain = device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR
        {
            Surface = _surface, MinImageCount = imageCount,
            ImageFormat = format.Format, ImageColorSpace = format.ColorSpace, ImageExtent = _extent,
            ImageArrayLayers = 1, ImageUsage = VkImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = VkSharingMode.Exclusive, PreTransform = caps.CurrentTransform,
            CompositeAlpha = alpha, PresentMode = VkPresentModeKHR.FifoKHR, Clipped = true,
        }, null);
        _images = new VkImage[device.GetSwapchainImagesKHR_Count(_swapchain)];
        Check(device.GetSwapchainImagesKHR_Write(_swapchain, _images), "Enumerate swapchain images");
        _presentReady = new VkSemaphore[_images.Length];
        for (var i = 0; i < _presentReady.Length; i++)
            _presentReady[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        CreateAttachments(format.Format);
        _swapchainGeneration++;
        _recreate = false;
        Console.WriteLine($"Swapchain: {_extent.Width}x{_extent.Height}, {_images.Length} images, {format.Format}, FIFO.");
    }

    private void DestroySwapchain()
    {
        ResourceLifetime.ReleaseAll(SwapchainReleases().ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Even device loss must not skip native destruction. Run every release step,
        // then report all failures (including non-device-loss errors) to the owner.
        // OnFailure/Run preserve an initialization/render exception alongside these errors.
        var device = _device;
        var instance = _instance;
        List<Action> releases = [];
        if (device != null)
        {
            releases.Add(device.WaitIdle);
            releases.Add(DisposeOverlay);
            releases.AddRange(SwapchainReleases());
            if (!_vertices.IsNull()) releases.Add(_vertices.Dispose);
            if (!_indices.IsNull()) releases.Add(_indices.Dispose);
            if (!_vertexShader.IsNull()) releases.Add(() => device.DestroyShaderModule(_vertexShader, null));
            if (!_fragmentShader.IsNull()) releases.Add(() => device.DestroyShaderModule(_fragmentShader, null));
            if (!_pipelineLayout.IsNull()) releases.Add(() => device.DestroyPipelineLayout(_pipelineLayout, null));
            if (_allocator != null) releases.Add(() => device.DestroyVmaAllocator(_allocator));
            if (!_imageAvailable.IsNull()) releases.Add(() => device.DestroySemaphore(_imageAvailable, null));
            if (!_frameFence.IsNull()) releases.Add(() => device.DestroyFence(_frameFence, null));
            if (!_commandPool.IsNull()) releases.Add(() => device.DestroyCommandPool(_commandPool, null));
            releases.Add(device.Dispose);
        }
        if (instance != null)
        {
            if (!_surface.IsNull()) releases.Add(() => instance.DestroySurfaceKHR(_surface, null));
            if (!_messenger.IsNull()) releases.Add(() => instance.DestroyDebugUtilsMessengerEXT(_messenger, null));
            releases.Add(instance.Dispose);
        }
        ResourceLifetime.ReleaseAll(releases.ToArray());
    }

    private static void Check(VkResult result, string operation)
    {
        if (result != VkResult.Success)
            throw new InvalidOperationException($"{operation} failed: {result}.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 DebugCallback(VkDebugUtilsMessageSeverityFlagsEXT severity,
        VkDebugUtilsMessageTypeFlagsEXT type, VkDebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        // No managed exception may escape an unmanaged callback.
        try
        {
            if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0)
                Interlocked.Increment(ref _validationErrors);
            else if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT) != 0)
                Interlocked.Increment(ref _validationWarnings);
            Console.Error.WriteLine($"[Vulkan {severity}/{type}] {Marshal.PtrToStringUTF8((nint)data->Message)}");
        }
        catch { /* Logging must not unwind through the native driver. */ }
        return false;
    }
}
