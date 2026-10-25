# Headless CPU geometry tests

From the repository root (.NET 10 SDK):

```sh
dotnet restore tests/Draw2D/Draw2D.Tests.csproj --locked-mode
dotnet build src/CrushKit.Brutal.Draw2D/CrushKit.Brutal.Draw2D.csproj -c Debug --no-restore
dotnet build src/CrushKit.Brutal.Draw2D/CrushKit.Brutal.Draw2D.csproj -c Release --no-restore
dotnet test tests/Draw2D/Draw2D.Tests.csproj -c Debug --no-restore
dotnet test tests/Draw2D/Draw2D.Tests.csproj -c Release --no-restore
```

No window, GPU, Vulkan, or game entrypoint is involved. ABI tests cover the frozen BRUTAL float2/float4 vertex layout. Geometry tests verify finite/index-valid positive-area triangles, area and pairwise positive-area overlap, the ring hole, segment caps, bevel polylines, transforms (including BRUTAL's row-vector convention), curve error, command order, clips, budgets and transactional state.

## Defined edge semantics

- Frame dimensions must be strictly positive. Invalid `BeginFrame` leaves the current frame unchanged. A valid `BeginFrame` resets state and invalidates all prior borrowed frame data.
- First validate state and every supplied finite component/scalar constraint (including every polyline point, input count, cap enum, ring ordering and color). Then known no-output cases skip generated-geometry/envelope/tessellation/budget checks: zero alpha, empty effective clip, zero shape extent, equal ring radii or exactly singular transform. Hidden polylines need not undergo outline/self-intersection analysis.
- Open polylines have butt caps and bevel outer joins. The inner offset edges intersect; one simple outline is triangulated into nonoverlapping tiles. Reversals and crossing/touching/overlapping outlines are rejected; very tight/wide turns may therefore be rejected rather than unioned. Consecutive duplicates and same-direction collinear runs are removed. At most 1024 supplied points are accepted, even if many are duplicates.
- Finite nonsingular transforms may exceed FLOAT32 framebuffer representability. Before committing output, double predicates over the actual quantized vertices reject inverted nonzero tiles (with the double-transformed winding, including reflections); zero-area tiles are dropped. Quantized polyline boundaries are also cleaned and rechecked for nonadjacent touching/crossing. These failures throw a clear `ArgumentException` transactionally; no determinant epsilon, topology repair, or union operation is used. Tests cover the concrete thin-shear inversion, translated tiny U-gap collapse, and representable thin/reflected/sheared/zoomed controls.
- Transforms affect geometry only; scissors remain framebuffer integers. Transparent shape order is retained; only adjacent equal scissors merge. Empty clipped shapes consume no output budget.
- Curve subdivision uses the largest singular value of the composed linear transform. The 0.25px bound conservatively reserves 0.09px for float framebuffer quantization and `radiusInPixels * 1e-6` for the published disc generator's float trigonometry. This can reject large curves earlier than a sagitta-only 4096-segment calculation. No tolerance is silently relaxed.
- `GenerateDisk` 2026.10.2 is reused, including its index output. It emits XY triangle soup for axis +Z. The adapter owns/disposes two reusable native lists; `ProcGenMesh` borrows them. Its final rim endpoint is welded to its first endpoint: published float `sin(Tau)` otherwise leaves a tiny overlapping seam. The probe test verifies the actual package topology and tiled area.

Output arrays and per-shape scratch storage grow on demand and are reused; vertex/index arrays never exceed configured frame capacities. Polyline simplicity and ear triangulation use bounded quadratic work rather than a generic path-union engine. No GPU behavior is validated by this suite.
