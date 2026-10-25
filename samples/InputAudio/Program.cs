using System.Buffers.Binary;
using Brutal.GlfwApi;
using Brutal.Numerics;
using CrushKit.Brutal.Input;
using CrushKit.Brutal.Audio;

// Independent headless example: fake input, silent output, no GLFW/SDL initialization or device.
var source = new SampleInput();
var poller = new InputPoller(source, [GlfwKey.Space], []);
var edges = new ButtonEdges();
if (!poller.Poll().Focused) throw new Exception("Input contract");
edges.Update(true, new Dictionary<string, bool> { ["activate"] = false });
source.Held = true;
if (!edges.Update(true, new Dictionary<string, bool> { ["activate"] = poller.Poll().Down(GlfwKey.Space) }))
    throw new Exception("Input edge contract");
if (!InputMath.TryDirection(new(3, 4), .2f, out var direction) || Math.Abs(direction.X - .6f) > .00001f)
    throw new Exception("Input direction contract");

var directory = Path.Combine(Path.GetTempPath(), "crushkit-silent-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory, "clip.wav");
    byte[] data = new byte[46]; // One silent PCM16 sample, generated locally; not a game asset.
    "RIFF"u8.CopyTo(data); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 38);
    "WAVEfmt "u8.CopyTo(data.AsSpan(8)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 8000); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 16000);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 2); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16);
    "data"u8.CopyTo(data.AsSpan(36)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 2); File.WriteAllBytes(path, data);
    // Application provides all tuning explicitly. Disabled factory is guaranteed not to open a device.
    var settings = new AudioSettings<string>(false, .5, 2, new Dictionary<string, string> { ["activate"] = path });
    using var player = new VoicePlayer<string>(settings, SdlMixerOutput<string>.Create(settings, out var diagnostic));
    if (diagnostic is not null || player.TryPlay("activate")) throw new Exception("Silent audio contract");
}
finally { Directory.Delete(directory, true); }
Console.WriteLine("Input/audio consumer passed without window, device or game dependencies.");

sealed class SampleInput : IInputSource
{
    public bool Held;
    public bool Focused => true;
    public bool Minimized => false;
    public double2 Cursor => default;
    public bool Down(GlfwKey key) => key == GlfwKey.Space && Held;
    public bool Down(GlfwMouseButton button) => false;
    public bool TryGamepad(int id, out GamepadSnapshot? gamepad) { gamepad = null; return false; }
}
