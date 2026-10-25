using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Independent bounded 1/2/2.5/5 numeric spacing and silhouette labels.
/// Matches the reference's far-face grid arrangement, not its proprietary text/layout engine.</summary>
internal static class SurfaceAxisGraduations
{
    internal static ImmutableArray<SurfaceAxisTick> Add(SurfaceRasterSnapshot raster,
        ReadOnlySpan<SurfaceAxisReferencePoint> corners, int farIndex, Point3DVector minimum, Point3DVector maximum,
        ImmutableArray<SurfaceAxisReferenceLine>.Builder lines, CancellationToken cancellationToken)
    {
        var ticks = ImmutableArray.CreateBuilder<SurfaceAxisTick>();
        for (var axis = 0; axis < 3; axis++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dimension = (SurfaceAxisDimension)axis;
            var bit = axis == 0 ? 1 : axis == 1 ? 4 : 2;
            var otherBits = 7 ^ bit;
            var low = axis == 0 ? minimum.X : axis == 1 ? minimum.Z : minimum.Y;
            var high = axis == 0 ? maximum.X : axis == 1 ? maximum.Z : maximum.Y;
            var best = -1; var score = double.NegativeInfinity;
            var outwardX = 0d; var outwardY = 0d;
            var center = SurfaceAxisReference.Lerp(corners[0], corners[7], .5);
            var screenCenter = center.Depth > raster.Camera.Near ? SurfaceAxisReference.Screen(center, raster)
                : new SurfaceAxisReferencePoint(raster.Width / 2d, raster.Height / 2d, raster.Camera.Near);
            // Edges shared by a far and a near face form the visible silhouette. The lower
            // argument edges and the left height edge keep labels outside the data volume.
            for (var i = 0; i < 8; i++)
            {
                if ((i & bit) != 0) continue;
                var matches = ~(i ^ farIndex) & otherBits;
                if (matches == 0 || matches == otherBits) continue;
                var a = corners[i]; var b = corners[i | bit];
                if (!SurfaceAxisReference.Clip(ref a, ref b, raster)) continue;
                a = SurfaceAxisReference.Screen(a, raster); b = SurfaceAxisReference.Screen(b, raster);
                var dx = b.X - a.X; var dy = b.Y - a.Y;
                var edgeLength = Math.Sqrt(dx * dx + dy * dy);
                if (edgeLength < 12) continue;
                var mx = (a.X + b.X) / 2; var my = (a.Y + b.Y) / 2;
                var candidate = axis == 2 ? -mx : my;
                if (candidate <= score) continue;
                var nx = -dy / edgeLength; var ny = dx / edgeLength;
                if (nx * (mx - screenCenter.X) + ny * (my - screenCenter.Y) < 0) { nx = -nx; ny = -ny; }
                best = i; score = candidate; outwardX = nx; outwardY = ny;
            }
            // Reference drawing box: longest unrotated side = 1000, spacing factors
            // 100 for arguments and 70 for height. Rotation/viewport do not alter values;
            // only visible labels are culled. WorldScale is half the longest raw side.
            var rawStep = raster.WorldScale * (axis == 2 ? .14 : .2);
            foreach (var value in Values(low, high, rawStep))
            {
                var t = Fraction(value, low, high);
                // Two grid segments, one on each far face parallel to this numeric axis.
                for (var fixedBit = 1; fixedBit <= 4; fixedBit <<= 1)
                {
                    if ((fixedBit & otherBits) == 0 || value == low || value == high) continue;
                    var varyingBit = otherBits ^ fixedBit;
                    var i = farIndex & fixedBit;
                    var a = SurfaceAxisReference.Lerp(corners[i], corners[i | bit], t);
                    var b = SurfaceAxisReference.Lerp(corners[i | varyingBit], corners[i | varyingBit | bit], t);
                    if (SurfaceAxisReference.Clip(ref a, ref b, raster))
                    {
                        var raw = (farIndex & fixedBit) == 0 ? minimum : maximum;
                        var coordinate = fixedBit == 1 ? raw.X : fixedBit == 2 ? raw.Y : raw.Z;
                        var centerCoordinate = fixedBit == 1 ? raster.WorldCenter.X : fixedBit == 2 ? raster.WorldCenter.Y : raster.WorldCenter.Z;
                        var difference = coordinate - centerCoordinate;
                        var normalized = double.IsFinite(difference) ? difference / raster.WorldScale
                            : coordinate / raster.WorldScale - centerCoordinate / raster.WorldScale;
                        lines.Add(new(SurfaceAxisReference.Screen(a, raster), SurfaceAxisReference.Screen(b, raster),
                            dimension, SurfaceAxisStrokeKind.Grid, new(fixedBit == 1 ? 0 : fixedBit == 2 ? 1 : 2, normalized)));
                    }
                }
                if (best < 0) continue;
                var point = SurfaceAxisReference.Lerp(corners[best], corners[best | bit], t);
                if (point.Depth < raster.Camera.Near || point.Depth > raster.Camera.Far) continue;
                var anchor = SurfaceAxisReference.Screen(point, raster);
                if (anchor.X < 0 || anchor.Y < 0 || anchor.X >= raster.Width || anchor.Y >= raster.Height) continue;
                var pixel = (int)anchor.Y * raster.Width + (int)anchor.X;
                // No tick or label for a silhouette point obscured by source/projection data.
                if (raster.TriangleIndices[pixel] >= 0 && anchor.Depth > raster.Depths[pixel] + 1e-9) continue;
                var end = anchor with { X = Math.Clamp(anchor.X + 5 * outwardX, 0, raster.Width - .001),
                    Y = Math.Clamp(anchor.Y + 5 * outwardY, 0, raster.Height - .001) };
                lines.Add(new(anchor, end, dimension, SurfaceAxisStrokeKind.Tick));
                ticks.Add(new(dimension, value, anchor, outwardX, outwardY));
            }
        }
        return ticks.ToImmutable();
    }

    internal static double Fraction(double value, double low, double high)
    {
        if (low == high) return .5;
        var scale = Math.Max(Math.Abs(low), Math.Abs(high));
        return Math.Clamp((value / scale - low / scale) / (high / scale - low / scale), 0, 1);
    }

    internal static ImmutableArray<double> Values(double low, double high, double rawStep)
    {
        if (low == high) return [low];
        if (!(rawStep > 0) || !double.IsFinite(rawStep)) return [low, high];
        var exponent = Math.Floor(Math.Log10(rawStep));
        var power = Math.Pow(10, exponent);
        if (!(power > 0) || !double.IsFinite(power)) return [low, high];
        var mantissa = rawStep / power;
        var step = (mantissa <= 1 ? 1 : mantissa <= 2 ? 2 : mantissa <= 3 ? 3 : mantissa <= 5 ? 5 : 10) * power;
        if (!(step > 0) || !double.IsFinite(step)) return [low, high];
        var first = Math.Ceiling(low / step);
        if (!double.IsFinite(first)) return [low, high];
        var result = ImmutableArray.CreateBuilder<double>();
        for (var i = 0; i < SurfaceAxisReference.MaximumTicksPerAxis; i++)
        {
            var value = (first + i) * step;
            if (!double.IsFinite(value) || value > high) break;
            if (value >= low && (result.Count == 0 || value > result[^1])) result.Add(value == 0 ? 0 : value);
        }
        return result.Count > 0 ? result.ToImmutable() : [low, high];
    }
}
