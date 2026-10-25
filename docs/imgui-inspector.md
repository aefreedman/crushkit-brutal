# ImGui inspector application controls

`CrushKit.Brutal.ImGui` provides the sealed `ImGuiInspector`:

```csharp
public ImGuiInspector(InspectorSettings settings, Action? drawControls = null)
```

Pass it to `renderer.ConfigureOverlay(textSettings, inspector)`; ownership transfers to the renderer. The one-argument call (or a null callback) retains diagnostics-only behavior. Settings and callback are retained, not copied. Construction does not invoke the callback or initialize native resources.

When the inspector is visible and its submitted overlay frame is prepared for rendering, the callback runs synchronously on the renderer thread, once per prepared inspector frame. Its own ImGui context and frame are current/active. The diagnostics window has already ended (`Ui.End()`), and `Ui.Render()` has not run. It is not called for hidden, disabled, unsubmitted/cancelled inspector frames. It is not an input-polling or simulation-update hook.

## UI-only contract

- Emit application UI using `Brutal.ImGuiApi.ImGui`, including your own windows. Own your labels, state and policy. Use normal ImGui logical coordinates (inspector layout settings alone are physical pixels).
- Balance every Begin/End, Push/Pop and other UI scope, **even when throwing**, using `try/finally` where needed.
- Do not call `NewFrame`, `Render` or `EndFrame`. Do not create, switch, destroy or otherwise manage ImGui contexts/backends. Do not dispose or reconfigure the renderer/inspector, or re-enter rendering.
- Do not retain borrowed ImGui frame/draw data. Do not perform simulation steps, blocking work or GPU/resource-lifetime operations in the callback. Widgets may update application-owned UI state or enqueue commands; apply game policy outside drawing.

A callback exception propagates to the caller after the inspector cancels its active frame via the existing `Cancel`/`EndFrame` path. A subsequent cancellation is harmless, and the next frame can start normally. The inspector keeps ownership of its context/backends for normal reuse and fence/device-idle retirement; exceptions do not transfer or abandon ownership. This is not recovery from violations of the UI-scope or lifetime contract.

The Vulkan package remains independent of ImGui; this is a small extension to the optional inspector, not a public render-plugin framework. Existing capture timing and diagnostic evidence semantics are unchanged: application windows contribute draw indices but are not counted as the diagnostics window/image.

## Validation boundary

The focused `tests/ImGui` suite compiles against the pinned published `Ahwoo.BRUTAL.ImGui` 2026.10.3 API and uses native CPU-only contexts/frames, without GLFW windows, a Vulkan device or texture uploads. It checks the optional constructor, callback timing/context, own-window drawing, null behavior, exception propagation, cancellation and next-frame reuse. This does not establish GPU presentation or visible UI quality.
