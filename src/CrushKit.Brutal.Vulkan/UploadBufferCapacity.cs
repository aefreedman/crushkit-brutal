namespace CrushKit.Brutal.Vulkan;

/// <summary>Private upload storage policy, independent of logical geometry counts.</summary>
internal static class UploadBufferCapacity
{
    internal static int Grow(int current, int required)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(current);
        ArgumentOutOfRangeException.ThrowIfNegative(required);
        if (required <= current) return current;
        // Round up geometrically, but do not overflow or reserve beyond the int byte domain.
        // Above the largest representable power of two, use exactly the requested size.
        int capacity = 1;
        while (capacity < required)
        {
            if (capacity > int.MaxValue / 2) return required;
            capacity = checked(capacity * 2);
        }
        return capacity;
    }
}
