# ImGui inspector application controls

`CrushKit.Brutal.ImGui` provides the sealed `ImGuiInspector`:

```csharp
public ImGuiInspector(InspectorSettings settings, Action? drawControls = null)
```

Pass it to `renderer.ConfigureOverlay(textSettings, inspector)`; ownership transfers to the renderer. The one-argument call (or a null callback) retains diagnostics-only behavior. Settings and callback are retained, not copied. Construction does not invoke the callback or initialize native resources.

When the inspector is visible, the callback runs synchronously on the renderer thread during `BeginOverlayFrame`, immediately after `Ui.NewFrame()`, before diagnostics are emitted and before any swapchain image acquisition. Its own ImGui context and frame are current/active. It runs once per successfully started inspector frame, even if that frame is later cancelled or never submitted. It is not called for hidden, disabled or minimized inspector frames. It is not an input-polling or simulation-update hook.

## UI-only contract

- Emit application UI using `Brutal.ImGuiApi.ImGui`, including your own windows. Own your labels, state and policy. Use normal ImGui logical coordinates (inspector layout settings alone are physical pixels).
- Balance every Begin/End, Push/Pop and other UI scope, **even when throwing**, using `try/finally` where needed.
- Do not call `NewFrame`, `Render` or `EndFrame`. Do not create, switch, destroy or otherwise manage ImGui contexts/backends. Do not dispose or reconfigure the renderer/inspector, or re-enter rendering.
- Do not retain borrowed ImGui frame/draw data. Do not perform simulation steps, blocking work or GPU/resource-lifetime operations in the callback. Widgets may update application-owned UI state or enqueue commands; apply game policy outside drawing.

A callback exception cancels its active frame via the existing `Cancel`/`EndFrame` path. Before propagating from `BeginOverlayFrame`, the renderer waits the shared frame fence and retires the inspector context/backends, even when the first begin fails before the inspector is marked active. This prevents stranded native platform callbacks when the inspector is subsequently hidden and avoids destroying resources still used by a previous GPU frame. A later visible begin recreates the context/backends; cancellation and final disposal remain harmless. No swapchain image has been acquired for the failing frame, so retry does not reuse an unconsumed image-available semaphore signal. Cleanup failures preserve the original exception through `ResourceLifetime` aggregation. This is not recovery from violations of the UI-scope or lifetime contract.

The Vulkan package remains independent of ImGui; this is a small extension to the optional inspector, not a public render-plugin framework. Capture is still sampled during `BeginOverlayFrame`, now after application UI is emitted. Diagnostic evidence semantics are unchanged: application windows contribute draw indices but are not counted as the diagnostics window/image.

## Diagnostics layout and session state

Application controls and kit diagnostics remain separate windows. Diagnostics has native titlebar dragging, edge resizing and collapsing. `InspectorSettings.Left/Top/Width/Height` supply **initial physical-pixel bounds**, not per-frame constraints. Font size remains physical pixels. The inspector retains its latest bounds, whole-window collapse and Preview/Rendering/Text cache/Font open choices in the managed instance across hide/show, resize and context retirement. There is no disk settings file, new configuration layer or application-owned context management. A new inspector instance starts from settings again.

When framebuffer size or DPI changes, bounds are converted at the ImGui boundary and recovered inside the available display. The window shrinks only when necessary; it does not snap back to JSON bounds each frame. Position and expanded size are retained even when the titlebar is collapsed. Collapsed bounds recovery uses the native titlebar height, so the titlebar can stay near the bottom of the display; expanding immediately recovers the full body inside the display. Backend ownership, shared-fence retirement and texture lifetimes are unchanged.

State, wrapped details and frame milliseconds are always outside collapsible sections. Preview starts open near the top; Rendering, Text cache and Font start closed. Two-column tables wrap labels and values, including long font names and details. The technical sections retain shape/overlay frame counts, geometry counts, text-cache counters, font debug name, logical/physical font size, framebuffer scale and raster density. Preview scales down to fit both available width and body height. A clipped/closed preview does not initiate an upload; a clipped open preview reserves the same layout extent with native `Dummy`, keeping content height and scroll range stable. Image evidence still requires actual emitted image indices and visible geometry, not a forced flag. Application-specific key bindings/hints do not belong in this reusable UI.

