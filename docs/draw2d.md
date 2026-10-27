# Draw2D API and limits

Use a disposable `ShapeDrawList`, `BeginFrame(width, height[, worldToPixel])`, primitive calls, then `Finish()` and synchronously `VulkanRenderer.Draw(frame)` before the next `BeginFrame`. Frames borrow producer buffers: never retain after the next frame or disposal. The consuming application disposes the list, including BRUTAL mesh native scratch, before renderer shutdown. GLFW, frame production and graphics stay on the main thread. `Draw` returns true only when at least one indexed draw from the supplied current-extent frame was recorded and presentation returned Success/Suboptimal. It returns false for clear-only, mismatched-extent, empty/all-clipped frames and recoverable unavailable/out-of-date presentation paths; existing fatal errors still throw. `PresentedFrames` continues counting all successful presentations, including clear-only. Callers may ignore the return. `Draw(null, background)` clears only; skip `BeginFrame` for zero extents.

Coordinates are physical framebuffer pixels, origin top-left, +Y down. `SetTransform` applies local-to-world before world-to-pixel; local radii/widths transform too. `PushClip` intersects a half-open integer framebuffer scissor with its parent; balance with `PopClip`. Clips are not transformed local geometry. Submission order is preserved, merging only adjacent equal-scissor commands. `LinearColor` accepts straight linear RGBA in [0,1]; use `FromSrgb` for sRGB source colors. Vertices premultiply once; blending uses ONE / ONE_MINUS_SRC_ALPHA and the sRGB attachment encodes once.

`FillDisc(center, radius, color[, detail])` and `OrderedShapeDrawList.Disc(draw, center, radius, color[, detail])` accept the same immutable `CircleDetail`. Omitted/null means Automatic: round the automatic count upward to a multiple of four, retaining the ≤0.25 physical-pixel curve allowance. Explicit `new CircleDetail(CircleDetailMode.FixedSideCount, n)` requires a multiple of four in [8,4096], never rounds, and uses exactly n sides independently of radius. Fixed intentionally waives only the automatic chord-error target, not coordinate/precision/area/budget validation. Ordered fallback retains the selected policy. Geometry keys/certificates depend on n, not policy; automatic eligibility is checked per call. Disc rims reuse the published generator topology with exact quarter-turn/sign/swap symmetry, canonical cardinal zeros and an exact closing weld. This choice is per call, not global: rings, rounded rectangles, outlines and patterns retain their own policies.

Filled calls: `FillDisc`, `FillRing`, `FillRect`, `FillRoundedRect`, `FillTriangle`. Solid open calls: `StrokeSegment` (butt/square/round caps), `StrokePolyline` (butt-cap, bevel-join). Solid closed calls: `StrokeCircle`, `StrokeRect`, `StrokeRoundedRect`, `StrokeTriangle`, `StrokeClosedPolyline`.

```csharp
using Brutal.Numerics;
using CrushKit.Brutal.Draw2D;

using var draw = new ShapeDrawList();
draw.BeginFrame(1280, 720);
var cyan = LinearColor.FromSrgb(.24f, .84f, .85f);
draw.StrokeRoundedRect(new(40, 40), new(200, 100), 20, 8, cyan,
    alignment: StrokeAlignment.Inside, space: StrokeSpace.Framebuffer);
draw.StrokePatternedSegment(new(40, 200), new(320, 200), 10,
    StrokePattern.Dashes(24, 18, offsetCycles: -.25), cyan, LineCap.Round);
draw.StrokePatternedCircle(new(440, 200), 60, 8,
    StrokePattern.FixedDots(16), cyan);
var pattern = StrokePattern.Dashes(36, 28, offsetCycles: .15);
draw.StrokePatternedSegment(new(40, 300), new(328, 300), 18,
    pattern.WithShape(DashShape.Angled, -1), cyan); // Butt only
draw.StrokePatternedSegment(new(40, 380), new(328, 380), 18,
    pattern.WithShape(DashShape.Chevron, .5), cyan);
var frame = draw.Finish(); // synchronously renderer.Draw(frame) before next BeginFrame/disposal
```

Outline width is full thickness; `Center` (default) puts half on either side of the input boundary, `Inside` preserves outer bounds and `Outside` preserves the inner boundary, independent of winding/reflection. Rectangles, triangles and convex closed polylines use miter corners. Rounded rectangles clamp radius to half the shorter extent; eroded radii clamp to zero. A consumed hole produces a solid outer silhouette, not inverted triangles.

`StrokeSpace.Local` (default) measures offsets, widths and pattern lengths before local/world transforms; nonuniform transforms also distort local circular dots. `Framebuffer` transforms the boundary/centerline first and then measures offsets and lengths in physical pixels, keeping width constant under scaling/shear. Transformed circles use sampled contours (including ellipses), not an analytic ellipse-arclength promise. Patterned strokes follow their centerline; they do not take an alignment argument.

