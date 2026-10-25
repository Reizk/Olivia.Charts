using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

/// <summary>Independent adversarial checks: metamorphic oracles and exact bounded contracts.</summary>
public sealed class Point3DReviewTests
{
    [Fact]
    public void PositivePowerOfTwoRawScalingLeavesGeometryInvariantButNotRawIdentity()
    {
        Point3DSample[] raw = [new(long.MinValue, -8, 2, 4), new(0, 4, -6, 2), new(long.MaxValue, 2, 8, -4)];
        var camera = new Point3DCamera(.37, -.29, new(.1, -.2, .05), halfHeight: 2);
        var viewport = new Point3DViewport(937, 413);
        var a = new Point3DSource("a", raw, 11).Query(camera, viewport);
        var b = new Point3DSource("b", raw.Select(p => new Point3DSample(p.Key, p.X * 1024, p.Y * 1024, p.Z * 1024)), 12).Query(camera, viewport);
        Assert.Equal(3, a.Points.Length); Assert.Equal(a.Points.Length, b.Points.Length);
        Assert.Equal(a.WorldScale * 1024, b.WorldScale);
        for (var i = 0; i < a.Points.Length; i++)
        {
            Assert.Equal(a.Points[i].SourceIndex, b.Points[i].SourceIndex);
            Assert.Equal(a.Points[i].X, b.Points[i].X); Assert.Equal(a.Points[i].Y, b.Points[i].Y); Assert.Equal(a.Points[i].Depth, b.Points[i].Depth);
            Assert.Equal(a.Points[i].Hit.Key, b.Points[i].Hit.Key);
            Assert.Equal("b", b.Points[i].Hit.SourceId); Assert.Equal(12, b.Points[i].Hit.Version);
            Assert.Equal(a.Points[i].Hit.X * 1024, b.Points[i].Hit.X);
        }
    }

    [Fact]
    public void OrthographicRaysRecoverEveryProjectedTripleAcrossPolesAndExtremeAspects()
    {
        var source = new Point3DSource("ray-oracle", [new(1, -1, .5, .25), new(2, .125, -.25, .75), new(3, 0, 0, 0)]);
        var checkedPoints = 0;
        foreach (var yaw in new[] { -Math.PI, -.61, 0, 1.13, Math.PI })
        foreach (var pitch in new[] { -Math.PI / 2, -.43, 0, .72, Math.PI / 2 })
        foreach (var viewport in new[] { new Point3DViewport(1, 8192), new Point3DViewport(8192, 1), new Point3DViewport(607, 419) })
        {
            var snapshot = source.Query(new(yaw, pitch, halfHeight: 16), viewport);
            foreach (var p in snapshot.Points)
            {
                var ray = Point3DChartGeometry.RayAt(snapshot, p.X, p.Y)!.Value;
                var norm = ray.Direction.X * ray.Direction.X + ray.Direction.Y * ray.Direction.Y + ray.Direction.Z * ray.Direction.Z;
                Assert.InRange(Math.Abs(norm - 1), 0, 1e-14);
                Assert.InRange(Math.Abs(ray.Origin.X + p.Depth * ray.Direction.X - p.Hit.X!.Value / snapshot.WorldScale), 0, 1e-9);
                Assert.InRange(Math.Abs(ray.Origin.Y + p.Depth * ray.Direction.Y - p.Hit.Y!.Value / snapshot.WorldScale), 0, 1e-9);
                Assert.InRange(Math.Abs(ray.Origin.Z + p.Depth * ray.Direction.Z - p.Hit.Z!.Value / snapshot.WorldScale), 0, 1e-9);
                checkedPoints++;
            }
        }
        Assert.True(checkedPoints >= 175);
    }

    [Fact]
    public void EachScreenAxisAndDepthRespondsToIndependentXYZPerturbationsAtObliquePose()
    {
        var source = new Point3DSource("axis-perturbations", [new(0, 0, 0, 0), new(1, 1, 0, 0), new(2, 0, 1, 0), new(3, 0, 0, 1)]);
        var snapshot = source.Query(new(.6, .4, halfHeight: 2), new(800, 600));
        var origin = snapshot.Points.Single(p => p.Hit.Key == 0);
        foreach (var p in snapshot.Points.Where(p => p.Hit.Key != 0))
        {
            Assert.NotEqual(origin.Depth, p.Depth);
            Assert.True(Math.Abs(origin.X - p.X) + Math.Abs(origin.Y - p.Y) > 1);
        }
        // Y has no horizontal component in the declared yaw/pitch frame, but does alter up and depth.
        var y = snapshot.Points.Single(p => p.Hit.Key == 2);
        Assert.Equal(origin.X, y.X); Assert.NotEqual(origin.Y, y.Y);
    }

    [Fact]
    public void OutsideCenterCannotSpendBudgetOrHitEvenWhenItsDiskWouldCrossViewport()
    {
        // The third complete point is only .5 pixel beyond the top edge.
        var snapshot = new Point3DSource("center-clip", [new(1, 0, 1, 0), new(2, 0, .5, 0), new(3, 0, .505, 0), new(4, null, 99, 99)])
            .Query(new(halfHeight: .5), new(100, 100), geometryBudget: 1);
        Assert.Equal(ChartBudgetStatus.Satisfied, snapshot.BudgetStatus);
        Assert.Equal(1, snapshot.RequiredPointCount); Assert.Equal(2, snapshot.ClippedCount); Assert.Single(snapshot.Gaps);
        var edge = Assert.Single(snapshot.Points); Assert.Equal(2, edge.Hit.Key); Assert.Equal(0, edge.Y);
        Assert.Equal(2, Point3DChartGeometry.HitTest(snapshot, 50, 0)!.Value.Key);
        Assert.Null(Point3DChartGeometry.HitTest(snapshot, 50, -double.Epsilon));
    }

    [Fact]
    public void GapPayloadBitsSurviveAndNeverSetNormalizationScale()
    {
        var nan = BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000042));
        var minusZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var source = new Point3DSource("gap-bits", [new(1, nan, minusZero, double.MaxValue), new(2, 0, 0, double.Epsilon)]);
        var snapshot = source.Query();
        Assert.Equal(double.Epsilon, snapshot.WorldScale);
        var gap = Assert.Single(snapshot.Gaps);
        Assert.Equal(BitConverter.DoubleToInt64Bits(nan), BitConverter.DoubleToInt64Bits(gap.X!.Value));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(gap.Y!.Value));
        Assert.Equal(double.MaxValue, gap.Z); Assert.Equal(3, Assert.Single(snapshot.Points).Depth);
    }

    [Fact]
    public void ReversingEqualDepthInputReversesPainterWinnerWithoutChangingUnrelatedRawKeys()
    {
        Point3DSample[] raw = [new(70, 0, 0, 0), new(20, 0, 0, 0), new(90, 0, 0, 0)];
        var forward = new Point3DSource("ties", raw).Query();
        var backward = new Point3DSource("ties", raw.Reverse()).Query();
        Assert.Equal(new long[] { 70, 20, 90 }, forward.Points.Select(p => p.Hit.Key));
        Assert.Equal(new long[] { 90, 20, 70 }, backward.Points.Select(p => p.Hit.Key));
        Assert.Equal(90, Point3DChartGeometry.HitTest(forward, 400, 300)!.Value.Key);
        Assert.Equal(70, Point3DChartGeometry.HitTest(backward, 400, 300)!.Value.Key);
        Assert.Equal(3, backward.Points.Select(p => p.Hit.Key).Distinct().Count());
    }
}