### Published API reuse audit

The disposable net10.0 compile probe referenced exact `Ahwoo.BRUTAL.ImGui` **[2026.10.3]**, restored from nuget.org (`https://api.nuget.org/v3/index.json`; archive `https://api.nuget.org/v3-flatcontainer/ahwoo.brutal.imgui/2026.10.3/ahwoo.brutal.imgui.2026.10.3.nupkg`), using this repository's SDK pin. It built successfully with zero warnings/errors. This is published-package compile evidence, not source-cache inspection. Relevant locked dependencies: Core 2026.10.4, ImGui.Native/Localization 2026.10.3, Numerics/Memory/Strings 2026.10.2, CommunityToolkit.HighPerformance 8.4.2. Backends remains 2026.10.3.

All symbols below are public, on `Brutal.ImGuiApi.ImGui` (flags/conditions in `Brutal.ImGuiApi`):

| Capability | Published package/version | Symbol/compile evidence | Decision | Gap |
| --- | --- | --- | --- | --- |
| Native geometry and collapse | Ahwoo.BRUTAL.ImGui 2026.10.3 | `SetNextWindowPos(float2, ImGuiCond)`, `SetNextWindowSize(float2, ImGuiCond)`, `SetNextWindowCollapsed(bool, ImGuiCond)`, `Begin(UTF8, ImGuiWindowFlags.NoSavedSettings)`, `GetWindowPos()`, `GetWindowSize()`, `IsWindowCollapsed()`, `GetFrameHeight(): float`, `SetWindowPos(float2)`, `End()` | Reuse native interaction; omit NoMove/NoResize/NoCollapse | Ini is disabled and native state dies with context; retain a few instance fields and clamp only when needed |
| Readable rows | Same | `BeginTable(UTF8, 2, ImGuiTableFlags.SizingStretchProp)`, `TableSetupColumn(UTF8, ImGuiTableColumnFlags.WidthStretch, float)`, `TableNextRow()`, `TableNextColumn()`, `TextWrapped(UTF8)`, `EndTable()` | Reuse tables/wrapping; EndTable in finally | No new framework |
| Disclosure sections | Same | `SetNextItemOpen(bool, ImGuiCond.Always)`, `CollapsingHeader(UTF8)` | Retain returned open choice in instance fields | Native context storage alone cannot survive retirement |
| Clipped preview | Same | `IsRectVisible(float2)`, `Dummy(float2)` (plus existing Image/draw-list index counting) | Native clipping plus display intersection, bounded scale and retained clipped extent | CPU compile does not verify GPU visibility/upload |

A follow-up disposable exact-package net10.0 probe also compiled `GetFrameHeight()`, `SetWindowPos(float2)` and `Dummy(float2)` successfully (zero warnings/errors). The CPU scroll regression additionally uses published `GetScrollY()`, `SetScrollY(float)` and `GetScrollMaxY()` verified by that probe; no native/GPU probe executable was run.

## Validation boundary

The focused `tests/ImGui` suite compiles against the pinned published `Ahwoo.BRUTAL.ImGui` 2026.10.3 API and uses native CPU-only contexts/frames, without GLFW windows, a Vulkan device or texture uploads. It checks the optional constructor, callback timing/context, own-window drawing, null behavior, exception propagation, cancellation, retirement and next-frame context recreation. It also checks unrestricted window flags, no normal-frame geometry override, physical bounds recovery across smaller displays/DPI/context recreation, retained collapse/section choices, narrow wrapped tables, table/window scope balancing after row-formatting exceptions and independent window scopes. CPU preview tests use an already-uploaded texture stand-in (no device) to require actual image indices at 960×540, 1280×720 and narrow widths, and verify no upload attempt without available body space. Regressions retain a bottom-positioned collapsed titlebar over normal frames and context retirement, recover expanded bounds immediately, and exercise multiple shown/scrolled-out preview frames with stable widget height, content extent and scroll range and no clipped upload. This does not establish GPU presentation or visible UI quality.
