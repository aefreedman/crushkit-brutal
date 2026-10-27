# ImGui inspector application controls

`CrushKit.Brutal.ImGui` provides the sealed `ImGuiInspector`. For opt-in responsive category navigation, aligned property rows and action flow, see [tool layout composition](imgui-layout.md).

```csharp
public ImGuiInspector(InspectorSettings settings, Action? drawControls = null)
public ImGuiInspector(InspectorSettings settings, Action? drawControls,
    FrameTimeHistory? frameTimeHistory)
```

Pass it to `renderer.ConfigureOverlay(textSettings, inspector)`; ownership transfers to the renderer. Settings, callback and optional history are retained, not copied. Construction does not invoke the callback or initialize native resources. A controls-only consumer supplies no PNG or image limits:

```csharp
var settings = new InspectorSettings(fontPath, fontSize, visible, left, top,
    width, height, diagnosticsVisible: false);
var history = new FrameTimeHistory(historyCapacity); // Explicit application-owned capacity.
var inspector = new ImGuiInspector(settings, DrawApplicationControls, history);
renderer.ConfigureOverlay(textSettings, inspector);
// On the owner thread, outside UI drawing, once per host-defined elapsed-frame observation:
history.Observe(observationId, elapsedMilliseconds);
// A consumer tool button may toggle this independently of its own application windows:
inspector.DiagnosticsVisible = true;
```

`DiagnosticsVisible` is initialized from settings and controls only the kit diagnostics window. It does not suppress the application callback, end the ImGui context or disable capture. The original two-parameter `ImGuiInspector` constructor remains available for compiled consumers. The legacy PNG-taking `InspectorSettings` constructor remains available and explicitly enables diagnostics plus an image preview. `InspectorSettings.ImagePath` is now nullable because image-free settings have no path; nullable-enabled consumers should use `Preview` or handle absence explicitly (including when treating nullable warnings as errors). The other legacy image accessors report zero without a preview.

When the inspector is visible, the callback runs synchronously on the renderer thread during `BeginOverlayFrame`, immediately after `Ui.NewFrame()`, before diagnostics are emitted and before any swapchain image acquisition. Its own ImGui context and frame are current/active. It runs once per successfully started inspector frame, even if that frame is later cancelled or never submitted. It is not called for hidden, disabled or minimized inspector frames. It is not an input-polling or simulation-update hook.

## UI-only contract

- Emit application UI using `Brutal.ImGuiApi.ImGui`, including your own windows. Own your labels, state and policy. Use normal ImGui logical coordinates (inspector layout settings alone are physical pixels).
- Balance every Begin/End, Push/Pop and other UI scope, **even when throwing**, using `try/finally` where needed.
- Do not call `NewFrame`, `Render` or `EndFrame`. Do not create, switch, destroy or otherwise manage ImGui contexts/backends. Do not dispose or reconfigure the renderer/inspector, or re-enter rendering.
- Do not retain borrowed ImGui frame/draw data. Do not perform simulation steps, blocking work or GPU/resource-lifetime operations in the callback. Widgets may update application-owned UI state or enqueue commands; apply game policy outside drawing.

A callback exception cancels its active frame via the existing `Cancel`/`EndFrame` path. Before propagating from `BeginOverlayFrame`, the renderer waits the shared frame fence and retires the inspector context/backends, even when the first begin fails before the inspector is marked active. This prevents stranded native platform callbacks when the inspector is subsequently hidden and avoids destroying resources still used by a previous GPU frame. A later visible begin recreates the context/backends; cancellation and final disposal remain harmless. No swapchain image has been acquired for the failing frame, so retry does not reuse an unconsumed image-available semaphore signal. Cleanup failures preserve the original exception through `ResourceLifetime` aggregation. This is not recovery from violations of the UI-scope or lifetime contract.

The Vulkan package remains independent of ImGui; this is a small extension to the optional inspector, not a public render-plugin framework. Capture is still sampled during `BeginOverlayFrame`, now after application UI is emitted. Diagnostic evidence semantics are unchanged: application windows contribute draw indices but are not counted as the diagnostics window/image.

## Diagnostics layout and session state

