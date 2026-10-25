namespace Olivia.Core.Charts;

/// <summary>Fit the normalized data-box bounding sphere to the shorter viewport axis.
/// A positive vertical scroll percent moves the data upward, as in the reference demo.</summary>
public static class SurfaceCameraLayout
{
    public static Point3DCamera Create(SurfaceChartSource source, double minimumValue, double maximumValue,
        double width, double height, bool perspective = true, double verticalScrollPercent = 10)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!double.IsFinite(minimumValue) || !double.IsFinite(maximumValue) || minimumValue > maximumValue)
            throw new ArgumentOutOfRangeException(nameof(minimumValue));
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(verticalScrollPercent) || Math.Abs(verticalScrollPercent) > 25)
            throw new ArgumentOutOfRangeException(nameof(verticalScrollPercent));
        var x = source.XArguments[^1] / 2 - source.XArguments[0] / 2;
        var y = maximumValue / 2 - minimumValue / 2;
        var z = source.YArguments[^1] / 2 - source.YArguments[0] / 2;
        var scale = Math.Max(x, Math.Max(y, z));
        var radius = scale > 0 ? Math.Sqrt(Math.Pow(x / scale, 2) + Math.Pow(y / scale, 2) + Math.Pow(z / scale, 2)) : 1;
        const double halfAngle = Math.PI / 8;
        var distance = perspective ? radius / Math.Sin(halfAngle) : 2 * radius;
        var extent = perspective ? distance * Math.Tan(halfAngle) : radius;
        var halfHeight = Math.Clamp(extent * Math.Max(1, height / width), .01, 16);
        const double yaw = 40 * Math.PI / 180, pitch = 20 * Math.PI / 180;
        var offset = -.02 * verticalScrollPercent * halfHeight;
        var target = new Point3DVector(-Math.Sin(yaw) * Math.Sin(pitch) * offset,
            Math.Cos(pitch) * offset, -Math.Cos(yaw) * Math.Sin(pitch) * offset);
        return new(yaw, pitch, target, halfHeight, distance);
    }
}
