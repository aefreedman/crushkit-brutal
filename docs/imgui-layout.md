# Opt-in tool layouts

`CrushKit.Brutal.ImGui.ToolLayout` composes ordinary public ImGui tables and widgets. It does not create windows, own configuration documents, store edits, choose persistence policy, or replace the native controls. Use it inside the inspector's application callback; the existing [callback and lifetime contract](imgui-inspector.md) still applies.

## Composition

Keep categories and selection in application state. Construct `ToolCategory` objects once: they retain stable IDs, display labels and their UTF-8 encodings. Category IDs must be unique within the panel. If the application reorders/removes categories, it must update its selected index to match; an invalid index is rejected rather than silently selecting a different page.

A wide panel shows category navigation beside the selected body. A narrow panel shows a native category selector above the body, including a wrapped selected-category title so an elided combo preview is not the only source of its name. Only the application-selected body is emitted. The threshold follows the measured navigation labels, native style padding and the explicitly supplied minimum content width. No global pixel breakpoint is stored.

`PropertyRow` reserves equal minimum label/control widths when both fit; otherwise it stacks the control below its wrapped label. Pass the same minimum control width for sibling rows to align their controls, independent of label lengths. Use stable field paths as IDs and hidden widget IDs such as `##value`. Help attaches to the label. Validation failures and important warnings should also be visible text, not tooltip-only.

Both methods return a borrowed `ToolLayoutScope`. Always dispose it, even when `ContentVisible` is false. Emit the body/control only when true. Use a local `using`; do not copy a scope, retain it across frames, manually end its table, or change contexts inside it. Scope disposal balances native table and ID stacks if application drawing throws. The one-column and two-column modes preserve table identity so native widget identity does not change merely because the window gets narrower.

`Heading` emits a section heading and optional wrapped explanation. `SameLineIfFits` keeps ordinary action buttons compact while allowing them to wrap. Applications own action enablement, status text and the location of their action area; button actions should enqueue requests for the owner update, not perform I/O or resource changes while drawing. Independent scrolling is ordinary native ImGui composition owned by the application, not a hidden child-window lifecycle in the template.

## Small consumer example

This snippet lives in an application object; it is not a JSON editor or persistence service. The callback is synchronous on the renderer thread.

```csharp
using Brutal.ImGuiApi;
using CrushKit.Brutal.ImGui;
using Ui = Brutal.ImGuiApi.ImGui;

private readonly ToolCategory[] categories =
[
    new("play", "Gameplay"),
    new("render", "Rendering")
];
private int selectedCategory;
private float speed;
private bool requestSave;

private void DrawControls()
{
    // Application supplies/retains its window position and size as needed.
    if (Ui.Begin("Tools"u8, ImGuiWindowFlags.NoSavedSettings))
    {
        try
        {
            ToolLayout.Heading("Settings"u8, "Edits are staged; Save writes the selected source."u8);
            {
                using var body = ToolLayout.Categories("settings"u8, categories,
                    ref selectedCategory, Ui.GetFontSize() * 24);
                if (body.ContentVisible && selectedCategory == 0)
                {
                    using var row = ToolLayout.PropertyRow("gameplay.speed"u8, "Speed (units/s)"u8,
                        Ui.GetFontSize() * 12, "Applied by the next owner update."u8);
                    if (row.ContentVisible) Ui.SliderFloat("##value"u8, ref speed, 0, 100);
                }
                else if (body.ContentVisible)
                    Ui.TextWrapped("Application-owned rendering controls go here."u8);
            }
            Ui.Separator();
            if (Ui.Button("Save"u8)) requestSave = true;
            ToolLayout.SameLineIfFits("Discard"u8);
            if (Ui.Button("Discard"u8)) { /* enqueue an application command */ }
        }
        finally { Ui.End(); }
    }
    else Ui.End(); // Begin/End must also balance for a collapsed window.
}
```

The font-relative widths here are choices made by the example application, not kit defaults. Applications can source their own layout preferences from their authoritative configuration. Category selection and field values are ordinary managed state. Context recreation does not destroy that state, but native window/widget state is not automatically serialized: consumers decide what to retain and reapply. No persistence warning is rendered by the helper.

## Published API reuse

A disposable net10.0 compile probe referenced exactly `Ahwoo.BRUTAL.ImGui [2026.10.3]` from `https://api.nuget.org/v3/index.json`, with the kit SDK pin. Restore and build succeeded with zero warnings/errors. The relevant dependency closure includes ImGui.Native/Localization 2026.10.3, Core 2026.10.4, Numerics/Memory/Strings 2026.10.2 and CommunityToolkit.HighPerformance 8.4.2. No package references were changed.

| Capability | Published package/version | Compiled public symbols on `Brutal.ImGuiApi.ImGui` | Reuse decision / remaining gap |
| --- | --- | --- | --- |
| Responsive navigation/body | Ahwoo.BRUTAL.ImGui 2026.10.3 | `BeginTable`, `TableSetupColumn`, `TableNextRow`, `TableNextColumn`, `EndTable`, `BeginCombo`, `EndCombo`, `Selectable` | Reuse native tables/selector; small composition helper decides one/two columns from current available space. |
| Stable identity | Same | `PushID`, `PopID`, `GetID` | Reuse native ID stack; application supplies unique panel/field/category IDs and owns selection. |
| Aligned wrapped rows/help | Same | `TextWrapped`, `AlignTextToFramePadding`, `SetNextItemWidth`, `SetItemTooltip` | Reuse native formatting; helper pairs labels with caller-emitted controls, without owning their values. |
| Header/action flow | Same | `SeparatorText`, `CalcTextSize`, `GetContentRegionAvail`, `GetCursorScreenPos`, `GetItemRectMax`, `SameLine`, style `FramePadding`, `ItemSpacing`, `CellPadding` | Compose existing primitives; no custom widget, backend or retained layout engine. |

The probe verifies published API shape, not GPU quality. CPU-only native tests exercise wide/narrow composition, long-label wrapping, sibling alignment, exception unwinding, stable IDs across responsive mode changes and application selection across context recreation. They do not establish visible font/DPI quality or real GLFW input behavior.

## Work and lifetime bounds

Each observed panel measures its category labels and emits at most that many navigation widgets plus the selected body; it never traverses hidden category content. Width decisions use the current font/style/available region each frame, so font, scale and viewport changes cannot leave a stale breakpoint cache. Property rows do constant work plus native wrapping of their visible label/help. UTF-8 category storage is owned and fixed at construction; span labels are borrowed for the synchronous call. Helpers retain no native resources, global registry, schema cache, historical frames or growing scratch buffers.