Application controls and kit diagnostics remain separate windows. Diagnostics has native titlebar dragging, edge resizing and collapsing. `InspectorSettings.Left/Top/Width/Height` supply **initial physical-pixel bounds**, not per-frame constraints. Font size remains physical pixels. The inspector retains its latest bounds, whole-window collapse and Frame history/Preview/Rendering/Text cache/Font open choices in the managed instance across hide/show, resize and context retirement. There is no disk settings file, new configuration layer or application-owned context management. A new inspector instance starts from settings again.

Consumers own persistence of their application windows and view state, including whether diagnostics is shown. Hiding the inspector, swapchain recreation and failure retirement may destroy its native ImGui context. Native-only application window positions, collapse choices and widget state therefore do not survive automatically. Retain the state the application needs and reapply it when creating its UI; choose storage format/save policy in the application if disk persistence is wanted. This does not erase application-managed state. Do not manage the kit's context to implement persistence; the kit does not silently enable `imgui.ini`, mutate startup settings, or display persistence warnings on screen.

When framebuffer size or DPI changes, bounds are converted at the ImGui boundary and recovered inside the available display. The window shrinks only when necessary; it does not snap back to JSON bounds each frame. Position and expanded size are retained even when the titlebar is collapsed. Collapsed bounds recovery uses the native titlebar height, so the titlebar can stay near the bottom of the display; expanding immediately recovers the full body inside the display. Backend ownership, shared-fence retirement and texture lifetimes are unchanged.

State, wrapped details and frame milliseconds are always outside collapsible sections. If supplied, Frame history starts open and the explicitly opted-in Preview starts open; Rendering, Text cache and Font start closed. Two-column tables wrap labels and values, including long font names and details. The technical sections retain shape/overlay frame counts, geometry counts, text-cache counters, font debug name, logical/physical font size, framebuffer scale and raster density. Preview scales down to fit both available width and body height. A clipped/closed preview does not initiate an upload; a clipped open preview reserves the same layout extent with native `Dummy`, keeping content height and scroll range stable. Image evidence still requires actual emitted image indices and visible geometry, not a forced flag. Application-specific key bindings/hints do not belong in this reusable UI.

### Optional diagnostic image

A PNG is a diagnostic/sample fixture, not a prerequisite for controls. Supply `new InspectorPreviewSettings(imagePath, imageSize, maxTextureDimension, maxEncodedImageBytes)` as `preview:` to opt in. It validates the asset header and explicit bounds before native startup. CPU decode and the straight-linear image copy occur only when the preview has visible space; clipped/closed previews do not decode, scan or upload. Coverage is computed once for the privately owned immutable image and retained across context retirement, avoiding per-frame image-sized scans. GPU references still retire at the existing shared-fence boundary. Transparent previews reserve their layout extent but never claim image evidence.

### Optional frame-time history

`FrameTimeHistory(capacity)` has a fixed capacity of 1–65,536 samples and no native resources. `Observe(nonnegativeObservationId, milliseconds)` accepts finite, nonnegative, float-representable durations; it returns false for duplicate/older IDs. IDs must increase within an application-defined observation epoch. Retrying/cancelling UI or presentation does not collect anything: call `Observe` outside drawing once per actual host observation. `Clear()` empties samples and starts a new ID epoch. At capacity, observations overwrite the oldest sample without deferred work or allocation.

The history is owner-thread mutable state, not a cross-thread publication. The host owns measurement provenance: use elapsed frame time for the supplied diagnostics view, not unlabeled GPU timestamps or simulation-step durations. Min/mean/max scan valid samples only, once per changed revision. Plot scratch is private and bounded by capacity; it invalidates on revision or available plot width. Dense chronological buckets preserve min/max in their original within-bucket order, keeping spikes while bounding the number of native plot points by horizontal resolution. `CopyEnvelope(Span<float>)` writes into caller-owned storage when a consumer needs the same representation; no mutable ring aliases escape. Hidden/clipped history avoids graph generation, but required host observations remain the host's responsibility.

### Published API reuse audit

The disposable net10.0 compile probe referenced exact `Ahwoo.BRUTAL.ImGui` **[2026.10.3]**, restored from nuget.org (`https://api.nuget.org/v3/index.json`; archive `https://api.nuget.org/v3-flatcontainer/ahwoo.brutal.imgui/2026.10.3/ahwoo.brutal.imgui.2026.10.3.nupkg`), using this repository's SDK pin. It built successfully with zero warnings/errors. This is published-package compile evidence, not source-cache inspection. Relevant locked dependencies: Core 2026.10.4, ImGui.Native/Localization 2026.10.3, Numerics/Memory/Strings 2026.10.2, CommunityToolkit.HighPerformance 8.4.2. Backends remains 2026.10.3.

