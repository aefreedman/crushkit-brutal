using Brutal.ImGuiApi;
using Brutal.Numerics;
using CrushKit.Brutal.ImGui;
using Xunit;
using Ui = Brutal.ImGuiApi.ImGui;

// All tests in this assembly use ImGui's process-global native current context.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CrushKit.Tests.ImGui;

public sealed class ToolLayoutTests
{
    private static readonly ToolCategory[] Categories = [new("play", "Gameplay"), new("render", "Rendering")];

    [Theory]
    [InlineData(900, true)]
    [InlineData(220, false)]
    public void CategoryAndPropertyLayoutsAdaptToAvailableWidth(int width, bool wide)
    {
        InContext(() => Frame(width, () =>
        {
            int selected = 1;
            using (var body = ToolLayout.Categories("settings"u8, Categories, ref selected, 250))
            {
                Assert.Equal(wide, body.IsWide);
                Assert.True(body.ContentVisible);
                Assert.Equal(1, selected);
                using var row = ToolLayout.PropertyRow("speed"u8, "Speed"u8, 180, "World units per second"u8);
                Assert.Equal(wide, row.IsWide);
                if (row.ContentVisible) { float value = 1; Ui.SliderFloat("##value"u8, ref value, 0, 10); }
            }
            Assert.True(Ui.GetCurrentContext().CurrentTable.IsNull());
        }));
    }

    [Fact]
    public void LongLabelWrapsWithoutTakingControlWidth()
    {
        InContext(() => Frame(600, () =>
        {
            using var row = ToolLayout.PropertyRow("long"u8,
                "A deliberately long descriptive property label that must wrap rather than squeeze its control"u8, 180);
            Assert.True(row.IsWide);
            Assert.True(row.ContentVisible);
            Assert.True(Ui.GetContentRegionAvail().X >= 180);
            float value = 1; Ui.SliderFloat("##value"u8, ref value, 0, 10);
        }));
    }

    [Fact]
    public void SiblingControlsAlignIndependentlyOfLabelLength()
    {
        InContext(() => Frame(600, () =>
        {
            float firstX, secondX;
            using (var row = ToolLayout.PropertyRow("speed"u8, "Speed"u8, 180))
            {
                firstX = Ui.GetCursorScreenPos().X;
                Ui.Button("##value"u8);
            }
            using (var row = ToolLayout.PropertyRow("limit"u8, "Maximum active particle count"u8, 180))
            {
                secondX = Ui.GetCursorScreenPos().X;
                Ui.Button("##value"u8);
            }
            Assert.Equal(firstX, secondX);
        }));
    }

