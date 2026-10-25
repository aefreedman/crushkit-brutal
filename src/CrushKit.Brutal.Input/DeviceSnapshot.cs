using System.Collections.Frozen;
using Brutal.GlfwApi;
using Brutal.Numerics;

namespace CrushKit.Brutal.Input;

/// <summary>Copied standardized GLFW gamepad data. Axes keep GLFW orientation (+Y down); no bindings or deadzones.</summary>
public sealed class GamepadSnapshot
{
    private readonly float[] axes;
    private readonly FrozenSet<GlfwGamepadButton> buttons;
    public GamepadSnapshot(IReadOnlyList<float> axes, IEnumerable<GlfwGamepadButton> buttons)
    {
        ArgumentNullException.ThrowIfNull(axes); ArgumentNullException.ThrowIfNull(buttons);
        if (axes.Count != 6) throw new ArgumentException("Six standardized axes required.", nameof(axes));
        this.axes = axes.ToArray();
        this.buttons = buttons.ToFrozenSet();
        if (this.buttons.Any(b => (int)b < 0 || (int)b > 14)) throw new ArgumentException("Invalid gamepad button.", nameof(buttons));
    }
    public float Axis(GlfwGamepadAxis axis)
    {
        if ((uint)(int)axis >= axes.Length) throw new ArgumentOutOfRangeException(nameof(axis));
        return axes[(int)axis];
    }
    public bool Down(GlfwGamepadButton button) => buttons.Contains(button);
}

/// <summary>Owned immutable sample. Unwatched keyboard/mouse controls are absent.
/// Polling can miss complete edges or disconnect/reconnect events between samples.</summary>
public sealed class DeviceSnapshot
{
    public bool Focused { get; }
    public bool Minimized { get; }
    public double2 Cursor { get; }
    public int? PadId { get; }
    public GamepadSnapshot? Gamepad { get; }
    private readonly FrozenSet<GlfwKey> keys;
    private readonly FrozenSet<GlfwMouseButton> mouse;
    internal DeviceSnapshot(bool focused, bool minimized, double2 cursor, IEnumerable<GlfwKey> keys,
        IEnumerable<GlfwMouseButton> mouse, int? padId, GamepadSnapshot? gamepad)
    {
        Focused = focused; Minimized = minimized; Cursor = cursor;
        this.keys = keys.ToFrozenSet(); this.mouse = mouse.ToFrozenSet(); PadId = padId; Gamepad = gamepad;
    }
    public bool Down(GlfwKey key) => keys.Contains(key);
    public bool Down(GlfwMouseButton button) => mouse.Contains(button);
}
