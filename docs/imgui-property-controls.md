# Property controls, help and field actions

These opt-in helpers compose the published BRUTAL native controls inside an application-owned window/property row. They do not infer schema types, supply game defaults, read configuration files, own values, apply simulation changes or manage ImGui frames/contexts. The existing [inspector callback contract](imgui-inspector.md) and [layout scope contract](imgui-layout.md) still apply.

## Cached descriptions and quieter metadata

Construct `ToolProperty(id, label, units, effect, help)` once for a stable field description. It retains owned UTF-8 identity, primary label and subdued metadata. `ToolLayout.PropertyRow(property, minimumControlWidth)` aligns/stacks like the existing span-taking overload, which remains unchanged. Units and effect appear as secondary text rather than being concatenated into the primary label. The application supplies their wording; the kit has no LIVE/RESET schema.

`ToolHelp(title, description, interaction, disabledReason)` stores structured text once. `ShowForLastItem(disabled)` reuses native tooltip timing and disabled-item hover; `DrawContent(disabled)` supports an application-owned help surface. A disabled reason is included only when explicitly requested. `DrawDisabledReason()` emits the same explanation visibly. Important failures must remain visible application state, not tooltip-only information.

Numeric helpers accept `enabled` and optional help. They disable only the value widget, then restore the native disabled scope before help/actions; unavailable controls retain their value, can explain why, and do not disable a following recovery button. The row itself never silently disables its contents.

## Explicit numeric presentation

| Description | Factories | Storage / behavior |
| --- | --- | --- |
| `ToolDoubleEditor` | `Input(format)`, `Drag(minimum, maximum, speed, format)`, `Slider(minimum, maximum, format)` | Native `Double`, never a float intermediate. Formats are one safe numeric conversion: `%.0f` through `%.17f`, `%.1g` through `%.17g`, or `%.0e` through `%.17e`. Units belong in metadata, not a printf format. |
| `ToolInt64Editor` | `Input()`, `Drag(minimum, maximum, speed)`, `Slider(minimum, maximum)` | Native signed `S64`, including exact values above 2^53. Input supports the full Int64 range. Native bounded controls require ordered endpoints inside the signed half-range; use Input for larger domains. |

Call `ToolControls.EditDouble(id, ref value, editor, enabled, help, inputFlags)` or `EditInt64` and stage the value only when it returns true. The caller still validates finite/range/integer/application constraints and publishes changes at its owner update. Do not infer integer schema from a JSON number's spelling.

Drag/slider bounds are useful **interaction bounds**, not a replacement validator. Native Ctrl-click exact entry deliberately remains unclamped, and an unedited value outside the useful range is not silently normalized. A native slider can have coarser pointer resolution over a huge domain; exact text entry remains available. `speed` is the native float drag rate, not the value's storage type. Double descriptors reject nonfinite/difference-overflow ranges and native slider half-range violations before drawing.

`ImGuiInputTextFlags` apply only to Input descriptors. Passing them to a drag/slider descriptor throws before entering native scopes instead of pretending that `EnterReturnsTrue` changes immediate dragging semantics. Use Input for fields whose host intentionally commits only on Enter, such as physical window dimensions. Float/nonfinite input policy remains application-owned.

## Discoverable actions and presets

`ToolFieldActions.ReserveControlWidth()` reserves a native-style-sized button slot for the next value widget. In insufficient space the visible action button wraps below it. After the widget, call:

```csharp
ToolFieldAction request = ToolFieldActions.Draw(property.IdUtf8,
    canRestore: hasSourceValue, canCopy: true, presets: presetLabels);
```

The visible `...` button opens a native popup. Optional right-click targets the preceding item; it is an additional shortcut, never the only way to find actions. The result is `None`, `Restore`, `Copy`, or `Preset`. `PresetIndex` is meaningful only for `Preset`, and refers to the supplied order for this call. `ToolFieldPreset` owns a cached display label only; values, stable semantic keys and commands stay in the application. Duplicate preset captions have distinct native index scopes.

The helper does not restore defaults, choose a selected source, save, write the clipboard or execute commands. Restore/copy availability is explicit. Map a returned preset index immediately to the application's value/key before queueing work; do not retain the index across a changing list. Source recovery and factory defaults must be labeled/handled distinctly by the consumer. Supply `restoreLabel: "Restore from source"u8` (or another truthful application caption) to distinguish the source of recovery; the default generic caption is "Restore value".

## Small composition example

The range/precision/presets below are illustration choices, not kit defaults. An application's configuration/schema owns its real choices.

