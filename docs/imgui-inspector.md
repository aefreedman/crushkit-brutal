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

## Validation boundary

The focused `tests/ImGui` suite compiles against the pinned published `Ahwoo.BRUTAL.ImGui` 2026.10.3 API and uses native CPU-only contexts/frames, without GLFW windows, a Vulkan device or texture uploads. It checks the optional constructor, callback timing/context, own-window drawing, null behavior, exception propagation, cancellation, retirement and next-frame context recreation. This does not establish GPU presentation or visible UI quality.
