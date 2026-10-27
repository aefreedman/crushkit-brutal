using Brutal.ImGuiApi;
using Brutal.Numerics;
using CrushKit.Brutal.ImGui;
using Xunit;
using Ui = Brutal.ImGuiApi.ImGui;

namespace CrushKit.Tests.ImGui;

public sealed class ToolFeedbackTests
{
    [Fact]
    public void LimitsAndEventValidationAreExplicitAndTransactional()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolFeedbackBuffer(0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolFeedbackBuffer(ToolFeedbackBuffer.MaximumCapacity + 1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolFeedbackBuffer(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolFeedbackBuffer(2, 1));
        var minimal = new ToolFeedbackBuffer(1, ToolFeedbackBuffer.MinimumTextCharacters);
        minimal.Post("a", ToolFeedbackSeverity.Info, "B", "", 0, 1);
        Assert.Equal(1, minimal.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolFeedbackBuffer(2, ToolFeedbackBuffer.MaximumTextCharacters + 1));
        var buffer = new ToolFeedbackBuffer(2, 32);
        var retained = buffer.Post("save", ToolFeedbackSeverity.Success, "Saved", "", 1, 10);
        Assert.Equal(2, buffer.Capacity); Assert.Equal(32, buffer.MaxTextCharacters);
        foreach (string id in new[] { "", " ", "bad\0id", "bad##id" })
            Assert.Throws<ArgumentException>(() => buffer.Post(id, ToolFeedbackSeverity.Info, "title", "", 100, 2));
        Assert.Throws<ArgumentException>(() => buffer.Post("id", ToolFeedbackSeverity.Info, "", "", 100, 2));
        Assert.Throws<ArgumentException>(() => buffer.Post("id", ToolFeedbackSeverity.Info, "title\0", "", 100, 2));
        Assert.Throws<ArgumentException>(() => buffer.Post("id", ToolFeedbackSeverity.Info, "title", "\0", 100, 2));
        Assert.Throws<ArgumentException>(() => buffer.Post("id", ToolFeedbackSeverity.Info, new string('x', 31), "", 100, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Post("id", (ToolFeedbackSeverity)99, "title", "", 100, 2));
        foreach (double duration in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Post("id", ToolFeedbackSeverity.Info, "title", "", 100, duration));
        Assert.Throws<System.Text.EncoderFallbackException>(() => buffer.Post("id", ToolFeedbackSeverity.Info, "\uD800", "", 100, 2));
        Assert.Same(retained, buffer[0]);
        buffer.Post("next", ToolFeedbackSeverity.Info, "Next", "", 2, 5); // Invalid future events did not advance the clock.
        Assert.Equal(2, buffer.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[2]);
    }

    [Fact]
    public void HostClockIsExplicitMonotonicAndNeedsRepresentableDeadlines()
    {
        var buffer = new ToolFeedbackBuffer(2, 100);
        foreach (double now in new[] { -1, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(now));
        buffer.Post("a", ToolFeedbackSeverity.Info, "A", "", 5, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Post("b", ToolFeedbackSeverity.Info, "B", "", 4, 2));
        buffer.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Advance(0)); // Clear is not a hidden clock reset.
        buffer.Advance(5);
        var large = new ToolFeedbackBuffer(1, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => large.Post("a", ToolFeedbackSeverity.Info, "A", "", double.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => large.Post("a", ToolFeedbackSeverity.Info, "A", "", double.MaxValue, double.MaxValue));
        large.Post("a", ToolFeedbackSeverity.Info, "A", "", 0, 1); // Invalid huge time was not published.
    }

    [Fact]
    public void SameIdCoalescesMovesToNewestAndRetainedPublicationsStayImmutable()
    {
        var buffer = new ToolFeedbackBuffer(3, 100);
        var original = buffer.Post("save", ToolFeedbackSeverity.Info, "Saving", "old detail", 0, 2);
        var other = buffer.Post("reload", ToolFeedbackSeverity.Success, "Reloaded", "", 0, 20);
        var replacement = buffer.Post("save", ToolFeedbackSeverity.Success, "Saved", "new detail", 1, 10);
        Assert.Equal(2, buffer.Count);
        Assert.Same(other, buffer[0]); Assert.Same(replacement, buffer[1]);
        Assert.Equal("Saving", original.Title); Assert.Equal("old detail", original.Detail);
        Assert.Equal(ToolFeedbackSeverity.Info, original.Severity);
        Assert.Equal(2, original.ExpiresAtSeconds);
        Assert.Equal(1, replacement.CreatedAtSeconds); Assert.Equal(11, replacement.ExpiresAtSeconds);
        Assert.False(buffer.Dismiss("missing"));
        Assert.True(buffer.Dismiss("save")); Assert.Equal(1, buffer.Count);
        buffer.Clear(); Assert.Equal(0, buffer.Count);
        Assert.Equal("Saved", replacement.Title); Assert.Equal("new detail", replacement.Detail);
    }

    [Fact]
    public void ExpiryAtDeadlinePreservesSurvivorOrderAndPrecedesCapacityEviction()
    {
        var buffer = new ToolFeedbackBuffer(3, 100);
        var a = buffer.Post("a", ToolFeedbackSeverity.Info, "A", "", 0, 10);
        var b = buffer.Post("b", ToolFeedbackSeverity.Warning, "B", "", 1, 2);
        var c = buffer.Post("c", ToolFeedbackSeverity.Success, "C", "", 2, 4);
        Assert.Equal(1, buffer.Advance(3));
        Assert.Same(a, buffer[0]); Assert.Same(c, buffer[1]);
        var d = buffer.Post("d", ToolFeedbackSeverity.Info, "D", "", 6, 5); // C expires; A must not be evicted.
        Assert.Equal(2, buffer.Count); Assert.Same(a, buffer[0]); Assert.Same(d, buffer[1]);
        Assert.Equal(1, buffer.Advance(10)); Assert.Same(d, buffer[0]);
        Assert.Equal(1, buffer.Advance(11)); Assert.Equal(0, buffer.Count);
        Assert.Equal("B", b.Title); // Expiry does not mutate retained objects.
    }

    [Fact]
    public void SaturationEvictsOldestRegardlessOfPresentationSeverity()
    {
        var buffer = new ToolFeedbackBuffer(2, 100);
        var error = buffer.Post("error", ToolFeedbackSeverity.Error, "Rejected", "persistent error belongs to app", 0, 50);
        var success = buffer.Post("success", ToolFeedbackSeverity.Success, "Saved", "", 0, 50);
        var info = buffer.Post("info", ToolFeedbackSeverity.Info, "Reloaded", "", 1, 50);
        Assert.Equal(2, buffer.Count); Assert.Same(success, buffer[0]); Assert.Same(info, buffer[1]);
        Assert.False(buffer.Dismiss(error.Id));
        buffer.Post("success", ToolFeedbackSeverity.Success, "Saved again", "", 1, 50);
        Assert.Equal("info", buffer[0].Id); Assert.Equal("success", buffer[1].Id);
        var single = new ToolFeedbackBuffer(1, 100);
        for (int i = 0; i < 100; i++) single.Post(i.ToString(), ToolFeedbackSeverity.Info, "Event", "", i, 5);
        Assert.Equal(1, single.Count); Assert.Equal("99", single[0].Id);
    }

    [Fact]
    public void OwnerUpdatesAndObservationNeedNoPerFrameAllocations()
    {
        var buffer = new ToolFeedbackBuffer(4, 100);
        buffer.Post("save", ToolFeedbackSeverity.Success, "Saved", "", 0, 100);
        for (int i = 0; i < 100; i++) { buffer.Advance(1); _ = buffer[0]; }
        long before = GC.GetAllocatedBytesForCurrentThread();
        int characters = 0;
        for (int i = 0; i < 10000; i++) { buffer.Advance(1); characters += buffer[0].Title.Length; }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(50000, characters);
        Assert.InRange(bytes, 0, 256);
        Assert.Equal(1, buffer.Count);
    }

    [Theory]
    [InlineData(180)] [InlineData(900)]
    public void InlineViewWrapsAndActualDismissButtonReturnsRequestWithoutMutatingBuffer(int width)
    {
        var buffer = new ToolFeedbackBuffer(2, 100);
        var notice = buffer.Post("save", ToolFeedbackSeverity.Success, "Selected configuration sources saved", "", 0, 5);
        InContext(() =>
        {
            float2 center = default;
            bool Draw()
            {
                var before = Ui.GetID("sentinel"u8);
                bool dismiss = ToolFeedbackView.DrawNotice(notice, true);
                center = (Ui.GetItemRectMin() + Ui.GetItemRectMax()) / 2;
                Assert.Equal(before, Ui.GetID("sentinel"u8));
                Assert.True(Ui.GetItemRectMax().X > Ui.GetItemRectMin().X);
                return dismiss;
            }
            Frame(width, Draw); Frame(width, Draw);
            var io = Ui.GetIO(); io.AddMousePosEvent(center.X, center.Y);
            Frame(width, Draw);
            io.AddMouseButtonEvent(0, true); Frame(width, Draw);
            io.AddMouseButtonEvent(0, false); Assert.True(Frame(width, Draw));
            Assert.Equal(1, buffer.Count); // Drawing returns intent; it never consumes or expires the model.
            Assert.True(buffer.Dismiss(notice.Id)); Assert.Equal(0, buffer.Count);
        });
    }

    [Fact]
    public void NondismissibleDetailAndInvalidNoticeKeepNativeIdsBalanced()
    {
        var buffer = new ToolFeedbackBuffer(1, 200);
        var notice = buffer.Post("reload", ToolFeedbackSeverity.Info, "Reloaded", "A longer detail wraps inside a narrow caller-owned window.", 0, 1);
        InContext(() => Frame(180, () =>
        {
            var before = Ui.GetID("sentinel"u8);
            Assert.False(ToolFeedbackView.DrawNotice(notice, false));
            Assert.Equal(before, Ui.GetID("sentinel"u8));
            Assert.Throws<ArgumentNullException>(() => ToolFeedbackView.DrawNotice(null!, true));
            Assert.Equal(before, Ui.GetID("sentinel"u8));
            Assert.Equal(1, buffer.Count);
            return false;
        }));
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

    private static bool Frame(float width, Func<bool> draw)
    {
        Ui.NewFrame();
        try
        {
            Ui.SetNextWindowPos(new float2(0), ImGuiCond.Always);
            Ui.SetNextWindowSize(new float2(width, 400), ImGuiCond.Always);
            Ui.Begin("Feedback tests"u8, ImGuiWindowFlags.NoSavedSettings);
            try { return draw(); }
            finally { Ui.End(); }
        }
        finally { Ui.EndFrame(); }
    }
}
