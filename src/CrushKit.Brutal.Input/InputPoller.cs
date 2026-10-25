using Brutal.GlfwApi;
using Brutal.Numerics;

namespace CrushKit.Brutal.Input;

/// <summary>Owner-thread sampling seam; application pumps events first. Implementations must return copied gamepad data.</summary>
public interface IInputSource
{
    bool Focused { get; }
    bool Minimized { get; }
    double2 Cursor { get; }
    bool Down(GlfwKey key);
    bool Down(GlfwMouseButton button);
    bool TryGamepad(int id, out GamepadSnapshot? gamepad);
}

/// <summary>Retains a standardized pad until unavailable, then scans IDs 0..15.
/// No GLFW initialization, callbacks, window ownership, event pumping, bindings or action semantics.</summary>
public sealed class InputPoller
{
    private readonly IInputSource source;
    private readonly GlfwKey[] keys;
    private readonly GlfwMouseButton[] buttons;
    private readonly int owner = Environment.CurrentManagedThreadId;
    private int selectedPad = -1;
    public InputPoller(IInputSource source, IEnumerable<GlfwKey> keys, IEnumerable<GlfwMouseButton> buttons)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(keys); ArgumentNullException.ThrowIfNull(buttons);
        this.keys = keys.Distinct().ToArray(); this.buttons = buttons.Distinct().ToArray();
        if (this.keys.Any(k => !Enum.IsDefined(k) || (int)k < 0)) throw new ArgumentException("Invalid key.", nameof(keys));
        if (this.buttons.Any(b => (int)b < 0 || (int)b > 7)) throw new ArgumentException("Invalid mouse button.", nameof(buttons));
    }
    public DeviceSnapshot Poll()
    {
        if (Environment.CurrentManagedThreadId != owner) throw new InvalidOperationException("Input belongs to its creating window/event thread.");
        bool focused = source.Focused, minimized = source.Minimized;
        if (!focused || minimized) return new(focused, minimized, default, [], [], null, null);
        GamepadSnapshot? pad = null;
        int old = selectedPad;
        if (old < 0 || !source.TryGamepad(old, out pad) || pad is null)
        {
            selectedPad = -1;
            for (int id = 0; id < 16; id++)
                if (id != old && source.TryGamepad(id, out pad) && pad is not null) { selectedPad = id; break; }
        }
        return new(true, false, source.Cursor, keys.Where(source.Down), buttons.Where(source.Down),
            selectedPad >= 0 ? selectedPad : null, selectedPad >= 0 ? pad : null);
    }
}

/// <summary>Published GLFW binding adapter. Construct and Poll on the window thread after pumping events.</summary>
public sealed class GlfwInputPoller
{
    private readonly InputPoller poller;
    public GlfwInputPoller(GlfwWindow window, IEnumerable<GlfwKey> keys, IEnumerable<GlfwMouseButton> buttons) =>
        poller = new(new GlfwSource(window), keys, buttons);
    public DeviceSnapshot Poll() => poller.Poll();

    private sealed class GlfwSource(GlfwWindow window) : IInputSource
    {
        public bool Focused => window.IsFocused;
        public bool Minimized => window.IsIconified;
        public double2 Cursor => window.CursorPos;
        public bool Down(GlfwKey key) => window.GetKey(key) == GlfwKeyAction.Press;
        public bool Down(GlfwMouseButton button) => window.GetMouseButton(button) == GlfwButtonAction.Press;
        public unsafe bool TryGamepad(int id, out GamepadSnapshot? gamepad)
        {
            gamepad = null;
            if (!Glfw.JoystickIsGamepad(id) || !Glfw.GetGamepadState(id, out var state)) return false;
            float[] axes = new float[6];
            for (int i = 0; i < axes.Length; i++) axes[i] = state.Axes[i];
            List<GlfwGamepadButton> held = [];
            for (int i = 0; i < 15; i++)
                if (state.Buttons[i] == (byte)GlfwButtonAction.Press) held.Add((GlfwGamepadButton)i);
            gamepad = new(axes, held);
            return true;
        }
    }
}
