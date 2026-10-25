namespace Olivia.Core.Charts;

/// <summary>Immutable rendering choices captured with the source/camera for one surface frame.
/// Perspective uses HalfHeight at Camera.Distance; the vertical field of view is
/// 2*atan(HalfHeight/Distance). Lighting affects color only, never depth or raw identity.</summary>
public sealed record SurfaceRenderingOptions(bool Perspective = false, bool Lighting = false)
{
    public static SurfaceRenderingOptions Legacy { get; } = new();
    public static SurfaceRenderingOptions LitPerspective { get; } = new(true, true);

    /// <summary>Null retains the original rainbow and transparent box. Captured by value
    /// with each render request, so later theme changes cannot recolor an old snapshot.</summary>
    public SurfaceMaterial? Material { get; init; }

    /// <summary>Four coverage samples at detected layer/occlusion/color boundaries.
    /// Only display pixels change; depth, original triangles and center-point hits do not.
    /// Off by default for existing CPU API callers. Private scratch is pooled and bounded.</summary>
    public bool EdgeAntialiasing { get; init; }

    private double _heightSideMarginFraction;
    /// <summary>Extra domain extent on each height end, as a fraction of the color range.
    /// Zero preserves the original API. The real-time reference uses 0.1; colors retain
    /// their original Min/0/Max stops. Expanded ranges must remain finite.</summary>
    public double HeightSideMarginFraction
    {
        get => _heightSideMarginFraction;
        init
        {
            if (!double.IsFinite(value) || value is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(HeightSideMarginFraction));
            _heightSideMarginFraction = value;
        }
    }

    public (double Minimum, double Maximum) HeightRange(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
        var margin = (maximum / 2 - minimum / 2) * (2 * HeightSideMarginFraction);
        var low = minimum - margin; var high = maximum + margin;
        if (!double.IsFinite(low) || !double.IsFinite(high))
            throw new ArgumentOutOfRangeException(nameof(minimum), "Expanded surface height range must be finite.");
        return (low, high);
    }

    internal double W(double depth, Point3DCamera camera) => Perspective ? depth / camera.Distance : 1;
}