```csharp
private readonly ToolProperty speedProperty = new("play.speed", "Speed", "units/s", "Live",
    new ToolHelp("Movement speed", "Applied by the next owner update.",
        "Drag to adjust; Ctrl-click for exact entry.", "Resume to edit."));
private readonly ToolDoubleEditor speedEditor = ToolDoubleEditor.Drag(0, 2000, 1, "%.1f");
private readonly ToolFieldPreset[] speedPresets = [new("Slow"), new("Fast")];

private void DrawSpeed()
{
    using var row = ToolLayout.PropertyRow(speedProperty, Ui.GetFontSize() * 8);
    if (!row.ContentVisible) return;
    ToolFieldActions.ReserveControlWidth();
    if (ToolControls.EditDouble("##value"u8, ref speed, speedEditor, enabled: !paused,
        help: speedProperty.Help)) StageSpeed(speed);
    var action = ToolFieldActions.Draw(speedProperty.IdUtf8, hasSourceSpeed, true, speedPresets);
    // Translate into application-owned commands or draft edits; no file/resource I/O here.
    HandleSpeedAction(action);
}
```

The application owns the fields/methods referenced by the snippet. Keep per-field source snapshots and preset values outside the kit. Commands that save/reload/reset/resources still execute outside the UI callback.

## Published API audit

A disposable net10.0 probe used the repository's .NET SDK 10.0.201 pin and exact `Ahwoo.BRUTAL.ImGui [2026.10.3]` from `https://api.nuget.org/v3/index.json` (archive: `https://api.nuget.org/v3-flatcontainer/ahwoo.brutal.imgui/2026.10.3/ahwoo.brutal.imgui.2026.10.3.nupkg`). Restore/build succeeded with zero warnings/errors; the probe executable was not run. Relevant locked family: Core 2026.10.4, ImGui.Native/Localization 2026.10.3, Numerics/Memory/Strings 2026.10.2, CommunityToolkit.HighPerformance 8.4.2. Product Backends remains the existing 2026.10.3 dependency; no dependency references were changed.

| Capability | Compiled public API on `Brutal.ImGuiApi.ImGui` | Reuse / verified gap |
| --- | --- | --- |
| Exact native numeric storage | `InputScalar(ImString, ImGuiDataType, Brutal.Pointers.Ptr, Ptr, Ptr, ImString, ImGuiInputTextFlags)` with `Double` and `S64` | Reuse native editing/parsing. Small managed descriptors validate/cache presentation; no custom numeric input engine. |
| Quick adjustment with exact input | `DragScalar(ImString, ImGuiDataType, Ptr, float, Ptr, Ptr, ImString, ImGuiSliderFlags)`, `SliderScalar(ImString, ImGuiDataType, Ptr, Ptr, Ptr, ImString, ImGuiSliderFlags)` | Reuse native dragging/sliding and Ctrl-click entry, not float conversion or guessed clamping. |
| Disabled explanations / rich help | `BeginDisabled`, `EndDisabled`, `IsItemHovered(ForTooltip \| AllowWhenDisabled)`, `BeginTooltip`, `EndTooltip`, `TextWrapped`, `PushStyleColor`, `PopStyleColor`, style `Colors[TextDisabled]` | Reuse native disabled state, hover delay and styling. The helper composes caller-authored content only. |
| Visible/context actions | `SmallButton`, `IsItemClicked(Right)`, `OpenPopup`, `BeginPopup`, `MenuItem`, `EndPopup` | Reuse native popup/item interactions. Requests and source/preset semantics remain separate. |

Compilation proves API shape, not visual/GPU correctness. Native CPU tests additionally exercise exact S64 entry above double precision, slider editing, drag Ctrl-click exact entry outside the useful range, disabled help, narrow/wide cached metadata, visible action selection, repeated preset captions, and exception scope cleanup. These tests use offscreen CPU contexts, not GLFW windows, GPU resources or visible desktop interaction.

## Work and ownership bounds

Descriptors and help own immutable strings/UTF-8 allocated at construction. Drawing has no encoding, schema parsing or retained scratch buffers. Numeric work is constant per emitted control; native text wrapping follows the supplied visible text. Popup preset traversal happens only while its popup is open, scales with caller-supplied entries and retains no preset list/value alias. No native borrowed pointers escape the synchronous scalar call. Window/table/ID/disabled/style/tooltip/popup scopes balance on exceptions; callers still use local `using` for returned property-row scopes. Descriptions contain no global caches or cross-thread mutable state.
