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
    /// <param name="drawControls">Optional synchronous renderer-thread callback, immediately after NewFrame
    /// during BeginOverlayFrame, before diagnostics and swapchain image acquisition, with this inspector's
    /// context and frame active. May emit its own windows;
    /// balance all UI scopes (also on exceptions). UI only: do not call NewFrame, Render, EndFrame, change
    /// or destroy contexts, or dispose the renderer/inspector. Exceptions cancel the frame, retire the
    /// context/backends at the renderer's fence boundary, and propagate. A later begin recreates them.</param>
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
    // Physical-pixel session state survives context retirement; never written to an ini file.
    private float2 _diagnosticsPosition, _diagnosticsSize, _lastScale;
    private bool _hasDiagnosticsBounds, _restoreDiagnostics = true, _diagnosticsCollapsed;
    private bool _previewOpen = true, _renderingOpen, _textOpen, _fontOpen;

    internal override OverlayCapture BeginFrame(VulkanRenderer renderer)
    {
        _renderer = renderer;
        ResourceLifetime.OnFailure(() => Ensure(renderer, Settings), Release);
        Ui.SetCurrentContext(_uiContext);
        _uiRenderer!.NewFrame(); _uiPlatform!.NewFrame();
        Ui.GetStyle().FontSizeBase = InspectorFont.LogicalSize(Settings.FontSize, Ui.GetIO().DisplayFramebufferScale);
        Ui.NewFrame(); _uiFrame = true;
        DrawControls();
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
        var io = Ui.GetIO(); var scale = io.DisplayFramebufferScale;
        bool expanded = BeginDiagnostics();
        try
        {
            if (expanded)
            {
                DrawOverview(frame.Diagnostics);
                if (Section("Preview", ref _previewOpen)) DrawPreview(renderer);
                if (Section("Rendering", ref _renderingOpen))
                    Rows("rendering", ("Shape frames", $"{frame.Diagnostics.PresentedShapeFrames}"),
                        ("Vertices", $"{frame.Diagnostics.ShapeVertices}"), ("Indices", $"{frame.Diagnostics.ShapeIndices}"),
                        ("Overlay frames", $"{renderer.OverlayEvidence.PresentedFrames}"));
                if (Section("Text cache", ref _textOpen))
                    Rows("text", ("Entries", $"{text.Count}"), ("Rasterizations", $"{text.Rasterizations}"), ("Hits", $"{text.CacheHits}"));
                if (Section("Font", ref _fontOpen))
                    Rows("font", ("Inspector TTF", $"{_inspectorFont.GetDebugName()}"),
                        ("Logical px", $"{Ui.GetFontSize():F2}"), ("Physical px", $"{Ui.GetFontSize() * scale.Y:F2}"),
                        ("Framebuffer scale", $"{scale.X:F2} x {scale.Y:F2}"), ("Raster density", $"{Ui.GetFontBaked().RasterizerDensity:F2}"));
                _recordedInspector = Visible(Ui.GetWindowPos(), Ui.GetWindowPos() + Ui.GetWindowSize(), io.DisplaySize);
            }
        }
        finally { EndDiagnostics(); }
        Ui.Render(); _uiFrame = false;
        return new(Ui.GetDrawData().TotalIdxCount, _recordedImage, _recordedInspector);
    }

    private bool BeginDiagnostics()
    {
        var io = Ui.GetIO(); var scale = io.DisplayFramebufferScale;
        if (!_hasDiagnosticsBounds)
        {
            _diagnosticsPosition = new(Settings.Left, Settings.Top);
            _diagnosticsSize = new(Settings.Width, Settings.Height);
            _hasDiagnosticsBounds = true;
        }
        var display = io.DisplaySize;
        var size = new float2(Math.Min(_diagnosticsSize.X / scale.X, display.X), Math.Min(_diagnosticsSize.Y / scale.Y, display.Y));
        // A collapsed window occupies only its titlebar, not its retained expanded body.
        float visibleHeight = _diagnosticsCollapsed ? Ui.GetFrameHeight() : size.Y;
        var position = new float2(Math.Clamp(_diagnosticsPosition.X / scale.X, 0, Math.Max(0, display.X - size.X)),
            Math.Clamp(_diagnosticsPosition.Y / scale.Y, 0, Math.Max(0, display.Y - visibleHeight)));
        bool scaleChanged = scale.X != _lastScale.X || scale.Y != _lastScale.Y;
        if (_restoreDiagnostics || scaleChanged || Math.Abs(size.X * scale.X - _diagnosticsSize.X) > 0.01f || Math.Abs(size.Y * scale.Y - _diagnosticsSize.Y) > 0.01f)
            Ui.SetNextWindowSize(size, ImGuiCond.Always);
        if (_restoreDiagnostics || scaleChanged || Math.Abs(position.X * scale.X - _diagnosticsPosition.X) > 0.01f || Math.Abs(position.Y * scale.Y - _diagnosticsPosition.Y) > 0.01f)
            Ui.SetNextWindowPos(position, ImGuiCond.Always);
        _diagnosticsSize = new(size.X * scale.X, size.Y * scale.Y);
        if (_restoreDiagnostics) Ui.SetNextWindowCollapsed(_diagnosticsCollapsed, ImGuiCond.Always);
        _restoreDiagnostics = false; _lastScale = scale;
        bool expanded = Ui.Begin("Diagnostics (read only)"u8, ImGuiWindowFlags.NoSavedSettings);
        if (expanded && _diagnosticsCollapsed)
        {
            // Expansion happens inside Begin: recover the body immediately, not one frame later.
            var actualPosition = Ui.GetWindowPos(); var actualSize = Ui.GetWindowSize();
            Ui.SetWindowPos(new float2(Math.Clamp(actualPosition.X, 0, Math.Max(0, display.X - actualSize.X)),
                Math.Clamp(actualPosition.Y, 0, Math.Max(0, display.Y - actualSize.Y))));
        }
        return expanded;
    }

    private void EndDiagnostics()
    {
        try
        {
            var scale = Ui.GetIO().DisplayFramebufferScale;
            var pos = Ui.GetWindowPos(); var size = Ui.GetWindowSize();
            _diagnosticsPosition = new(pos.X * scale.X, pos.Y * scale.Y);
            _diagnosticsCollapsed = Ui.IsWindowCollapsed();
            // GetWindowSize reports only titlebar height while collapsed.
            if (!_diagnosticsCollapsed) _diagnosticsSize = new(size.X * scale.X, size.Y * scale.Y);
        }
        finally { Ui.End(); }
    }

    private static bool Section(string label, ref bool open)
    {
        Ui.SetNextItemOpen(open, ImGuiCond.Always);
        return open = Ui.CollapsingHeader(Encoding.UTF8.GetBytes(label));
    }

    private static void DrawOverview(OverlayDiagnostics diagnostics) =>
        Rows("overview", ("State", diagnostics.State), ("Details", diagnostics.Detail), ("Frame", $"{diagnostics.FrameMilliseconds:F2} ms"));

    private static void Rows(string id, params (string Label, string Value)[] rows)
    {
        if (!Ui.BeginTable(Encoding.UTF8.GetBytes(id), 2, ImGuiTableFlags.SizingStretchProp)) return;
        try
        {
            Ui.TableSetupColumn("Label"u8, ImGuiTableColumnFlags.WidthStretch, 0.36f);
            Ui.TableSetupColumn("Value"u8, ImGuiTableColumnFlags.WidthStretch, 0.64f);
            foreach (var row in rows)
            {
                Ui.TableNextRow(); Ui.TableNextColumn(); Ui.TextWrapped(Encoding.UTF8.GetBytes(row.Label));
                Ui.TableNextColumn(); Ui.TextWrapped(Encoding.UTF8.GetBytes(row.Value));
            }
        }
        finally { Ui.EndTable(); }
    }

    private void DrawPreview(VulkanRenderer renderer)
    {
        var io = Ui.GetIO(); var scale = io.DisplayFramebufferScale;
        var available = Ui.GetContentRegionAvail();
        // Preserve physical aspect ratio, fit both the column and remaining usable body.
        float factor = Math.Min(1, Math.Min(available.X * scale.X / Settings.ImageSize, available.Y * scale.Y / Settings.ImageSize));
        if (factor <= 0 || !OverlayPixels.HasCoverage(_previewPixels!)) return;
        var imageSize = new float2(Settings.ImageSize * factor / scale.X, Settings.ImageSize * factor / scale.Y);
        var min = Ui.GetCursorScreenPos();
        if (!Ui.IsRectVisible(imageSize) || !Visible(min, min + imageSize, io.DisplaySize))
        {
            // Clipping must not remove the widget's layout extent (and change the scroll range).
            Ui.Dummy(imageSize);
            return;
        }
        if (!_hasPreviewReference)
        {
            _previewGpu = renderer.UploadOverlayTexture(_previewPixels!, gameDescriptor: false);
            _previewReference = _uiRenderer!.AddTexture(renderer._overlaySampler, _previewGpu.View.VkImageView, VkImageLayout.ShaderReadOnlyOptimal);
            _hasPreviewReference = true;
        }
        var drawList = Ui.GetWindowDrawList(); int before = drawList.IdxBuffer.Count;
        Ui.Image(_previewReference, imageSize);
        _recordedImage = drawList.IdxBuffer.Count > before;
    }

    // BeginOverlayFrame retires context/backends after the shared fence on any BeginFrame failure.
    // Cancel here immediately: the renderer has not yet marked this inspector frame as active.
    private void DrawControls() => ResourceLifetime.OnFailure(() => _drawControls?.Invoke(), Cancel);

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
        finally { _uiRenderer = null; _uiPlatform = null; _hasUiContext = false; _inspectorFont = default; _restoreDiagnostics = true; }
    }

    public override void Dispose()
    {
        List<Action> releases = [Release];
        if (_debugPixels != null) { var pixels = _debugPixels; _debugPixels = null; releases.Add(pixels.Destroy); }
        if (_previewPixels != null) { var pixels = _previewPixels; _previewPixels = null; releases.Add(pixels.Destroy); }
        ResourceLifetime.ReleaseAll(releases.ToArray());
    }
}
