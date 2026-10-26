using CrushKit.Brutal.Draw2D;
using CrushKit.Brutal.Vulkan;
using System.Runtime.InteropServices;
using Xunit;

namespace Vulkan.Tests;

public class BakedFrameTests
{
    [Fact]
    public void BakedFrameRetainsCoverageAndRejectsCorruptedDynamicPayload()
    {
        using var draw=new ShapeDrawList();using var baked=new OrderedShapeDrawList(new(4,100_000,100_000,100,100));
        draw.BeginFrame(1280,720);baked.BeginFrame();draw.FillRect(new(0),new(8),new(1,0,0));
        baked.Disc(draw,new(100),4,new(0,.5f,1,.5f));draw.FillRect(new(20),new(8),new(0,1,0));
        var f=baked.Finish(draw);VulkanRenderer.ValidateBaked(f);
        Assert.True(MemoryMarshal.TryGetArray(f.Instances,out var values));var saved=values.Array![values.Offset];
        values.Array[values.Offset]=new(new(float.NaN,0),saved.AxisX,saved.AxisY,saved.Color);
        Assert.Throws<ArgumentException>(()=>VulkanRenderer.ValidateBaked(f));values.Array[values.Offset]=saved;
        Assert.True(MemoryMarshal.TryGetArray(f.Operations,out var operations));var original=operations.Array![operations.Offset];
        operations.Array[operations.Offset]=original with{Clip=new(1,2,4,8)};
        Assert.Throws<ArgumentException>(()=>VulkanRenderer.ValidateBaked(f));operations.Array[operations.Offset]=original;
        VulkanRenderer.ValidateBaked(f);
        MeshInstance[] unsupported=[
            new(saved.Origin,new(-4,0),new(0,-4),saved.Color),
            new(saved.Origin,new(4,0),new(0,5),saved.Color),
            new(saved.Origin,new(4,.125f),new(0,4),saved.Color),
            new(saved.Origin,new(MathF.ScaleB(1,-127),0),new(0,MathF.ScaleB(1,-127)),saved.Color)];
        foreach(var bad in unsupported)
        {
            values.Array[values.Offset]=bad;
            Assert.Throws<ArgumentException>(()=>VulkanRenderer.ValidateBaked(f));
        }
        values.Array[values.Offset]=saved;VulkanRenderer.ValidateBaked(f);
    }
    [Fact]
    public void BakedAbiAndExplicitLimitsAreGuarded()
    {
        Assert.Equal(40,Marshal.SizeOf<MeshInstance>());Assert.Equal(24,Marshal.OffsetOf<MeshInstance>(nameof(MeshInstance.Color)).ToInt32());
        Assert.Throws<ArgumentOutOfRangeException>(()=>new BakedMeshLimits(2,100,99,1,1));
        Assert.Throws<ArgumentException>(()=>VulkanRenderer.ValidateBaked(default));
    }
}
