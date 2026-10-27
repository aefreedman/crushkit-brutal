# Opt-in inspector theme and semantic emphasis

`InspectorTheme` is a small immutable style recipe, not a global theme manager or a settings file. A consumer supplies its own palette and font-relative dimensions, validates its authoritative configuration before native startup, and passes the same objects to the kit. Existing inspector constructors leave appearance unchanged.

```csharp
var theme = new InspectorTheme(loadedColors, loadedMetrics);
var inspector = new ImGuiInspector(settings, DrawApplicationControls, frameHistory, theme);
renderer.ConfigureOverlay(textSettings, inspector);
```

The original `(InspectorSettings, Action?)` and `(InspectorSettings, Action?, FrameTimeHistory?)` CLR constructor signatures remain present. The distinct four-argument overload adds a nullable `InspectorTheme`; null preserves the opt-out behavior. `inspector.Theme`, `theme.Colors` and `theme.Metrics` retain the supplied instances rather than copying, merging or applying hidden game defaults. Constructors perform no native initialization. The existing [callback/lifetime contract](imgui-inspector.md) remains in force.

## Explicit colors and metrics

`InspectorThemeColors` takes twelve `Brutal.Numerics.float4` tokens, in this order (named arguments are encouraged):

- `text`, `mutedText`: primary and supporting text, including the native disabled-text slot.
- `window`, `surface`, `popup`, `border`: window/child background, framed controls and section surfaces, popup background and separators/borders.
- `accent`, `accentHovered`, `accentActive`: control, hover and active/selected accents across buttons, headers, grabs, navigation cursor, plots and tabs.
- `success`, `warning`, `error`: explicit semantic text emphasis, used only when a consumer requests the corresponding tone.

Every component must be finite and in `[0,1]`. These are **native ImGui normalized straight-RGBA values**, passed unchanged. The helper does not interpret them as PNG bytes, linearize them, premultiply RGB, change the framebuffer format, or alter renderer blending. Consumer palette selection and contrast remain intentional design work; CPU tests cannot establish visible contrast or color fidelity. Semantic color must supplement words/icons, not replace the warning/error message itself.

`InspectorThemeMetrics` takes:

| Input | Meaning |
| --- | --- |
| `windowPaddingEm`, `framePaddingEm` | Two-dimensional window and framed-control padding |
| `itemSpacingEm`, `innerSpacingEm`, `cellPaddingEm` | Two-dimensional item, composite-control and table-cell spacing |
| `roundingEm`, `borderEm` | Common corner rounding and window/child/popup/frame border thickness |
| `scrollbarEm`, `grabMinEm`, `indentEm` | Scrollbar width, minimum grab dimension and indentation |
| `disabledAlpha` | Native disabled-state alpha multiplier, not an em dimension |

All em components are finite and in `[0,16]`; scrollbar/grab dimensions must also be positive. Disabled alpha is finite and in `[0,1]`. These are implementation bounds, not duplicated application defaults. Each dimension is assigned as `suppliedEm * logicalFontSize`, never by scaling the previous native style. Text size and font roles remain the inspector's existing physical-pixel policy; the theme neither selects nor scales fonts.

This is deliberately not exhaustive native-style serialization. It applies common window/control/interaction slots and dimensions; native fields outside that set retain their normal behavior. There is no style registry, saved layout, schema introspection, font collection, or additional graphics dependency.

## Context and DPI behavior

The inspector applies its theme before `NewFrame`, after the platform backend has supplied framebuffer scale. It computes logical font size through the existing font policy and only rewrites theme style when:

- the theme has not yet been applied to the current owned context; or
- the logical font size changes, including a framebuffer-scale change.

Context retirement invalidates the cache even if a later context reuses the same native address. Ordinary frames do constant comparison work, not palette/metric rebuilding. Reapplication assigns absolute values, so repeated hide/show, resize or DPI transitions do not accumulate rounding/padding growth. The immutable recipe remains managed state with no native lifetime to retire.

`theme.Apply(logicalFontSize)` is also public for an application-owned CPU/native context. Call it before `NewFrame`, with a current context and no active style scopes. The size must be finite, positive and at most `float.MaxValue / 16`, so all validated dimensions remain representable. An inspector consumer should normally let the inspector perform this work instead of applying every callback. Do not create/switch/manage the inspector's context to apply a theme.

Application drawing may use normal temporary style scopes. Persistent direct style edits can be superseded at the next theme invalidation; the supplied recipe remains authoritative. Runtime theme replacement is not an implicit mutable settings channel.

## Semantic text

```csharp
theme.TextWrapped("Source saved; runtime reset remains pending."u8, InspectorTone.Secondary);
using (var warning = theme.PushTone(InspectorTone.Warning))
{
    Ui.TextWrapped("Reset required before structural changes take effect."u8);
}
```

`InspectorTone` has `Primary`, `Secondary`, `Success`, `Warning` and `Error`. `PushTone` pushes exactly one native text color and returns a borrowed `InspectorToneScope`. Use a local `using` so it unwinds if drawing throws; do not copy/retain the scope, manually pop it, or switch contexts before disposing. Disposing the same local twice is harmless. `TextWrapped` borrows UTF-8 text synchronously and balances its scope internally. Native disabled scopes continue to work; disabled explanations and application state remain separate concerns.

## Published API evidence and validation boundary

A disposable net10.0 compile-only probe used the kit's SDK pin and exact `Ahwoo.BRUTAL.ImGui [2026.10.3]` from `https://api.nuget.org/v3/index.json`. Restore succeeded; the final probe build succeeded with zero warnings/errors. No native probe executable was run. Relevant locked dependencies are ImGui.Native/Localization 2026.10.3, Core 2026.10.4, Numerics/Memory/Strings 2026.10.2 and CommunityToolkit.HighPerformance 8.4.2.

| Capability | Compiled published symbols | Reuse / gap |
| --- | --- | --- |
| Native palette | `Brutal.ImGuiApi.ImGui.GetStyle()`, public `Colors[(int)ImGuiCol]` inline-array access, common text/window/frame/button/header/grab/scrollbar/tab/table/plot/navigation slots | Reuse native style storage; remaining code maps explicitly supplied immutable semantic tokens. The enum must be cast to an integer for the inline-array index. |
| Absolute dimensions | Public `WindowPadding`, `FramePadding`, `ItemSpacing`, `ItemInnerSpacing`, `CellPadding`, window/child/popup/frame rounding and borders, `ScrollbarRounding`, `GrabRounding`, `ScrollbarSize`, `GrabMinSize`, `IndentSpacing`, `DisabledAlpha` | Reuse native dimensions; the managed recipe validates input and recomputes absolute font-relative values on invalidation. |
| Semantic text scope | `ImGui.PushStyleColor(ImGuiCol.Text, float4)`, `PopStyleColor()`, `TextWrapped(UTF8)` | Reuse native style/text rendering; a borrowed scope only ensures balanced composition. |

Focused CPU-native tests cover input validation for every color/component and metric, opt-out behavior, exact constructor signatures, same-object retention, all mapped interaction colors, absolute dimensions across repeated scale changes, cache invalidation on context retirement, and nested/throwing/disabled semantic scopes. They do not establish GLFW input, GPU presentation, visible appearance, contrast, or font/DPI quality.

The application chooses its authoritative JSON tokens/metrics. Work scales with a fixed set of style assignments per invalidation; storage is the three immutable managed objects and two scalar inspector cache fields. No per-frame arrays, configuration parsing, mutable publications or growing scratch are introduced.
