using System.Text;
using Brutal.Numerics;
using Brutal.TextureApi.Abstractions;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace CrushKit.Brutal.Vulkan;

public sealed unsafe partial class VulkanRenderer
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private OverlaySettings? _overlaySettings;
    private OverlayText? _overlayText;
    private VulkanInspector? _inspector;
    private bool _inspectorActive, _uiFrame, _overlayActiveFrame;
    private OverlayFrame? _overlayFrame;
    private bool _inspectorVisible;
    private readonly OverlayCaptureTracker _captureTracker = new();
    private readonly Dictionary<string, OverlayGpuTexture> _overlayTextures = new(StringComparer.Ordinal);
    private readonly List<BufferEx> _overlayStaging = [];
    private int _swapchainGeneration, _recordedLabels, _recordedLabelIndices, _recordedUiIndices;
    private bool _recordedImage, _recordedInspector;
    public OverlayPresentationEvidence OverlayEvidence { get; private set; }
    public bool InspectorActive => _inspectorActive;

    public void ConfigureOverlay(OverlaySettings settings, VulkanInspector? inspector = null)
    {
        CheckOverlayThread(); ObjectDisposedException.ThrowIf(_disposed, this);
        if (_overlaySettings != null) throw new InvalidOperationException("Overlay is startup-only.");
        _overlaySettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _inspector = inspector;
    }

    /// <summary>Before input routing. HUD coordinates/font size are physical framebuffer pixels, independent of ImGui/DPI.</summary>
    public OverlayCapture BeginOverlayFrame(bool? inspectorVisible = null)
    {
        CheckOverlayThread(); ObjectDisposedException.ThrowIf(_disposed, this);
        CancelOverlayFrame();
        var settings = _overlaySettings ?? throw new InvalidOperationException("Configure overlay first.");
        if (!settings.Enabled) return _captureTracker.Update(false, false, false);
        _inspectorVisible = inspectorVisible ?? _inspector?.InitiallyVisible ?? false;
        if (!_inspectorVisible && _inspectorActive)
        {
            // Remove callbacks/context immediately on close, even if minimized, after their last GPU use.
            RetireInspector();
        }
        var size = _window.FramebufferSize;
        if (_window.IsIconified || size.X <= 0 || size.Y <= 0) return _captureTracker.Update(false, false, false);
        if (_recreate || size.X != _extent.Width || size.Y != _extent.Height) RecreateSwapchain(size);
        ResourceLifetime.OnFailure(EnsureGameOverlay, DisposeOverlay);
        OverlayCapture capture = default;
        if (_inspectorVisible && _inspector != null)
        {
            // Application UI runs here, before Draw can acquire a swapchain image. A failed first
            // begin must retire resources too: _inspectorActive has not been set yet. Prior frames
            // may still use the backends, so retirement waits the same fence as closing the inspector.
            ResourceLifetime.OnFailure(() => capture = _inspector.BeginFrame(this), RetireInspector);
            _inspectorActive = _uiFrame = true;
        }
        _overlayActiveFrame = true;
        return _captureTracker.Update(_inspectorActive, capture.Mouse, capture.Keyboard);
    }

    public void SetOverlayFrame(OverlayFrame frame)
    {
        CheckOverlayThread(); ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = _overlaySettings ?? throw new InvalidOperationException("Configure overlay first.");
        ValidateOverlayFrame(frame, settings);
        if (_overlayActiveFrame) _overlayFrame = new(frame.Labels.ToArray(), frame.Diagnostics);
    }

    private static void ValidateOverlayFrame(OverlayFrame frame, OverlaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(frame); ArgumentNullException.ThrowIfNull(frame.Labels); ArgumentNullException.ThrowIfNull(frame.Diagnostics);
        if (frame.Labels.Count > settings.MaxLabels) throw new ArgumentException("Too many overlay labels.");
        foreach (var label in frame.Labels)
        {
            if (label == null || string.IsNullOrEmpty(label.Text) || label.Text.Length > settings.MaxLabelCharacters || label.Text.Any(char.IsControl))
                throw new ArgumentException("Invalid overlay label.");
            var c = label.Color;
            if (!float.IsFinite(c.R) || !float.IsFinite(c.G) || !float.IsFinite(c.B) || !float.IsFinite(c.A) || c.A < 0 || c.A > 1 ||
                c.R < 0 || c.G < 0 || c.B < 0 || c.R > 1 || c.G > 1 || c.B > 1) throw new ArgumentException("Expected straight-linear palette tint.");
        }
        var d = frame.Diagnostics;
        if (d.State == null || d.State.Length > settings.MaxLabelCharacters || d.State.Any(char.IsControl) ||
            !double.IsFinite(d.FrameMilliseconds) || d.FrameMilliseconds < 0 || d.Detail == null || d.Detail.Length > settings.MaxLabelCharacters ||
            d.PresentedShapeFrames < 0 || d.ShapeVertices < 0 || d.ShapeIndices < 0) throw new ArgumentException("Invalid diagnostic facts.");
    }

    // After the shared fence; staging copies and barriers precede the render pass.
    private void PrepareOverlay()
    {
        _recordedLabels = _recordedLabelIndices = _recordedUiIndices = 0;
        _recordedImage = _recordedInspector = false;
        var retiredStaging = _overlayStaging.ToArray(); _overlayStaging.Clear();
        ResourceLifetime.ReleaseAll(retiredStaging.Select(b => (Action)b.Dispose).ToArray());
        _overlayDraws.Clear(); _overlayBatch?.Clear();
        if (!_overlayActiveFrame) return;
        if (_overlayFrame == null) { CancelOverlayFrame(); return; }
        var s = _overlaySettings!;
        var active = _overlayFrame.Labels.Select(l => l.Text).ToHashSet(StringComparer.Ordinal);
        if (_overlayTextures.Count + active.Count(t => !_overlayTextures.ContainsKey(t)) > s.CacheEntries)
            foreach (var key in _overlayTextures.Keys.Where(t => !active.Contains(t)).ToArray()) ReleaseOverlayTexture(key);
        for (int i = 0; i < _overlayFrame.Labels.Count; i++)
        {
            var label = _overlayFrame.Labels[i];
            if (!_overlayTextures.TryGetValue(label.Text, out var texture))
            {
                // Borrowed GenericTexture is copied to GPU staging immediately, before the next cache access can evict it.
                texture = UploadOverlayTexture(_overlayText!.Get(label.Text), gameDescriptor: true);
                try { _overlayTextures.Add(label.Text, texture); }
                catch { ReleaseGpuTexture(texture); throw; }
            }
            int first = _overlayBatch!.Indices.Length;
            if (texture.HasCoverage && _overlayBatch.TryAdd(s.Left, s.Top + i * s.LineSpacing,
                texture.Width, texture.Height, _extent.Width, _extent.Height, label.Color))
                _overlayDraws.Add((first, texture.Set));
        }
        UploadBuffer(ref _overlayVertices, _overlayBatch!.Vertices, VkBufferUsageFlags.VertexBufferBit);
        UploadBuffer(ref _overlayIndices, _overlayBatch.Indices, VkBufferUsageFlags.IndexBufferBit);
        if (_uiFrame)
        {
            var evidence = _inspector!.Prepare(this, _overlayFrame, _overlayText!);
            _recordedUiIndices = evidence.Indices; _recordedImage = evidence.Image; _recordedInspector = evidence.Window;
            _uiFrame = false;
        }
        _overlayActiveFrame = false;
    }

    private void CountOverlayPresentation()
    {
        int indices = _recordedLabelIndices + _recordedUiIndices;
        if (indices <= 0) return;
        var old = OverlayEvidence;
        OverlayEvidence = new(old.PresentedFrames + 1, old.TextFrames + (_recordedLabels > 0 ? 1 : 0),
            old.ImageFrames + (_recordedImage && _recordedUiIndices > 0 ? 1 : 0), old.InspectorFrames + (_recordedInspector && _recordedUiIndices > 0 ? 1 : 0),
            old.LabelQuads + _recordedLabels, old.DrawIndices + indices, _extent.Width, _extent.Height, _swapchainGeneration);
    }

    private void CancelOverlayFrame()
    {
        if (_uiFrame) { _uiFrame = false; _inspector?.Cancel(); }
        _overlayFrame = null; _overlayActiveFrame = false;
    }
    private void CheckOverlayThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread) throw new InvalidOperationException("Overlay is main-thread owned.");
    }

    private void RetireInspector()
    {
        Check(_device!.WaitForFences([_frameFence], true, -1), "Wait for inspector retirement");
        ReleaseInspector();
    }

    private void ReleaseInspector()
    {
        try { _inspector?.Release(); }
        finally { _inspectorActive = false; }
    }

    // Device-idle swapchain boundary, before destroying the render pass.
    private void ReleaseOverlaySwapchain()
    {
        List<Action> releases = [CancelOverlayFrame, ReleaseInspector];
        foreach (var key in _overlayTextures.Keys.ToArray()) releases.Add(() => ReleaseOverlayTexture(key));
        foreach (var staging in _overlayStaging) releases.Add(staging.Dispose); _overlayStaging.Clear();
        if (!_overlayPipeline.IsNull()) { var pipeline = _overlayPipeline; _overlayPipeline = default; releases.Add(() => _device!.DestroyPipeline(pipeline, null)); }
        ResourceLifetime.ReleaseAll(releases.ToArray());
    }

    private void DisposeOverlay()
    {
        List<Action> releases = [ReleaseOverlaySwapchain, DisposeOverlayGpu];
        if (_overlayText != null) { var text = _overlayText; _overlayText = null; releases.Add(text.Dispose); }
        if (_inspector != null) releases.Add(_inspector.Dispose);
        try { ResourceLifetime.ReleaseAll(releases.ToArray()); }
        finally { _inspector = null; }
    }
}
