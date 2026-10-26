using CrushKit.Brutal.Vulkan;
using Xunit;

namespace Vulkan.Tests;

public class UploadBufferCapacityTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 3, 4)]
    [InlineData(64, 64, 64)]
    [InlineData(64, 0, 64)]
    [InlineData(64, 32, 64)]
    [InlineData(64, 65, 128)]
    [InlineData(64, 1000, 1024)]
    [InlineData(0, 1073741824, 1073741824)]
    [InlineData(1073741824, 1073741825, 1073741825)]
    [InlineData(0, int.MaxValue - 1, int.MaxValue - 1)]
    [InlineData(0, int.MaxValue, int.MaxValue)]
    public void CapacityIsBoundedAndNeverShrinks(int current, int required, int expected)
    {
        int result = UploadBufferCapacity.Grow(current, required);
        Assert.Equal(expected, result);
        Assert.True(result >= required && result >= current);
        if (required > current) Assert.True((long)result < 2L * required);
    }

    [Fact]
    public void GradualGrowthAmortizesReplacementsAndReuseNeedsNone()
    {
        int capacity = 0, replacements = 0;
        for (int bytes = 1; bytes <= 65536; bytes++)
        {
            int next = UploadBufferCapacity.Grow(capacity, bytes);
            if (capacity != next) replacements++;
            capacity = next;
            Assert.InRange(capacity, bytes, 2 * bytes - 1);
        }
        Assert.Equal(17, replacements); // exact-size policy would replace 65,536 times
        for (int bytes = 65536; bytes >= 0; bytes--)
            Assert.Equal(capacity, UploadBufferCapacity.Grow(capacity, bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadBufferCapacity.Grow(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => UploadBufferCapacity.Grow(0, -1));
    }
}
