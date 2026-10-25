namespace Olivia.Core.Charts;

/// <summary>Two-sided Gouraud lighting with the reference's ambient RGB 105,
/// white directional light (1.1,-1,-1.8), specular RGB 75 and exponent 9.
/// WPF evaluates 3D lighting in sRGB, interpolates vertex colors in screen space,
/// and reflects normals for the triangle's back pass,
/// not independently according to each smoothed vertex normal's direction.</summary>
internal static class SurfaceLighting
{
    // Light travels along (1.1,-1,-1.8); normals point toward its opposite direction.
    private static readonly Point3DVector Light = Unit(new(-1.1, 1, 1.8));

    internal static (double Diffuse, double Specular) Evaluate(Point3DVector normal, Point3DVector relative,
        Point3DCamera camera, bool perspective, Point3DVector right, Point3DVector up, Point3DVector forward,
        bool? reverseNormal = null)
    {
        normal = Unit(normal);
        // Lighting stays fixed in view space as the object is rotated, matching the reference profile.
        normal = new(Dot(normal, right), Dot(normal, up), Dot(normal, forward));
        var view = perspective
            ? Unit(new(-Dot(relative, right), -Dot(relative, up), camera.Distance - Dot(relative, forward)))
            : new Point3DVector(0, 0, 1);
        if (reverseNormal ?? Dot(normal, view) < 0) normal = new(-normal.X, -normal.Y, -normal.Z);
        var diffuse = Math.Max(0, Dot(normal, Light));
        var specular = diffuse > 0 ? Math.Pow(Math.Max(0, Dot(normal, Unit(Add(Light, view)))), 9) : 0;
        // Diffuse is clamped before texture modulation, as in the reference material pass.
        return (Math.Min(1, 105 / 255d + diffuse), 75 / 255d * specular);
    }

    internal static (byte B, byte G, byte R, byte A) Shade((byte B, byte G, byte R, byte A) color,
        double diffuse, double specular)
    {
        byte Channel(byte channel) => (byte)Math.Round(Math.Clamp(channel * diffuse + 255 * specular, 0, 255));
        return (Channel(color.B), Channel(color.G), Channel(color.R), color.A);
    }

    internal static Point3DVector Add(Point3DVector a, Point3DVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    internal static Point3DVector Subtract(Point3DVector a, Point3DVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    internal static Point3DVector Cross(Point3DVector a, Point3DVector b) => new(
        a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Dot(Point3DVector a, Point3DVector b) => Point3DChartGeometry.Dot(a, b);
    internal static Point3DVector Unit(Point3DVector value)
    {
        var scale = Math.Max(Math.Abs(value.X), Math.Max(Math.Abs(value.Y), Math.Abs(value.Z)));
        if (scale == 0 || !double.IsFinite(scale)) return default;
        var x = value.X / scale; var y = value.Y / scale; var z = value.Z / scale;
        var length = Math.Sqrt(x * x + y * y + z * z);
        return new(x / length, y / length, z / length);
    }
}
