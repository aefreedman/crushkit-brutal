using Brutal.ImGuiApi;
using Brutal.Numerics;
using CrushKit.Brutal.ImGui;
using Xunit;
using Ui = Brutal.ImGuiApi.ImGui;

namespace CrushKit.Tests.ImGui;

public sealed class ToolControlsTests
{
    [Fact]
    public void ExplicitDescriptorsRejectUnsafeFormatsAndRangesBeforeNativeWork()
    {
        foreach (string format in new[] { "%s", "%n", "%f %f", "%.18g", "%.0g", "%.3f units" })
            Assert.Throws<ArgumentException>(() => ToolDoubleEditor.Input(format));
        Assert.Equal("%.0f", ToolDoubleEditor.Input("%.0f").Format);
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolDoubleEditor.Drag(1, 1, 1, "%.3f"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolDoubleEditor.Drag(0, 1, 0, "%.3f"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolDoubleEditor.Drag(-double.MaxValue, double.MaxValue, 1, "%.3f"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolDoubleEditor.Slider(0, double.MaxValue, "%.3f"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolInt64Editor.Slider(long.MinValue, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolInt64Editor.Drag(0, 10, float.NaN));
        Assert.Throws<ArgumentException>(() => new ToolProperty("", "Speed"));
        Assert.Throws<ArgumentException>(() => new ToolHelp("Title", "Bad\0description"));
        Assert.Throws<ArgumentException>(() => new ToolFieldPreset("Hidden##identity"));
        double value = 1;
        Assert.Throws<ArgumentException>(() => ToolControls.EditDouble("value"u8, ref value,
            ToolDoubleEditor.Drag(0, 10, 1, "%.3f"), inputFlags: ImGuiInputTextFlags.EnterReturnsTrue));
    }

    [Fact]
    public void InputUsesNativeS64WithoutRoundingLargeIntegersAndNeverClampsUneditedValues()
    {
        InContext(() =>
        {
            long integer = 9007199254740993L;
            double real = 123.45678901234567;
            var integerInput = ToolInt64Editor.Input();
            var bounded = ToolDoubleEditor.Drag(0, 10, 0.1f, "%.3f");
            for (int i = 0; i < 3; i++) Frame(600, () =>
            {
                Assert.False(ToolControls.EditInt64("integer"u8, ref integer, integerInput));
                Assert.False(ToolControls.EditDouble("outside-range"u8, ref real, bounded));
            });
            Assert.Equal(9007199254740993L, integer);
            Assert.Equal(123.45678901234567, real);
        });
    }

    [Fact]
    public void ExactNativeTextEntryPreservesInt64AboveDoublePrecision()
    {
        InContext(() =>
        {
            long value = 7;
            var editor = ToolInt64Editor.Input();
            void Draw(bool focus = false)
            {
                if (focus) Ui.SetKeyboardFocusHere();
                ToolControls.EditInt64("value"u8, ref value, editor);
            }
            Frame(600, () => Draw(true)); Frame(600, () => Draw());
            var io = Ui.GetIO();
            io.AddKeyEvent(ImGuiKey.Mod_Ctrl, true); io.AddKeyEvent(ImGuiKey.A, true); Frame(600, () => Draw());
            io.AddKeyEvent(ImGuiKey.A, false); io.AddKeyEvent(ImGuiKey.Mod_Ctrl, false); Frame(600, () => Draw());
            io.AddInputCharactersUTF8("9007199254740993"u8); Frame(600, () => Draw());
            io.AddKeyEvent(ImGuiKey.Enter, true); Frame(600, () => Draw());
            io.AddKeyEvent(ImGuiKey.Enter, false); Frame(600, () => Draw());
            Assert.Equal(9007199254740993L, value);
        });
    }

    [Fact]
    public void NativeDoubleSliderActuallyEditsWithoutChangingTheDescriptor()
    {
        InContext(() =>
        {
            double value = 0;
            var editor = ToolDoubleEditor.Slider(0, 10, "%.3f");
            float2 minimum = default, maximum = default;
            void Draw()
            {
                Ui.SetNextItemWidth(300);
                ToolControls.EditDouble("value"u8, ref value, editor);
                minimum = Ui.GetItemRectMin(); maximum = Ui.GetItemRectMax();
            }
            Frame(600, Draw); Frame(600, Draw);
            var io = Ui.GetIO(); io.AddMousePosEvent(minimum.X + (maximum.X - minimum.X) * 0.75f, (minimum.Y + maximum.Y) / 2);
            Frame(600, Draw); io.AddMouseButtonEvent(0, true); Frame(600, Draw);
            io.AddMouseButtonEvent(0, false); Frame(600, Draw);
            Assert.InRange(value, 6, 9);
            Assert.Equal(0, editor.Minimum); Assert.Equal(10, editor.Maximum);
        });
    }

    [Fact]
    public void NativeDragCtrlClickExactEntryIsNotSilentlyClampedToTheUsefulRange()
    {
        InContext(() =>
        {
            double value = 1;
            var editor = ToolDoubleEditor.Drag(0, 10, 0.1f, "%.17g");
            float2 point = default;
            void Draw()
            {
                Ui.SetNextItemWidth(300);
                ToolControls.EditDouble("value"u8, ref value, editor);
                point = (Ui.GetItemRectMin() + Ui.GetItemRectMax()) / 2;
            }
            Frame(600, Draw); Frame(600, Draw);
            var io = Ui.GetIO(); io.AddMousePosEvent(point.X, point.Y);
            io.AddKeyEvent(ImGuiKey.Mod_Ctrl, true); Frame(600, Draw);
            io.AddMouseButtonEvent(0, true); Frame(600, Draw);
            io.AddMouseButtonEvent(0, false); Frame(600, Draw);
            io.AddKeyEvent(ImGuiKey.Mod_Ctrl, false); Frame(600, Draw);
            io.AddInputCharactersUTF8("123.45678901234567"u8); Frame(600, Draw);
            io.AddKeyEvent(ImGuiKey.Enter, true); Frame(600, Draw);
            io.AddKeyEvent(ImGuiKey.Enter, false); Frame(600, Draw);
            Assert.Equal(123.45678901234567, value);
        });
    }

    [Theory]
    [InlineData(220, false)] [InlineData(900, true)]
    public void CachedPropertyMetadataWrapsAndScopesUnwindOnConsumerExceptions(int width, bool wide)
    {
        var property = new ToolProperty("simulation.speed", "A long primary label that must not be mixed with loud repeated effect tags",
            "units/s", "Live", new("Speed", "Applied by the owner update.", "Drag or Ctrl-click for exact input."));
        Assert.Equal("units/s · Live", System.Text.Encoding.UTF8.GetString(property.MetadataUtf8));
        InContext(() => Frame(width, () =>
        {
            var before = Ui.GetID("sentinel"u8);
            var textColor = Ui.GetStyle().Colors[(int)ImGuiCol.Text];
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var row = ToolLayout.PropertyRow(property, 140);
                Assert.Equal(wide, row.IsWide);
                if (row.ContentVisible) { double value = 1; ToolControls.EditDouble("##value"u8, ref value, ToolDoubleEditor.Input("%.3f")); }
                throw new InvalidOperationException("application UI");
            }));
            Assert.True(Ui.GetCurrentContext().CurrentTable.IsNull());
            Assert.Equal(before, Ui.GetID("sentinel"u8));
            Assert.Equal(textColor, Ui.GetStyle().Colors[(int)ImGuiCol.Text]);
        }));
    }

    [Fact]
    public void StructuredHelpUsesDisabledHoverAndDoesNotLeakStyleOrWindowScopes()
    {
        InContext(() =>
        {
            var help = new ToolHelp("Unavailable setting", "The configured value remains visible.", "Resume first.", "Paused by the application.");
            bool shown = false;
            float2 point = default;
            void Draw()
            {
                int windows = Ui.GetCurrentContext().CurrentWindowStack.Count;
                var color = Ui.GetStyle().Colors[(int)ImGuiCol.Text];
                Ui.BeginDisabled();
                try
                {
                    Ui.Button("Unavailable"u8);
                    point = (Ui.GetItemRectMin() + Ui.GetItemRectMax()) / 2;
                    shown |= help.ShowForLastItem(disabled: true);
                }
                finally { Ui.EndDisabled(); }
                help.DrawDisabledReason();
                Assert.Equal(windows, Ui.GetCurrentContext().CurrentWindowStack.Count);
                Assert.Equal(color, Ui.GetStyle().Colors[(int)ImGuiCol.Text]);
            }
            Frame(600, Draw); Frame(600, Draw);
            Ui.GetIO().AddMousePosEvent(point.X, point.Y);
            for (int i = 0; i < 30; i++) Frame(600, Draw);
            Assert.True(shown);
        });
    }

    [Fact]
    public void DisabledNumericControlsRetainValuesAndVisibleReason()
    {
        InContext(() => Frame(600, () =>
        {
            double real = 12.5; long integer = long.MaxValue;
            var help = new ToolHelp("Value", "Kept while unavailable.", disabledReason: "Resume to edit.");
            Assert.False(ToolControls.EditDouble("real"u8, ref real, ToolDoubleEditor.Input("%.3f"), false, help));
            Assert.False(ToolControls.EditInt64("integer"u8, ref integer, ToolInt64Editor.Input(), false, help));
            Assert.Equal(12.5, real); Assert.Equal(long.MaxValue, integer);
            Assert.False(Ui.GetCurrentContext().CurrentWindow.IsNull());
            Ui.Button("Still enabled"u8);
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VisibleActionButtonOpensPopupAndReturnsRestoreRequestWithoutExecutingIt(bool sourceCaption)
    {
        InContext(() =>
        {
            float2 button = default;
            ToolFieldAction action = default;
            bool popup = false;
            void Draw()
            {
                Ui.Text("Application value"u8);
                action = ToolFieldActions.Draw("field"u8, true, true,
                    restoreLabel: sourceCaption ? "Restore from source"u8 : default);
                button = (Ui.GetItemRectMin() + Ui.GetItemRectMax()) / 2;
                Ui.PushID("field"u8);
                try { popup = Ui.IsPopupOpen("##actions"u8); }
                finally { Ui.PopID(); }
            }
            Frame(600, Draw); Frame(600, Draw);
            var io = Ui.GetIO(); io.AddMousePosEvent(button.X, button.Y); Frame(600, Draw);
            io.AddMouseButtonEvent(0, true); Frame(600, Draw);
            io.AddMouseButtonEvent(0, false); Frame(600, Draw); Frame(600, Draw);
            Assert.True(popup);
            var popupWindow = Ui.GetCurrentContext().OpenPopupStack[0].Window;
            var item = popupWindow.Pos + Ui.GetStyle().WindowPadding + new float2(40, Ui.GetFontSize() / 2);
            io.AddMousePosEvent(item.X, item.Y); Frame(600, Draw);
            io.AddMouseButtonEvent(0, true); Frame(600, Draw);
            io.AddMouseButtonEvent(0, false); Frame(600, Draw);
            Assert.Equal(ToolFieldActionKind.Restore, action.Kind);
        });
    }

    [Fact]
    public void PresetPopupUsesCallerOrderAndDistinctIdsForRepeatedLabels()
    {
        InContext(() =>
        {
            ToolFieldPreset[] presets = [new("Useful value"), new("Useful value")];
            float2 button = default;
            ToolFieldAction action = default;
            bool popup = false;
            void Draw()
            {
                Ui.Text("Application value"u8);
                action = ToolFieldActions.Draw("field"u8, true, true, presets);
                if (!popup) button = (Ui.GetItemRectMin() + Ui.GetItemRectMax()) / 2;
                Ui.PushID("field"u8);
                try { popup = Ui.IsPopupOpen("##actions"u8); }
                finally { Ui.PopID(); }
            }
            Frame(600, Draw); Frame(600, Draw);
            var io = Ui.GetIO(); io.AddMousePosEvent(button.X, button.Y); Frame(600, Draw);
            io.AddMouseButtonEvent(0, true); Frame(600, Draw);
            io.AddMouseButtonEvent(0, false); Frame(600, Draw); Frame(600, Draw);
            Assert.True(popup);
            var window = Ui.GetCurrentContext().OpenPopupStack[0].Window;
            var lastItem = new float2(window.Pos.X + Ui.GetStyle().WindowPadding.X + 40,
                window.Pos.Y + window.Size.Y - Ui.GetStyle().WindowPadding.Y - Ui.GetFontSize() / 2);
            io.AddMousePosEvent(lastItem.X, lastItem.Y); Frame(600, Draw);
            io.AddMouseButtonEvent(0, true); Frame(600, Draw);
            io.AddMouseButtonEvent(0, false); Frame(600, Draw);
            Assert.Equal(new ToolFieldAction(ToolFieldActionKind.Preset, 1), action);
        });
    }

    [Fact]
    public void InvalidPopupPresetUnwindsNativeWindowAndIdScopes()
    {
        InContext(() =>
        {
            ToolFieldPreset[] invalid = [null!];
            bool threw = false;
            for (int i = 0; i < 3; i++) Frame(600, () =>
            {
                var before = Ui.GetID("sentinel"u8);
                int windows = Ui.GetCurrentContext().CurrentWindowStack.Count;
                Ui.PushID("field"u8); Ui.OpenPopup("##actions"u8); Ui.PopID();
                try { ToolFieldActions.Draw("field"u8, true, true, invalid); }
                catch (ArgumentNullException) { threw = true; }
                Assert.Equal(before, Ui.GetID("sentinel"u8));
                Assert.Equal(windows, Ui.GetCurrentContext().CurrentWindowStack.Count);
                Ui.Button("After invalid menu"u8);
            });
            Assert.True(threw);
        });
    }

    [Theory]
    [InlineData(90)] [InlineData(600)]
    public void ActionAffordanceRemainsVisibleAtNarrowWidthsAndInvalidIdsDoNotChangeScopes(int width)
    {
        InContext(() => Frame(width, () =>
        {
            ToolFieldActions.ReserveControlWidth();
            double value = 1; ToolControls.EditDouble("value"u8, ref value, ToolDoubleEditor.Input("%.3f"));
            var before = Ui.GetID("sentinel"u8);
            Assert.Equal(default, ToolFieldActions.Draw("field"u8, false, false));
            Assert.True(Ui.IsItemVisible());
            Assert.Throws<ArgumentException>(() => ToolFieldActions.Draw(default, true, true));
            Assert.Equal(before, Ui.GetID("sentinel"u8));
        }));
    }

    [Fact]
    public void CachedDescriptionsAndClosedActionsAvoidPerFrameManagedArrays()
    {
        InContext(() =>
        {
            var property = new ToolProperty("play.speed", "Speed", "units/s", "Live");
            var editor = ToolDoubleEditor.Drag(0, 100, 1, "%.3f");
            ToolFieldPreset[] presets = [new("Slow"), new("Fast")];
            double value = 1;
            Action draw = () =>
            {
                using var row = ToolLayout.PropertyRow(property, 140);
                if (!row.ContentVisible) return;
                ToolFieldActions.ReserveControlWidth();
                ToolControls.EditDouble("##value"u8, ref value, editor);
                ToolFieldActions.Draw(property.IdUtf8, true, true, presets);
            };
            for (int i = 0; i < 20; i++) Frame(600, draw);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) Frame(600, draw);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.InRange(allocated, 0, 1024); // Runtime latitude, but no per-frame encoded labels/arrays.
        });
    }

    private static void InContext(Action draw)
    {
        var previous = Ui.GetCurrentContext(); var context = Ui.CreateContext();
        try
        {
            var io = Ui.GetIO(); io.IniFilename = default; io.LogFilename = default;
            io.DisplaySize = new float2(1400, 900); io.DeltaTime = 1f / 30;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
            draw();
        }
        finally { Ui.DestroyContext(context); Ui.SetCurrentContext(previous); }
    }

    private static void Frame(float width, Action draw)
    {
        Ui.NewFrame();
        try
        {
            Ui.SetNextWindowPos(new float2(0), ImGuiCond.Always);
            Ui.SetNextWindowSize(new float2(width, 800), ImGuiCond.Always);
            Ui.Begin("Tool control tests"u8, ImGuiWindowFlags.NoSavedSettings);
            try { draw(); }
            finally { Ui.End(); }
        }
        finally { Ui.EndFrame(); }
    }
}
