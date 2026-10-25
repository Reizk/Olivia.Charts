namespace Olivia.Core.Charts;

/// <summary>Unpremultiplied sRGB paint, independent of any UI framework.</summary>
public readonly record struct SurfaceColor(byte R, byte G, byte B, byte A = 255)
{
    internal (byte B, byte G, byte R, byte A) Bgra => (B, G, R, A);
}

/// <summary>Immutable three-color palette and optional diffuse box material. The reference
/// sorts its Min/0/Max stops before assigning palette indices, regardless of insertion order.
/// Exact duplicate stops collapse; interpolation is in sRGB channels, not a native shader.</summary>
public sealed record SurfaceMaterial(SurfaceColor First, SurfaceColor Second, SurfaceColor Third,
    SurfaceColor? Domain = null)
{
    internal (byte B, byte G, byte R, byte A) ColorAt(double value, double minimum, double maximum)
    {
        var low = Math.Min(0, minimum);
        var middle = Math.Clamp(0, minimum, maximum);
        var high = Math.Max(0, maximum);
        if (low == high || value <= low) return First.Bgra;
        if (low == middle || middle == high)
            return Mix(First, Second, Fraction(value, low, high));
        return value <= middle ? Mix(First, Second, Fraction(value, low, middle))
            : Mix(Second, Third, Fraction(value, middle, high));
    }

    private static double Fraction(double value, double low, double high)
    {
        if (value <= low) return 0;
        if (value >= high) return 1;
        var range = high - low;
        return double.IsFinite(range) ? (value - low) / range
            : (value / 2 - low / 2) / (high / 2 - low / 2);
    }

    private static (byte B, byte G, byte R, byte A) Mix(SurfaceColor a, SurfaceColor b, double t)
    {
        byte Channel(byte first, byte second) => (byte)Math.Round(first + (second - first) * t);
        return (Channel(a.B, b.B), Channel(a.G, b.G), Channel(a.R, b.R), Channel(a.A, b.A));
    }
}
