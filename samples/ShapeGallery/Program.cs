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
    Console.WriteLine("Draw2D and embedded shader contract passed; use --show or --hidden-smoke to render.");
    return;
}
var config = JsonSerializer.Deserialize<SampleSettings>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sample.json")),
    new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true })
    ?? throw new InvalidDataException("Missing sample configuration.");
if (config.Width <= 0 || config.Height <= 0) throw new InvalidDataException("Positive dimensions required.");
bool hidden = args.Contains("--hidden-smoke");
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
    using var draw = new ShapeDrawList();
    var deadline = DateTime.UtcNow.AddSeconds(15);
    int presented = 0;
    while (!window.ShouldClose)
    {
        if (hidden && DateTime.UtcNow > deadline) throw new TimeoutException("Hidden sample deadline exceeded.");
        Glfw.PollEvents();
        var size = window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0 || window.IsIconified) { Glfw.WaitEventsTimeout(.05); continue; }
        draw.BeginFrame(size.X, size.Y);
        draw.FillDisc(new(size.X / 2f, size.Y / 2f), Math.Min(size.X, size.Y) / 4f, new(0, 1, 1));
        if (renderer.Draw(draw.Finish(), new(0, 0, 0))) presented++;
        if (hidden && presented == 3) window.Size = new(config.Height, config.Width);
        if (hidden && presented >= 6) break;
    }
}
finally { Glfw.Terminate(); }
if (VulkanRenderer.ValidationErrors != 0) throw new InvalidOperationException("Vulkan validation errors occurred.");
Console.WriteLine("Sample presentation passed.");

sealed record SampleSettings(int Width, int Height);
