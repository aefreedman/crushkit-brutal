using System.Text;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.Numerics;
using Brutal.TextureApi.Abstractions;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using CrushKit.Brutal.Vulkan;
using Ui = global::Brutal.ImGuiApi.ImGui;

namespace CrushKit.Brutal.ImGui;

/// <summary>Optional diagnostics inspector with application-supplied UI controls.</summary>
public sealed class ImGuiInspector : VulkanInspector
{
    public InspectorSettings Settings { get; }
    private readonly Action? _drawControls;

    /// <summary>Creates an inspector with optional application UI.</summary>
    /// <param name="settings">Validated inspector assets and layout, retained by reference.</param>
    /// <param name="drawControls">Optional synchronous renderer-thread callback, after the diagnostics window
    /// ends and before Render, with this inspector's context and frame active. May emit its own windows;
    /// balance all UI scopes (also on exceptions). UI only: do not call NewFrame, Render, EndFrame, change
    /// or destroy contexts, or dispose the renderer/inspector. Exceptions cancel the frame and propagate;
    /// context/backend ownership and retirement remain with the inspector.</param>
    public ImGuiInspector(InspectorSettings settings, Action? drawControls = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _drawControls = drawControls;
    }
    internal override bool InitiallyVisible => Settings.Visible;
    private ImGuiContextPtr _uiContext;
    private ImFontPtr _inspectorFont;
    private bool _hasUiContext, _uiFrame;
    private GlfwBackend? _uiPlatform;
    private VulkanBackend? _uiRenderer;
    private GenericTexture? _debugPixels, _previewPixels;
    private VulkanRenderer.OverlayGpuTexture? _previewGpu;
    private ImTextureRef _previewReference;
    private bool _hasPreviewReference, _recordedImage, _recordedInspector;
    private VulkanRenderer? _renderer;

    internal override OverlayCapture BeginFrame(VulkanRenderer renderer)
    {
        _renderer = renderer;
        ResourceLifetime.OnFailure(() => Ensure(renderer, Settings), Release);
        Ui.SetCurrentContext(_uiContext);
        _uiRenderer!.NewFrame(); _uiPlatform!.NewFrame();
        Ui.GetStyle().FontSizeBase = InspectorFont.LogicalSize(Settings.FontSize, Ui.GetIO().DisplayFramebufferScale);
        Ui.NewFrame(); _uiFrame = true;
        return new(Ui.GetIO().WantCaptureMouse, Ui.GetIO().WantCaptureKeyboard, false);
    }

    private void Ensure(VulkanRenderer renderer, InspectorSettings s)
    {
        _debugPixels ??= OverlayPixels.LoadPng(s.ImagePath, s.MaxTextureDimension, s.MaxEncodedImageBytes);
        _previewPixels ??= OverlayPixels.CreateInspectorPreview(_debugPixels);
        if (!_hasUiContext)
        {
            _uiContext = Ui.CreateContext();
            if (_uiContext.IsNull()) throw new InvalidOperationException("ImGui context creation failed.");
            _hasUiContext = true;
            var io = Ui.GetIO(); io.IniFilename = default; io.LogFilename = default;
            io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
            _inspectorFont = InspectorFont.Load(io, s.FontPath, s.FontSize);
            _uiPlatform = new GlfwBackend(renderer._window, GlfwBackend.ClientApi.Vulkan);
        }
        Ui.SetCurrentContext(_uiContext);
        _uiRenderer ??= new VulkanBackend(new VulkanBackend.CreateInfo
        {
            Device = (DeviceEx)renderer._device!, GraphicsQueue = renderer._queue, RenderPass = renderer._renderPass, SubPass = 0,
            MinImageCount = Math.Max(2, renderer._images.Length), ImageCount = renderer._images.Length, SampleCount = VkSampleCountFlags._4Bit,
            DescriptorPoolSize = 8,
        });
    }