All symbols below are public, on `Brutal.ImGuiApi.ImGui` (flags/conditions in `Brutal.ImGuiApi`):

| Capability | Published package/version | Symbol/compile evidence | Decision | Gap |
| --- | --- | --- | --- | --- |
| Native geometry and collapse | Ahwoo.BRUTAL.ImGui 2026.10.3 | `SetNextWindowPos(float2, ImGuiCond)`, `SetNextWindowSize(float2, ImGuiCond)`, `SetNextWindowCollapsed(bool, ImGuiCond)`, `Begin(UTF8, ImGuiWindowFlags.NoSavedSettings)`, `GetWindowPos()`, `GetWindowSize()`, `IsWindowCollapsed()`, `GetFrameHeight(): float`, `SetWindowPos(float2)`, `End()` | Reuse native interaction; omit NoMove/NoResize/NoCollapse | Ini is disabled and native state dies with context; retain a few instance fields and clamp only when needed |
| Readable rows | Same | `BeginTable(UTF8, 2, ImGuiTableFlags.SizingStretchProp)`, `TableSetupColumn(UTF8, ImGuiTableColumnFlags.WidthStretch, float)`, `TableNextRow()`, `TableNextColumn()`, `TextWrapped(UTF8)`, `EndTable()` | Reuse tables/wrapping; EndTable in finally | No new framework |
| Disclosure sections | Same | `SetNextItemOpen(bool, ImGuiCond.Always)`, `CollapsingHeader(UTF8)` | Retain returned open choice in instance fields | Native context storage alone cannot survive retirement |
| Clipped preview | Same | `IsRectVisible(float2)`, `Dummy(float2)` (plus existing Image/draw-list index counting) | Native clipping plus display intersection, bounded scale and retained clipped extent | CPU compile does not verify GPU visibility/upload |

A diagnostic-history net10.0 compile probe against exact `Ahwoo.BRUTAL.ImGui [2026.10.3]` from the same nuget.org source also built with zero warnings/errors. It verified public `PlotLines(ImString, ReadOnlySpan<float>, int, ImString, float, float, float2?, int)` and `SetItemTooltip(ImString)`. Reuse decision: native plotting/tooltip rendering; gap: application observation IDs, bounded ring ownership and spike-preserving envelope reduction remain small managed helpers. Compilation establishes API shape, not GPU or visual correctness. Probe transitive versions match the locked dependency family listed above.

A follow-up disposable exact-package net10.0 probe also compiled `GetFrameHeight()`, `SetWindowPos(float2)` and `Dummy(float2)` successfully (zero warnings/errors). The CPU scroll regression additionally uses published `GetScrollY()`, `SetScrollY(float)` and `GetScrollMaxY()` verified by that probe; no native/GPU probe executable was run.

## Validation boundary

The focused `tests/ImGui` suite compiles against the pinned published `Ahwoo.BRUTAL.ImGui` 2026.10.3 API and uses native CPU-only contexts/frames, without GLFW windows, a Vulkan device or texture uploads. It checks the optional constructor, callback timing/context, own-window drawing, null behavior, exception propagation, cancellation, retirement and next-frame context recreation. It also checks unrestricted window flags, no normal-frame geometry override, physical bounds recovery across smaller displays/DPI/context recreation, retained collapse/section choices, narrow wrapped tables, table/window scope balancing after row-formatting exceptions and independent window scopes. CPU preview tests use an already-uploaded texture stand-in (no device) to require actual image indices at 960×540, 1280×720 and narrow widths, and verify no upload attempt without available body space. Regressions retain a bottom-positioned collapsed titlebar over normal frames and context retirement, recover expanded bounds immediately, and exercise multiple shown/scrolled-out preview frames with stable widget height, content extent and scroll range and no clipped upload. Additional CPU regressions cover controls-only settings without a PNG, independently hidden diagnostics, no automatic history collection during drawing, cached immutable coverage across retirement, chronological overwrite, ID deduplication, clear epochs, flat/empty/one-sample histories, invalid observations and dense spike-preserving envelopes. This does not establish GLFW input routing, GPU presentation or visible UI quality.
