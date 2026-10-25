using Brutal.GlfwApi;
using Brutal.Numerics;
using CrushKit.Brutal.Input;
using Xunit;

namespace CrushKit.Tests.Input;

public sealed class InputTests
{
    private sealed class Source : IInputSource
    {
        public bool Focused { get; set; } = true;
        public bool Minimized { get; set; }
        public double2 Cursor { get; set; }
        public HashSet<GlfwKey> Keys = [];
        public HashSet<GlfwMouseButton> Mouse = [];
        public Dictionary<int, GamepadSnapshot> Pads = [];
        public int Queries;
        public bool Down(GlfwKey key) => Keys.Contains(key);
        public bool Down(GlfwMouseButton button) => Mouse.Contains(button);
        public bool TryGamepad(int id, out GamepadSnapshot? gamepad) { Queries++; return Pads.TryGetValue(id, out gamepad); }
    }
    private static GamepadSnapshot Pad(float x = 0, float y = 0) => new(new[] { x, y, 0f, 0f, -1f, -1f }, [GlfwGamepadButton.A]);
    [Fact]
    public void PollCopiesOnlyRequestedControlsAndRetainsPadUntilUnavailable()
    {
        var source = new Source { Cursor = new(10, 20), Keys = [GlfwKey.Space, GlfwKey.F1], Mouse = [GlfwMouseButton.Left] };
        source.Pads[4] = Pad(1, .5f);
        var keys = new List<GlfwKey> { GlfwKey.Space };
        var poller = new InputPoller(source, keys, [GlfwMouseButton.Left]); keys.Clear();
        var first = poller.Poll();
        Assert.True(first.Down(GlfwKey.Space)); Assert.False(first.Down(GlfwKey.F1)); Assert.True(first.Down(GlfwMouseButton.Left));
        Assert.Equal(new double2(10, 20), first.Cursor); Assert.Equal(4, first.PadId);
        Assert.Equal(.5f, first.Gamepad!.Axis(GlfwGamepadAxis.LeftY)); // Raw GLFW orientation, not game aim.
        source.Keys.Clear(); source.Mouse.Clear(); source.Pads[0] = Pad();
        Assert.Equal(4, poller.Poll().PadId);
        source.Pads.Remove(4); Assert.Equal(0, poller.Poll().PadId);
        source.Pads.Clear(); Assert.Null(poller.Poll().Gamepad);
        Assert.True(first.Down(GlfwKey.Space)); Assert.True(first.Down(GlfwMouseButton.Left)); Assert.Equal(4, first.PadId);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, true)]
    public void InactiveSamplesDoNotReadDevices(bool focused, bool minimized)
    {
        var source = new Source { Focused = focused, Minimized = minimized };
        var poller = new InputPoller(source, [GlfwKey.Space], []);
        var sample = poller.Poll();
        Assert.Equal(focused, sample.Focused); Assert.Equal(minimized, sample.Minimized);
        Assert.False(sample.Down(GlfwKey.Space)); Assert.Null(sample.Gamepad); Assert.Equal(0, source.Queries);
    }
    [Fact]
    public void GamepadCopiesAxesAndButtons()
    {
        float[] axes = [1, 2, 3, 4, 5, 6]; var buttons = new List<GlfwGamepadButton> { GlfwGamepadButton.A };
        var pad = new GamepadSnapshot(axes, buttons); axes[0] = 99; buttons.Clear();
        Assert.Equal(1, pad.Axis(GlfwGamepadAxis.LeftX)); Assert.True(pad.Down(GlfwGamepadButton.A));
        Assert.Throws<ArgumentOutOfRangeException>(() => pad.Axis((GlfwGamepadAxis)6));
        Assert.Throws<ArgumentException>(() => new GamepadSnapshot([], []));
    }
    [Fact]
    public void PollMustStayOnOwnerThread()
    {
        var poller = new InputPoller(new Source(), [], []); Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(() => poller.Poll()));
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); Assert.IsType<InvalidOperationException>(error);
        Assert.True(poller.Poll().Focused);
    }
    [Fact]
    public void EdgesBaselineStartupReconnectAndAggregateSources()
    {
        var edges = new ButtonEdges();
        Dictionary<string, bool> Sources(bool key, bool? pad = null) {
            var result = new Dictionary<string, bool> { ["key"] = key }; if (pad.HasValue) result["pad:0"] = pad.Value; return result;
        }
        Assert.False(edges.Update(true, Sources(true)));
        Assert.False(edges.Update(true, Sources(false)));
        Assert.True(edges.Update(true, Sources(true)));
        Assert.False(edges.Update(true, Sources(true, true))); // Newly connected held pad and already-held key.
        Assert.False(edges.Update(true, Sources(false, true)));
        Assert.False(edges.Update(true, Sources(false, false)));
        Assert.True(edges.Update(true, Sources(false, true)));
        Assert.False(edges.Update(false, Sources(true, true)));
        Assert.False(edges.Update(true, Sources(true, true))); // Focus return is a baseline.
        edges.Reset(); Assert.False(edges.Update(true, Sources(true)));
    }
    [Fact]
    public void ANewHeldDeviceDoesNotFireAndAReleasedDeviceCanFireLater()
    {
        var edges = new ButtonEdges();
        edges.Update(true, new Dictionary<string, bool> { ["key"] = false });
        Assert.False(edges.Update(true, new Dictionary<string, bool> { ["key"] = false, ["pad"] = true }));
        edges.Update(true, new Dictionary<string, bool> { ["key"] = false, ["pad"] = false });
        Assert.True(edges.Update(true, new Dictionary<string, bool> { ["key"] = false, ["pad"] = true }));
    }
    [Theory]
    [InlineData(0)] [InlineData(.2)] [InlineData(.999)]
    public void RadialDirectionNormalizesWithoutOverflow(double deadzone)
    {
        float d = InputMath.ValidateDeadzone(deadzone);
        Assert.False(InputMath.TryDirection(default, d, out _));
        Assert.True(InputMath.TryDirection(new(float.MaxValue, float.MaxValue), d, out var direction));
        Assert.InRange(direction.X, .70710f, .70711f);
        Assert.False(InputMath.TryDirection(new(float.NaN, 1), d, out _));
    }
    [Theory]
    [InlineData(-.1)] [InlineData(1)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(double.Epsilon)]
    public void InvalidDeadzonesReject(double value) => Assert.Throws<ArgumentOutOfRangeException>(() => InputMath.ValidateDeadzone(value));
    [Fact]
    public void InputHasNoAudioGameOrGraphicsReferences() =>
        Assert.DoesNotContain(typeof(InputPoller).Assembly.GetReferencedAssemblies(), a =>
            a.Name!.Contains("Breakout") || a.Name.Contains("Vulkan") || a.Name.Contains("ImGui") || a.Name.Contains("Sdl") || a.Name.Contains("CrushKit.Brutal.Audio"));
}