    internal override InspectorEvidence Prepare(VulkanRenderer renderer, OverlayFrame frame, OverlayText text)
    {
        _recordedImage = _recordedInspector = false;
        Ui.SetCurrentContext(_uiContext);
        var s = Settings; var io = Ui.GetIO(); var scale = io.DisplayFramebufferScale;
        // Inspector settings are also physical pixels; convert only at the local ImGui boundary.
        Ui.SetNextWindowPos(new float2(s.Left / scale.X, s.Top / scale.Y), ImGuiCond.Always);
        Ui.SetNextWindowSize(new float2(s.Width / scale.X, s.Height / scale.Y), ImGuiCond.Always);
        if (Ui.Begin("Diagnostics (read only)"u8, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
        {
            var d = frame.Diagnostics;
            Ui.Text(Encoding.UTF8.GetBytes($"{d.State}\n{d.Detail}\nFrame {d.FrameMilliseconds:F2} ms\nShape frames {d.PresentedShapeFrames}\nVertices {d.ShapeVertices}  Indices {d.ShapeIndices}\nOverlay frames {renderer.OverlayEvidence.PresentedFrames}\nText cache {text.Count}, rasterizations {text.Rasterizations}, hits {text.CacheHits}\nInspector TTF {_inspectorFont.GetDebugName()}\nFont {Ui.GetFontSize():F2} logical / {Ui.GetFontSize() * scale.Y:F2} physical px\nFramebuffer scale {scale.X:F2} x {scale.Y:F2}, raster density {Ui.GetFontBaked().RasterizerDensity:F2}\nF1 closes this inspector"));
            var min = Ui.GetCursorScreenPos(); var available = Ui.GetContentRegionAvail();
            var imageSize = new float2(s.ImageSize / scale.X, s.ImageSize / scale.Y);
            if (OverlayPixels.HasCoverage(_previewPixels!) && available.X > 0 && available.Y > 0 && Visible(min, min + imageSize, io.DisplaySize))
            {
                if (!_hasPreviewReference)
                {
                    _previewGpu = renderer.UploadOverlayTexture(_previewPixels!, gameDescriptor: false);
                    _previewReference = _uiRenderer!.AddTexture(renderer._overlaySampler, _previewGpu.View.VkImageView, VkImageLayout.ShaderReadOnlyOptimal);
                    _hasPreviewReference = true;
                }
                var drawList = Ui.GetWindowDrawList(); int before = drawList.IdxBuffer.Count;
                Ui.Image(_previewReference, imageSize);
                _recordedImage = drawList.IdxBuffer.Count > before; // Actual image indices, not just a requested widget.
            }
            _recordedInspector = Visible(Ui.GetWindowPos(), Ui.GetWindowPos() + Ui.GetWindowSize(), io.DisplaySize);
        }
        Ui.End();
        CompleteUiFrame();
        return new(Ui.GetDrawData().TotalIdxCount, _recordedImage, _recordedInspector);
    }

    private void CompleteUiFrame() => ResourceLifetime.OnFailure(() =>
    {
        _drawControls?.Invoke();
        Ui.Render(); _uiFrame = false;
    }, Cancel);

    private static bool Visible(float2 min, float2 max, float2 size) => max.X > 0 && max.Y > 0 && min.X < size.X && min.Y < size.Y && max.X > min.X && max.Y > min.Y;


    internal override void Record(VulkanRenderer renderer) =>
        _uiRenderer!.RenderDrawData(Ui.GetDrawData(), new CommandBuffer(renderer._device!, renderer._command));

    internal override void Cancel()
    {
        if (_uiFrame) { _uiFrame = false; Ui.SetCurrentContext(_uiContext); Ui.EndFrame(); }
    }

    internal override void Release()
    {
        List<Action> releases = [];
        if (_hasUiContext) Ui.SetCurrentContext(_uiContext);
        if (_uiFrame) { _uiFrame = false; releases.Add(Ui.EndFrame); }
        if (_hasPreviewReference) { _hasPreviewReference = false; releases.Add(() => _uiRenderer!.RemoveTexture(_previewReference)); }
        if (_previewGpu != null) { var gpu = _previewGpu; _previewGpu = null; releases.Add(() => _renderer!.ReleaseGpuTexture(gpu)); }
        if (_uiRenderer != null) releases.Add(_uiRenderer.Dispose);
        if (_uiPlatform != null) releases.Add(_uiPlatform.Dispose);
        if (_hasUiContext) releases.Add(() => Ui.DestroyContext(_uiContext));
        try { ResourceLifetime.ReleaseAll(releases.ToArray()); }
        finally { _uiRenderer = null; _uiPlatform = null; _hasUiContext = false; _inspectorFont = default; }
    }

    public override void Dispose()
    {
        List<Action> releases = [Release];
        if (_debugPixels != null) { var pixels = _debugPixels; _debugPixels = null; releases.Add(pixels.Destroy); }
        if (_previewPixels != null) { var pixels = _previewPixels; _previewPixels = null; releases.Add(pixels.Destroy); }
        ResourceLifetime.ReleaseAll(releases.ToArray());
    }
}
