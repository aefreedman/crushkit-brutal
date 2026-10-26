using System.Text.Json;
using System.Text.Json.Serialization;
using Brutal.GlfwApi;
using CrushKit.Brutal.Draw2D;
using CrushKit.Brutal.Vulkan;

// No window unless explicitly requested. --hidden-smoke is bounded and never initializes audio or ImGui.
var shaderNames = typeof(VulkanRenderer).Assembly.GetManifestResourceNames();
foreach (var shader in new[] { "shapes.vert", "shapes.frag", "overlay.vert", "overlay.frag" })
    if (!shaderNames.Contains("CrushKit.Brutal.Vulkan.Shaders." + shader))
        throw new InvalidDataException("Missing embedded shader: " + shader);
if (!args.Contains("--show") && !args.Contains("--hidden-smoke"))
{
    using var draw = new ShapeDrawList();
    draw.BeginFrame(100, 100);
    draw.FillDisc(new(50, 50), 20, new(0, 1, 1));
    if (draw.Finish().Indices.IsEmpty) throw new InvalidOperationException("No geometry.");
    // Header-only fixture verifies text configuration has no inspector-asset dependency, not font rasterization.
    string fontFixture = Path.GetTempFileName();
    try
    {
        File.WriteAllBytes(fontFixture, [0, 1, 0, 0, 0, 0, 0, 0]);
        _ = new OverlaySettings(true, fontFixture, 22, 10, 20, 24, 8, 128, 3, 1024);
    }
    finally { File.Delete(fontFixture); }
    Console.WriteLine("Draw2D, text-only settings and embedded shader contracts passed; use --show or --hidden-smoke to render.");
    return;
}
var config = JsonSerializer.Deserialize<SampleSettings>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample.json")),
    new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true })
    ?? throw new InvalidDataException("Missing sample configuration.");
if (config.Width <= 0 || config.Height <= 0) throw new InvalidDataException("Positive dimensions required.");
bool hidden = args.Contains("--hidden-smoke");
int textFontOption = Array.IndexOf(args, "--text-font");
if (textFontOption >= 0 && textFontOption + 1 >= args.Length) throw new ArgumentException("--text-font requires an application-supplied TTF.");
// Explicit integration fixture: no installed/shipped font default or inspector assets.
OverlaySettings? textSettings = textFontOption < 0 ? null : new(true, Path.GetFullPath(args[textFontOption + 1]), 22, 10, 20, 24, 8, 128, 3, 1024);
if (!Glfw.Init()) throw new InvalidOperationException("GLFW initialization failed.");
try
{
    Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
    if (hidden)
    {
        Glfw.WindowHint(GlfwWindowHint.Visible, 0);
        Glfw.WindowHint(GlfwWindowHint.Focused, 0);
        Glfw.WindowHint(GlfwWindowHint.FocusOnShow, 0);
    }
    var window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "CrushKit BRUTAL", Size = new(config.Width, config.Height) });
    if (window.IsNull()) throw new InvalidOperationException("Window creation failed.");
    using var renderer = VulkanRenderer.Create(window, validation: hidden);
    if (textSettings is not null) renderer.ConfigureOverlay(textSettings);
    using var draw = new ShapeDrawList();
    var deadline = DateTime.UtcNow.AddSeconds(15);
    int presented = 0;
    int[] hiddenCounts = [1, 32, 2, 2, 128, 1, 128, 1];
    var vertexBuffer = typeof(VulkanRenderer).GetField("_vertices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var indexBuffer = typeof(VulkanRenderer).GetField("_indices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    int Capacity(System.Reflection.FieldInfo field)
    {
        object buffer = field.GetValue(renderer)!;
        return checked((int)(Brutal.ByteSize)buffer.GetType().GetProperty("BufferSize")!.GetValue(buffer)!);
    }
    int previousVertexCapacity = 0, previousIndexCapacity = 0, replacements = 0;
    while (!window.ShouldClose)
    {
        if (hidden && DateTime.UtcNow > deadline) throw new TimeoutException("Hidden sample deadline exceeded.");
        Glfw.PollEvents();
        var size = window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0 || window.IsIconified) { Glfw.WaitEventsTimeout(.05); continue; }
        if (textSettings is not null)
        {
            var capture = renderer.BeginOverlayFrame();
            if (renderer.InspectorActive || capture.Mouse || capture.Keyboard) throw new InvalidOperationException("Text-only consumer activated an inspector.");
            renderer.SetOverlayFrame(new([new("CrushKit text without ImGui", new(1, 1, 1))], new("Sample", "", 0, presented, 0, 0)));
        }
        draw.BeginFrame(size.X, size.Y);
        if (hidden)
            for (int i = 0; i < hiddenCounts[presented]; i++)
                draw.FillRect(new(10 + i % 16 * 10, 10 + i / 16 * 10), new(8, 8), new(0, 1, 1));
        else draw.FillDisc(new(size.X / 2f, size.Y / 2f), Math.Min(size.X, size.Y) / 4f, new(0, 1, 1));
        var frame = draw.Finish();
        if (renderer.Draw(frame, new(0, 0, 0)))
        {
            if (hidden)
            {
                int vc = Capacity(vertexBuffer), ic = Capacity(indexBuffer);
                int vb = checked(frame.Vertices.Length * 24), ib = checked(frame.Indices.Length * 4);
                if (vc < vb || ic < ib || vc < previousVertexCapacity || ic < previousIndexCapacity)
                    throw new InvalidOperationException("Upload capacity is undersized or shrank.");
                if (vc != previousVertexCapacity || ic != previousIndexCapacity) replacements++;
                if (presented is 2 or 3 or 5 or 6 or 7 && (vc != previousVertexCapacity || ic != previousIndexCapacity))
                    throw new InvalidOperationException("Shrink/reuse unexpectedly replaced upload storage.");
                Console.WriteLine($"Upload receipt: step={presented}; extent={size.X}x{size.Y}; rectangles={hiddenCounts[presented]}; vertices={frame.Vertices.Length}; indices={frame.Indices.Length}; vertexBytes={vb}; indexBytes={ib}; vertexCapacity={vc}; indexCapacity={ic}.");
                previousVertexCapacity = vc; previousIndexCapacity = ic;
            }
            presented++;
            if (hidden && presented == 3) window.Size = new(config.Height, config.Width);
        }
        if (hidden && presented >= hiddenCounts.Length) break;
    }
    if (hidden && replacements != 3) throw new InvalidOperationException("Expected initial upload and exactly two growth replacements.");
    if (hidden) Console.WriteLine($"Upload growth/shrink/reuse/resize passed: frames={presented}; replacementPairs={replacements}; validationErrors={VulkanRenderer.ValidationErrors}.");
    if (hidden && textSettings is not null && (renderer.OverlayEvidence.TextFrames < 6 || renderer.OverlayEvidence.InspectorFrames != 0))
        throw new InvalidOperationException("Text-only presentation evidence missing.");
}
finally { Glfw.Terminate(); }
if (VulkanRenderer.ValidationErrors != 0) throw new InvalidOperationException("Vulkan validation errors occurred.");
Console.WriteLine("Sample presentation passed.");

sealed record SampleSettings(int Width, int Height);
