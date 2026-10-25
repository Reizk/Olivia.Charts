using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SunburstReviewTests
{
    [Fact]
    public void Nonzero_negative_subnormal_y_keeps_final_sector_side_while_negative_zero_uses_seam()
    {
        var snapshot = new SunburstChartSource([new("first", "Same", null, 1), new("last", "Same", null, 1)])
            .Query(new(displayDepth: 1));
        Assert.Equal("last", SunburstChartGeometry.HitTest(snapshot, .5, -double.Epsilon)!.Value.Key);
        Assert.Equal("last", SunburstChartGeometry.HitTest(snapshot, .5, -2 * double.Epsilon)!.Value.Key);
        Assert.Equal("first", SunburstChartGeometry.HitTest(snapshot, .5, -0d)!.Value.Key);
        Assert.Equal("first", SunburstChartGeometry.HitTest(snapshot, .5, double.Epsilon)!.Value.Key);
    }
}
