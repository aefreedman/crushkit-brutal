namespace CrushKit.Brutal.Draw2D;

/// <summary>Dash end boundaries in supplied path order and stroke measurement space.
/// Angled shifts arclength by modifier times signed transverse distance; Chevron uses its absolute
/// value. At magnitude one the straight-path boundary slope is 45 degrees (full-width angled
/// depth equals full width; chevron center-to-side depth equals half width). Sign mirrors direction.</summary>
public enum DashShape { Basic, Angled, Chevron }
