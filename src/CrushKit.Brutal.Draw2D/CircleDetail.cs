namespace CrushKit.Brutal.Draw2D;

public enum CircleDetailMode { Automatic, FixedSideCount }

/// <summary>Immutable visual tessellation choice, independent of collision geometry.</summary>
public sealed class CircleDetail
{
    public CircleDetailMode Mode { get; }
    public int FixedSides { get; }
    public CircleDetail(CircleDetailMode mode, int fixedSides)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (fixedSides is < 8 or > 4096 || fixedSides % 4 != 0)
            throw new ArgumentOutOfRangeException(nameof(fixedSides), "Circle fixed side count must be a multiple of four from 8 through 4096.");
        Mode = mode; FixedSides = fixedSides;
    }
}