    [Fact]
    public void ScopesUnwindTablesAndIdsAfterConsumerExceptions()
    {
        InContext(() => Frame(900, () =>
        {
            var idBefore = Ui.GetID("sentinel"u8);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                int selected = 0;
                using var body = ToolLayout.Categories("settings"u8, Categories, ref selected, 250);
                using var row = ToolLayout.PropertyRow("speed"u8, "Speed"u8, 140);
                throw new InvalidOperationException("consumer control failed");
            }));
            Assert.True(Ui.GetCurrentContext().CurrentTable.IsNull());
            Assert.Equal(idBefore, Ui.GetID("sentinel"u8));
            Ui.Button("After exception"u8);
        }));
    }

    [Fact]
    public void StablePropertyIdsDoNotDependOnLabelOrResponsiveMode()
    {
        InContext(() =>
        {
            ImGuiID first = default, narrow = default, changedLabel = default;
            Frame(900, () => { using var row = ToolLayout.PropertyRow("speed"u8, "Speed"u8, 180); first = Ui.GetID("##value"u8); });
            Frame(220, () => { using var row = ToolLayout.PropertyRow("speed"u8, "Speed"u8, 180); narrow = Ui.GetID("##value"u8); });
            Frame(900, () => { using var row = ToolLayout.PropertyRow("speed"u8, "Renamed speed"u8, 180); changedLabel = Ui.GetID("##value"u8); });
            Assert.Equal(first, narrow);
            Assert.Equal(first, changedLabel);
        });
    }

    [Fact]
    public void CategoryContentIdentitySurvivesResponsiveModeChanges()
    {
        InContext(() =>
        {
            int selected = 1;
            ImGuiID wide = default, narrow = default;
            Frame(900, () =>
            {
                using var body = ToolLayout.Categories("settings"u8, Categories, ref selected, 250);
                using var row = ToolLayout.PropertyRow("speed"u8, "Speed"u8, 180);
                wide = Ui.GetID("##value"u8);
            });
            Frame(220, () =>
            {
                using var body = ToolLayout.Categories("settings"u8, Categories, ref selected, 250);
                using var row = ToolLayout.PropertyRow("speed"u8, "Speed"u8, 180);
                narrow = Ui.GetID("##value"u8);
            });
            Assert.Equal(wide, narrow);
        });
    }

    [Fact]
    public void SelectionRemainsConsumerOwnedAcrossContextRecreation()
    {
        int selected = 1;
        ImGuiID first = default, second = default;
        InContext(() => Frame(900, () =>
        {
            using var body = ToolLayout.Categories("settings"u8, Categories, ref selected, 250);
            first = Ui.GetID("application-state"u8);
        }));
        InContext(() => Frame(900, () =>
        {
            using var body = ToolLayout.Categories("settings"u8, Categories, ref selected, 250);
            second = Ui.GetID("application-state"u8);
            Assert.Equal(1, selected);
        }));
        Assert.Equal(first, second);
    }

    [Fact]
    public void InvalidInputsAreRejectedBeforeNativeScopeChanges()
    {
        Assert.Throws<ArgumentException>(() => new ToolCategory("", "Gameplay"));
        Assert.Throws<ArgumentException>(() => new ToolCategory("game", "Hidden##identity"));
        InContext(() => Frame(900, () =>
        {
            var before = Ui.GetID("sentinel"u8);
            Assert.Throws<ArgumentException>(() => { using var row = ToolLayout.PropertyRow(default, "Speed"u8, 100); });
            Assert.Throws<ArgumentOutOfRangeException>(() => { using var row = ToolLayout.PropertyRow("speed"u8, "Speed"u8, float.NaN); });
            Assert.Throws<ArgumentOutOfRangeException>(() => { int selected = -1; using var body = ToolLayout.Categories("settings"u8, Categories, ref selected, 100); });
            Assert.Equal(before, Ui.GetID("sentinel"u8));
            Assert.True(Ui.GetCurrentContext().CurrentTable.IsNull());
        }));
    }

    [Fact]
    public void ActionFlowWrapsAtNarrowWidths()
    {
        InContext(() =>
        {
            Frame(600, () =>
            {
                Ui.Button("Save"u8); var first = Ui.GetItemRectMin();
                ToolLayout.SameLineIfFits("Discard"u8); Ui.Button("Discard"u8);
                Assert.Equal(first.Y, Ui.GetItemRectMin().Y);
            });
            Frame(110, () =>
            {
                Ui.Button("Save"u8); var first = Ui.GetItemRectMin();
                ToolLayout.SameLineIfFits("Discard"u8); Ui.Button("Discard"u8);
                Assert.True(Ui.GetItemRectMin().Y > first.Y);
            });
        });
    }

    private static void InContext(Action test)
    {
        var previous = Ui.GetCurrentContext();
        var context = Ui.CreateContext();
        try
        {
            var io = Ui.GetIO(); io.IniFilename = default; io.LogFilename = default;
            io.DisplaySize = new float2(1400, 900); io.DeltaTime = 1f / 60;
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
            test();
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
            Ui.Begin("Tool layout tests"u8, ImGuiWindowFlags.NoSavedSettings);
            try { draw(); }
            finally { Ui.End(); }
        }
        finally { Ui.EndFrame(); }
    }
}
