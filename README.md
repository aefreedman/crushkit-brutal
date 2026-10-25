# CrushKit BRUTAL

A small, composable C# game kit built on published BRUTAL packages. This is the BRUTAL successor to the older Unity-era CrushKit idea, not a rewrite of that repository or a full scene/entity engine.

**Local prerelease only.** These packages are not published. Source licence selection, source/asset ownership review, exact public-tree/history review and publication approval remain pending. No open-source licence grant or release clearance is implied.

## Modules

| Package / namespace | Responsibility |
| --- | --- |
| `CrushKit.Brutal.Draw2D` | Headless draw lists, tessellation, transforms, clipping, colors, fills, outlines and patterns |
| `CrushKit.Brutal.Vulkan` | Direct Vulkan/GLFW presentation, frame uploads, embedded shaders, bounded SDL.Ttf label cache and textured quads |
| `CrushKit.Brutal.ImGui` | Optional diagnostics inspector with application-supplied UI controls |
| `CrushKit.Brutal.Input` | Binding-neutral GLFW samples, pad selection, aggregate edges and radial direction helpers |
| `CrushKit.Brutal.Audio` | Generic clip/SDL.Mixer ownership, silent fallback and first-idle/drop-new bounded voices |
| `CrushKit.Brutal.Ecs` | Arch world lifetime, worker jobs with explicit dependencies, queued submissions and awaitable completion |
| `CrushKit.Brutal.Physics` | Box2D native task scheduling and guarded global world creation/destruction |

Vulkan depends on Draw2D, not ImGui. ImGui depends on Vulkan. None depends on Breakout, its simulation, settings JSON or assets. All modules currently share version `0.1.0-preview.4`; there is no umbrella package. Input and Audio depend on neither graphics nor one another. Bindings, aim priority, cue names/event mapping, focus/pause policy and simulation remain application-owned. Ecs owns generic world/job plumbing, not game components or physics policy; see [Arch hosting and threading constraints](docs/ecs.md). Physics provides the independent [Box2D task bridge](docs/physics-jobs.md), without an Arch dependency. See [input/audio usage and binding constraints](docs/input-audio.md).

The renderer's optional inspector seam is intentionally kit-internal, not a public arbitrary render-plugin API. The provided `ImGuiInspector` transfers ownership to the renderer when passed to `ConfigureOverlay`; it retires at the shared fence/device-idle boundaries. Overlay label content, diagnostic state/detail strings, layout settings, fonts and images are supplied by the application. The kit ships no game fonts, media or default configuration.

Text-only configuration is independent: `OverlaySettings` contains only text settings and requires no inspector assets. `CrushKit.Brutal.ImGui.InspectorSettings` owns the inspector font, PNG, limits and layout; construct `new ImGuiInspector(inspectorSettings)` and pass it to `ConfigureOverlay(textSettings, inspector)`. An optional second constructor argument, `Action? drawControls = null`, emits application UI after diagnostics and before ImGui rendering; see the [UI-only callback and lifetime contract](docs/imgui-inspector.md). Both settings objects validate before native startup and are retained, not copied or merged with hidden defaults. Applications may keep a combined JSON schema and explicitly create both objects, as Breakout does.

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
dotnet test tests/ImGui/ImGui.Tests.csproj --no-restore
dotnet test tests/Input/Input.Tests.csproj --no-restore
dotnet test tests/Audio/Audio.Tests.csproj --no-restore
dotnet test tests/Ecs/Ecs.Tests.csproj --no-restore
dotnet test tests/Physics/Physics.Tests.csproj --no-restore
pwsh -NoProfile -File tools/pack-local.ps1
```

Packages are written to ignored `artifacts/packages/`. `Notices.props` files enumerate the pinned published BRUTAL dependency closure; update them when changing dependencies. Each package bundles its applicable dependency notices and a `buildTransitive` target that copies them to build/publish output. Native binaries remain NuGet runtime dependencies, not manually copied binaries. This notice propagation is not full licence-compliance clearance.

## Consume packages

A game references only the modules it needs:

```xml
<ItemGroup>
  <PackageReference Include="CrushKit.Brutal.Draw2D" Version="0.1.0-preview.4" />
  <PackageReference Include="CrushKit.Brutal.Vulkan" Version="0.1.0-preview.4" />
  <!-- Optional inspector: -->
  <PackageReference Include="CrushKit.Brutal.ImGui" Version="0.1.0-preview.4" />
  <!-- Independent mechanics, if needed: -->
  <PackageReference Include="CrushKit.Brutal.Input" Version="0.1.0-preview.4" />
  <PackageReference Include="CrushKit.Brutal.Audio" Version="0.1.0-preview.4" />
  <PackageReference Include="CrushKit.Brutal.Ecs" Version="0.1.0-preview.4" />
  <PackageReference Include="CrushKit.Brutal.Physics" Version="0.1.0-preview.4" />
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
# Independent headless input/audio example (no device or window):
dotnet run --project samples/InputAudio
# Independent managed Arch/job concurrency example (no native dependencies):
dotnet run --project samples/EcsJobs
# Independent native collision/worker-task example (no graphics or Arch):
dotnet run --project samples/PhysicsJobs
# Fresh real-package consumers, native assets, notices, shader resources and publish output:
pwsh -NoProfile -File tools/verify-packages.ps1
# Also bounded hidden Vulkan presentation/resize with validation layers:
pwsh -NoProfile -File tools/verify-packages.ps1 -HiddenSmoke
```

The sample normally uses project references. The verifier creates independent renderer, input/audio, ECS/job and physics/job applications under ignored `.local/package-consumer-*/`, references real packed packages, restores into isolated caches, checks unwanted dependencies are absent, executes CPU/resource contracts, checks native/notice propagation and publishes locally. `-HiddenSmoke` additionally presents shapes and resizes a hidden GLFW window. With `-HiddenSmoke -TextFont <application-supplied-ttf>`, it also verifies direct text rendering without any inspector package/assets. It does not establish visible image quality, minimize/restore, text/font DPI fidelity, audible output, controller input or other-platform acceptance.

Breakout retains game integration tests and its hidden overlay/gameplay harness; the headless geometry and GPU ABI/upload tests live here.

## Publication gates

No remote, public package or source licence is configured by this extraction. Confirm the intended `crushkit-brutal` repository and `CrushKit.Brutal.*` IDs/prefix ownership before publishing. Preserve the existing Unity predecessor. Review all intended public files/refs and dependency/asset provenance, choose a source licence, and obtain explicit publication approval.

BRUTAL is not plain MIT: consult the actual licence for credits, pre-game logo/permission and NOTICE requirements before distributing products. Copying notices alone does not satisfy all obligations. Public source visibility and compiled-game distribution are separate decisions.
