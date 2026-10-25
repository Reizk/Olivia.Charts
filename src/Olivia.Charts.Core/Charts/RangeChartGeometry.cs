using System.Collections.Immutable;

namespace Olivia.Core.Charts;

public static partial class AreaChartGeometry
{
    // Range clipping shares the existing reduced-rational fixed convex half-plane graph. The
    // original pair is provenance only; projected intersections never become raw observations.
    internal static RangeChartPolygon? CreateRangePolygon(RangeChartHit first, RangeChartHit second,
        int series, HistoryValueDomain xd, HistoryValueDomain yd)
    {
        var x0 = Exact.FromDouble(first.X); var x1 = Exact.FromDouble(second.X);
        var bounds = new ExactBounds(Exact.FromDouble(xd.Minimum), Exact.FromDouble(xd.Maximum),
            Exact.FromDouble(yd.Minimum), Exact.FromDouble(yd.Maximum));
        var vertices = ClipAndProject([
            new(x0, Exact.FromDouble(first.Lower!.Value)), new(x0, Exact.FromDouble(first.Upper!.Value)),
            new(x1, Exact.FromDouble(second.Upper!.Value)), new(x1, Exact.FromDouble(second.Lower!.Value))], bounds);
        return vertices.IsEmpty ? null : new(series, first, second, vertices);
    }

    internal static RangeChartSegment? CreateRangeBoundary(RangeChartHit first, RangeChartHit second,
        int series, bool upper, HistoryValueDomain xd, HistoryValueDomain yd)
    {
        var a = new ExactPoint(Exact.FromDouble(first.X), Exact.FromDouble((upper ? first.Upper : first.Lower)!.Value));
        var b = new ExactPoint(Exact.FromDouble(second.X), Exact.FromDouble((upper ? second.Upper : second.Lower)!.Value));
        var bounds = new ExactBounds(Exact.FromDouble(xd.Minimum), Exact.FromDouble(xd.Maximum),
            Exact.FromDouble(yd.Minimum), Exact.FromDouble(yd.Maximum));
        if (!ClipSegmentPlane(ref a, ref b, bounds.MinX, true, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxX, true, false) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MinY, false, true) ||
            !ClipSegmentPlane(ref a, ref b, bounds.MaxY, false, false) || a == b) return null;
        return new(series, first, second, upper, ProjectSegmentPoint(a, bounds), ProjectSegmentPoint(b, bounds));
    }
}

public static class RangeChartGeometry
{
    /// <summary>Bar hit follows reverse paint order. Area hit follows the closest original clipped
    /// vertical interval in DIP; equal distance preserves source series/sample order. Polygon
    /// interiors, interpolated edge points and clip corners have no independent identity.</summary>
    public static RangeChartHit? HitTest(RangeChartSnapshot snapshot, double x, double y,
        double viewportWidth, double viewportHeight, double tolerance = 6)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(viewportWidth) || viewportWidth <= 0 || !double.IsFinite(viewportHeight) || viewportHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(viewportWidth));
        if (!double.IsFinite(tolerance) || tolerance < 0 || tolerance > 6) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (snapshot.Status != RangeChartStatus.Ready || x < 0 || x > 1 || y < 0 || y > 1) return null;
        if (snapshot.Options.Mode != RangeChartMode.LinearArea)
        {
            for (var i = snapshot.Bars.Length - 1; i >= 0; i--)
            {
                var bar = snapshot.Bars[i];
                if (bar.Width > 0 && bar.Height > 0 && x >= bar.Left && x <= bar.Left + bar.Width &&
                    y >= bar.Top && y <= bar.Top + bar.Height) return bar.Hit;
            }
            return null;
        }
        RangeChartHit? best = null; var bestDistance = double.PositiveInfinity;
        foreach (var interval in snapshot.Intervals)
        {
            var dx = Math.Abs(x - interval.X) * viewportWidth;
            var dy = (y < interval.Top ? interval.Top - y : y > interval.Bottom ? y - interval.Bottom : 0) * viewportHeight;
            if (dx > tolerance || dy > tolerance) continue;
            var largest = Math.Max(dx, dy); var smallest = Math.Min(dx, dy);
            var ratio = largest == 0 ? 0 : smallest / largest;
            var distance = largest * Math.Sqrt(1 + ratio * ratio);
            if (distance <= tolerance && distance < bestDistance) { bestDistance = distance; best = interval.Hit; }
        }
        return best;
    }
}