Pattern calls: `StrokePatternedSegment`, `StrokePatternedPolyline(points, width, pattern, color, closed=false, cap=Butt, space=Local)` and `StrokePatternedCircle`. Factories:

- `StrokePattern.Dashes(length, gap, offsetCycles=0, fitClosed=false)` and `RelativeDashes(lengthMultiples, gapMultiples, offsetCycles=0, fitClosed=false)`.
- `StrokePattern.Dots(spacing, offsetCycles=0, fitClosed=false)` and `RelativeDots(spacingMultiples, offsetCycles=0, fitClosed=false)`.
- `StrokePattern.FixedDashes(count, gapFraction=0.5, offsetCycles=0)` and `FixedDots(count, offsetCycles=0)`.

Absolute lengths use the selected space; relative lengths multiply full width. Dash length is the **occupied centerline interval**, not the visible tip-to-tip length: square/round caps extend half a width past each interval. Gaps must allow these extensions and turns; touching/overlapping fragment geometry rejects rather than blending twice. Zero gap can become a continuous solid path. Dots are discs of diameter width, not short round dashes; cap appearance is ignored for dots but the enum is still validated.

Positive phase moves the lattice forward by a fraction of a period; phase wraps periodically, including negative offsets (`-.25` equals `.75`, `1` equals `0`). Arc-length phase continues across vertices; closed seam fragments are stitched rather than capped twice. `FixedDashes` fits **count complete periods**, not necessarily count disconnected visible islands: phase may split/clip endpoint fragments. `FixedDots` places count sites over `[0,length)`, never duplicating the far endpoint. Natural dots include an open far endpoint only when it is a lattice site; closed paths never duplicate seam dots. `fitClosed` rescales natural spacing to the nearest whole period count (midpoints upward, minimum one), preserving dash duty cycle; fixed-count patterns inherently fit. Open end-to-end snapping is not supported.

`pattern.WithShape(DashShape.Angled, modifier)` and `pattern.WithShape(DashShape.Chevron, modifier)` (modifier defaults to 1) return immutable styles retaining sizing, phase and fitting; factories remain `Basic` by default. Modifiers must be finite in **[-1, 1]**. Angled ends are slanted stripes; Chevron mirrors the slant across the width into concave V-shaped ends. Sign mirrors direction against supplied path order in the chosen Local/Framebuffer space (including reflections); magnitude sets depth. At magnitude 1 a straight angled boundary shifts one full width across the stroke (45-degree slope); each chevron half shifts half a width. Exactly zero uses Basic geometry, and Basic ignores a validated modifier. Dots reject non-Basic shapes, even with modifier zero.

Decorative styles own their end boundary and require `LineCap.Butt`, including modifier zero; Square/Round reject rather than being combined or ignored. Exactly-zero modifiers use Basic geometry, not an exemption from style/cap validation. Compatibility is validated even for invisible/no-output calls. Open decorative endpoints clip to the path's arclength domain with **no cap extension**; unlike Basic caps, the slanted tip is not extended beyond the supplied endpoints. Decoration continues across corners and the closed seam using a piecewise-affine **miter ribbon**, whereas Basic patterned open joins use bevels. Sharp/wide turns that invert this ribbon reject; there is no general path-union repair or guarantee of identical corner geometry as a nonzero modifier approaches zero. Zero-gap continuous patterns fall back to Basic solid geometry (including its joins), so decorative ends are indistinguishable; decorative cap restrictions still apply.

Decorative clipping has a strict float32 precision limit: even moderate-size paths can generate tiny clipped fragments near sampled curve edges, seams or chevron half boundaries. Inverted/overlapping actual-float tiles or loss of an entire occupied glyph reject the whole call transactionally rather than silently dropping a glyph, changing the modifier, or repairing geometry. Zero-area quantized clipping slivers may disappear if the glyph retains geometry. Sampled circles are supported, but not every radius/width/phase combination is representable under these checks; no universal safe circle recipe or epsilon-based repair is promised. The Breakout showcase uses closed rectangular paths as a robust corner/seam example, not a fallback after a caught circle failure. Existing coordinate, tessellation, complexity and output bounds still apply.

Paths accept at most 1024 input points. Consecutive duplicates, repeated closing points on closed paths, and forward-collinear runs are removed. Closed outlines/patterned paths require simple **convex** contours, either winding; nonconvex/self-crossing paths are not a general polygon-union facility. Open paths reject reversals, crossings, touching or overlapping geometry. Pattern output is limited to 4096 intervals/sites and pairwise validation to 4,000,000 candidates, including broad-phase tests; excessive complexity rejects even below the output limit. Zero-length patterned paths are validated no-ops, including round caps.

