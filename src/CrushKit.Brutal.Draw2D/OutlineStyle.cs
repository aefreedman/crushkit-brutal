namespace CrushKit.Brutal.Draw2D;

/// <summary>Width is full thickness, relative to the supplied boundary, independent of winding.</summary>
public enum StrokeAlignment { Center, Inside, Outside }

/// <summary>Local offsets precede transforms; framebuffer offsets follow both transforms in physical pixels.</summary>
public enum StrokeSpace { Local, Framebuffer }
