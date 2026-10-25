# CrushKit BRUTAL

A small, composable C# game kit built on published BRUTAL packages. This is the BRUTAL successor to the older Unity-era CrushKit idea, not a rewrite of that repository or a full scene/entity engine.

**Local prerelease only.** These packages are not published. Source licence selection, source/asset ownership review, exact public-tree/history review and publication approval remain pending. No open-source licence grant or release clearance is implied.

## Modules

| Package / namespace | Responsibility |
| --- | --- |
| `CrushKit.Brutal.Draw2D` | Headless draw lists, tessellation, transforms, clipping, colors, fills, outlines and patterns |
| `CrushKit.Brutal.Vulkan` | Direct Vulkan/GLFW presentation, frame uploads, embedded shaders, bounded SDL.Ttf label cache and textured quads |
| `CrushKit.Brutal.ImGui` | Optional read-only inspector, supplied explicitly by the application |

Vulkan depends on Draw2D, not ImGui. ImGui depends on Vulkan. None depends on Breakout, its simulation, settings JSON or assets. All modules currently share version `0.1.0-preview.1`; there is no umbrella package. Input, audio and simulation remain application-owned.

The renderer's optional inspector seam is intentionally kit-internal, not a public arbitrary render-plugin API. The provided `ImGuiInspector` transfers ownership to the renderer when passed to `ConfigureOverlay`; it retires at the shared fence/device-idle boundaries. Overlay label content, diagnostic state/detail strings, layout settings, fonts and images are supplied by the application. The kit ships no game fonts, media or default configuration.

Current configuration limitation: `OverlaySettings` is still a composite of text and inspector settings. Even when no ImGui adapter is supplied, it validates the inspector font, PNG and layout fields. ImGui is dependency-optional, but text-only configuration is not yet independent. Separating these immutable settings is a follow-up before promising independently configurable text/inspector services.

## Requirements and limits

- .NET 10 SDK, pinned by `global.json`.
- Windows is the tested native/runtime platform. No other-platform compatibility claim.
- Vulkan 1.3, a combined graphics/present queue, sRGB BGRA8/RGBA8 surface, and blendable 4x-MSAA color attachments. Unsupported configurations fail; there is no lower-MSAA/non-sRGB fallback.
- Hidden GPU tests require installed Vulkan SDK validation layers. Normal players need the driver runtime, not the SDK. ShaderC compiles embedded GLSL at startup; no external shader compiler command is needed.
- One synchronous renderer thread and one fence-protected upload frame. Dispose the renderer before terminating GLFW. Host owns OS event pumping and minimized-window event waits.

`ShapeFrame` buffers are borrowed until the producer's next `BeginFrame` or disposal: consume immediately, never queue or retain them. Coordinates/scissors are physical framebuffer pixels, top-left origin, +Y down. Input color is straight linear RGBA; vertices/textures premultiply once, blending uses ONE / ONE_MINUS_SRC_ALPHA and the sRGB attachment encodes once. Shape and UV vertices retain their separate 24-byte and 32-byte ABIs.

See the [Draw2D API reference](docs/draw2d.md) for concrete primitive/stroke usage and detailed geometry limits.

The existing shape geometry restrictions remain: convex closed outlines, bounded curve/pattern complexity, float32 representability checks and transactional rejection rather than general path union or silent repair. The extraction does not expand geometric capabilities or introduce another graphics backend.

## Build, test and pack

From this repository:

```powershell
dotnet restore CrushKit.slnx --locked-mode
dotnet build CrushKit.slnx --no-restore
dotnet test tests/Draw2D/Draw2D.Tests.csproj --no-restore
dotnet test tests/Vulkan/Vulkan.Tests.csproj --no-restore
pwsh -NoProfile -File tools/pack-local.ps1
```

Packages are written to ignored `artifacts/packages/`. `Notices.props` files enumerate the pinned published BRUTAL dependency closure; update them when changing dependencies. Each package bundles its applicable dependency notices and a `buildTransitive` target that copies them to build/publish output. Native binaries remain NuGet runtime dependencies, not manually copied binaries. This notice propagation is not full licence-compliance clearance.

## Consume packages

A game references only the modules it needs:

```xml
<ItemGroup>
  <PackageReference Include="CrushKit.Brutal.Draw2D" Version="0.1.0-preview.1" />
  <PackageReference Include="CrushKit.Brutal.Vulkan" Version="0.1.0-preview.1" />
  <!-- Optional inspector: -->
  <PackageReference Include="CrushKit.Brutal.ImGui" Version="0.1.0-preview.1" />
</ItemGroup>
```

Until publication, add `artifacts/packages` as a local source using a path relative to the consuming repository's `nuget.config`; retain nuget.org for published dependencies. Breakout's configuration demonstrates a source mapping that restricts `CrushKit.Brutal.*` to the local feed. BRUTAL dependencies, native runtime assets and embedded shaders restore automatically; consumers do not copy source or shader files.

NuGet package versions are immutable once shared/published. During this unpublished extraction, prefer project references for edits; repacking the same preview version can leave stale global-cache copies and invalidate consumer lock-file hashes. The verification script uses a fresh isolated package cache instead. When advancing a shared release, increment the version and deliberately update consumer lock files.

## Independent sample and artifact verification

```powershell
# CPU-only contract check: no window, audio or inspector.
dotnet run --project samples/ShapeGallery
# Explicit visible demo; do not run in unattended validation.
dotnet run --project samples/ShapeGallery -- --show
# Fresh real-package consumer, native assets, notices, shader resources and publish output:
pwsh -NoProfile -File tools/verify-packages.ps1
# Also bounded hidden Vulkan presentation/resize with validation layers:
pwsh -NoProfile -File tools/verify-packages.ps1 -HiddenSmoke
```

The sample normally uses project references. The verifier creates an independent application under ignored `.local/package-consumer/`, references the packed Vulkan package, restores into an isolated cache, checks that no ImGui package appears, executes CPU/resource contracts, checks native/notice propagation and publishes locally. `-HiddenSmoke` additionally presents shapes and resizes a hidden GLFW window. It does not establish visible image quality, minimize/restore, text/font DPI fidelity, audible output, controller input or other-platform acceptance.

Breakout retains game integration tests and its hidden overlay/gameplay harness; the headless geometry and GPU ABI/upload tests live here.

## Publication gates

No remote, public package or source licence is configured by this extraction. Confirm the intended `crushkit-brutal` repository and `CrushKit.Brutal.*` IDs/prefix ownership before publishing. Preserve the existing Unity predecessor. Review all intended public files/refs and dependency/asset provenance, choose a source licence, and obtain explicit publication approval.

BRUTAL is not plain MIT: consult the actual licence for credits, pre-game logo/permission and NOTICE requirements before distributing products. Copying notices alone does not satisfy all obligations. Public source visibility and compiled-game distribution are separate decisions.