The Unity Shapes public interfaces in read-only PrototypeStudio were a **conceptual reference only**, not copied tessellation/shaders or a parity claim. Published BRUTAL packages were checked first: Numerics supplies vectors/transforms and Render.Mesh `GenerateDisk` supplies the tested disk adapter/rims; no complete planar outline/pattern facility was found. CPU tessellation fills that gap and submits the unchanged GPU triangle ABI; the outline/pattern milestone introduced no alternate renderer or font dependency (the later text overlay is described above).

Curves target <=0.25 physical-pixel chord error (8-4096 full-circle segments). Framebuffer coordinate magnitude is bounded by 2^20; default budgets are 1,000,000 vertices / 3,000,000 indices. Invalid/nonfinite/negative input or state throws. Valid zero-size/alpha/empty-clip/singular-transform shapes emit nothing. Reflections are supported, but finite geometry can be unrepresentable in float32: actual inverted triangles or touching/crossing quantized polyline boundaries are rejected transactionally, not repaired. Budget/tessellation/pattern-complexity failures also throw without partial shape output, including command merging: each failing shape call leaves previously published vertices, indices and commands unchanged. This does not extend borrowed-frame lifetime.

## Ordered shared meshes

Available since preview.11, this explicit path is selected through `OrderedShapeDrawList` and `VulkanRenderer.Draw(OrderedShapeFrame, ...)`; ordinary `ShapeDrawList`/`VulkanRenderer.Draw(ShapeFrame, ...)` calls remain triangle rendering. It supplies immutable local discs and nonoverlapping, optionally touching compound rectangles, not a general scene/path framework. Rounded bricks, arbitrary triangles, outlines and trails may continue through the ordinary dynamic path.

Create an `OrderedShapeDrawList` with application-supplied immutable `BakedMeshLimits`; no settings defaults are supplied. Consume a finished frame synchronously before reusing **either** producer:

```csharp
using var triangles = new ShapeDrawList();
using var ordered = new OrderedShapeDrawList(meshLimits);
triangles.BeginFrame(width, height);
ordered.BeginFrame();
triangles.FillRect(new(0), new(width, height), background);
ordered.Disc(triangles, center, radius, tint);
renderer.Draw(ordered.Finish(triangles), background);
```

`Disc` retains the published generator/seam weld and existing physical-pixel resolution policy, falling back to ordinary tessellation when its sufficient precision bounds cannot certify the instance. The accepted fast transform is identity world-to-pixel plus positive uniform local scale/translation; other supported ordinary transforms are not silently approximated. Nonzero subnormal local coordinates are ineligible. `TryRectangles` returns false when its candidate certificate/residency cannot support valid source geometry; the caller must emit the corresponding ordinary rectangles **at that same ordered position**. The `BakedShapeMesh.TryRectangles` factory distinguishes invalid source rejection from valid source that cannot form a FLOAT32 certificate (null). Cache keys must identify all immutable geometry inputs/revisions, not merely an entity id. Geometry changes explicitly invalidate relevant keys after prior borrowed-frame consumption; motion, tint and health changes are not geometry revisions.

CPU limits account retained vertices/indices, scalar certificates and entry overhead; applications reserve their bounded selection/revision storage through `ReserveExternalCpuBytes` before baking. Generation admission precedes staging allocation. Current-frame references are cleared before unpinning, and active entries cannot evict. GPU residency is separately byte/count bounded, retains identity-only entries (not CPU geometry/settings references), copies local geometry once on registration and retires/replaces after the existing shared fence. Unchanged meshes survive swapchain recreation; device disposal releases them. Dynamic instance/operation arrays have explicit limits, and virtual original vertex/index budgets remain enforced.

Operations split merged triangle commands at insertion checkpoints and merge only adjacent equal-mesh/equal-clip runs. Additional mesh switches can increase recording/draw work; benchmark total preparation/validation/submission rather than assume fewer draw calls. Colors, half-open clips, premultiplication, sRGB and 4x-MSAA remain unchanged. Frames borrow producer storage; owned game snapshots must not be recycled to accommodate this API. Internal capture/query/held-image hooks are diagnostic-only, not presentation modes or a public rendering extension contract.

Backend: one fence-protected in-flight upload frame, dynamic viewport/scissors, indexed triangles, classic render pass, 4x-MSAA resolve into sRGB swapchain, no depth/culling, VMA-backed buffers/images. Resize rebuilds attachment/pipeline resources. Actual device loss, other platforms/drivers, and unsupported-format/sample-count failure paths have not been exercised in integration.

The later text/texture increment uses published SDL.Ttf and Texture abstractions plus a narrow direct-Vulkan UV material. Standalone FreeType's published DLL still contains no public managed font API; no custom FreeType binding or ImGui game compositor is used.
