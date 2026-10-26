using System.Reflection;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using CrushKit.Brutal.Vulkan;
using Xunit;

namespace CrushKit.Tests.Vulkan;

public sealed class PresentationPolicyTests
{
    [Fact]
    public void SelectsOnlyExplicitSupportedModes()
    {
        VkPresentModeKHR[] all = [VkPresentModeKHR.FifoKHR, VkPresentModeKHR.ImmediateKHR, VkPresentModeKHR.MailboxKHR];
        Assert.Equal(VkPresentModeKHR.FifoKHR, VulkanRenderer.SelectPresentMode(true, all));
        Assert.Equal(VkPresentModeKHR.ImmediateKHR, VulkanRenderer.SelectPresentMode(false, all));
        Assert.Throws<NotSupportedException>(() => VulkanRenderer.SelectPresentMode(false, [VkPresentModeKHR.FifoKHR, VkPresentModeKHR.MailboxKHR]));
        Assert.Throws<NotSupportedException>(() => VulkanRenderer.SelectPresentMode(true, []));
    }

    [Fact]
    public void PreflightRejectionNoOpOwnerAndDisposalAreGuardedWithoutGpu()
    {
        using var renderer = (VulkanRenderer)Activator.CreateInstance(typeof(VulkanRenderer),
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { default(GlfwWindow)! }, null)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(VulkanRenderer).GetField("_presentModes", flags)!.SetValue(renderer, new[] { VkPresentModeKHR.FifoKHR });
        var recreate = typeof(VulkanRenderer).GetField("_recreate", flags)!;
        recreate.SetValue(renderer, false);
        renderer.ValidateVSync(true);
        renderer.SetVSync(true);
        Assert.False((bool)recreate.GetValue(renderer)!);
        Assert.Throws<NotSupportedException>(() => renderer.SetVSync(false));
        Assert.True(renderer.VSync); Assert.Null(renderer.EffectivePresentMode);
        Assert.False((bool)recreate.GetValue(renderer)!);
        // A dedicated thread guarantees this is not the owner even under synchronous test schedulers.
        Exception? error = null;
        var thread = new Thread(() => { try { renderer.SetVSync(true); } catch (Exception e) { error = e; } });
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(error);
        typeof(VulkanRenderer).GetField("_presentModes", flags)!.SetValue(renderer, new[] { VkPresentModeKHR.FifoKHR, VkPresentModeKHR.ImmediateKHR });
        renderer.SetVSync(false);
        Assert.False(renderer.VSync); Assert.True((bool)recreate.GetValue(renderer)!);
        recreate.SetValue(renderer, false);
        renderer.SetVSync(false);
        Assert.False((bool)recreate.GetValue(renderer)!);
        renderer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.ValidateVSync(true));
        Assert.Throws<ObjectDisposedException>(() => renderer.SetVSync(false));
    }
}
