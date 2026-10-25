using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class Point3DChartTests
{
    [Fact]
    public void CopiesOriginalTriplesAndPreservesSourceVersionAndBits()
    {
        var samples = new[] { new Point3DSample(8, 20, -0d, 5), new Point3DSample(3, 10, 4, 0) };
        var source = new Point3DSource("cloud", samples, 19);
        samples[0] = new(99, 99, 99, 99);
        var snapshot = source.Query();
        Assert.Same(source, snapshot.Source); Assert.Equal(20, snapshot.WorldScale);
        var point = snapshot.Points.Single(p => p.Hit.Key == 8);
        Assert.Equal("cloud", point.Hit.SourceId); Assert.Equal(19, point.Hit.Version);
        Assert.Equal(20, point.Hit.X); Assert.Equal(5, point.Hit.Z);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(point.Hit.Y!.Value));
        Assert.Equal(0, point.SourceIndex);
    }

    [Fact]
    public void CompleteTriplesAloneDetermineUniformScaleAndGapsRetainRawValues()
    {
        var snapshot = Source(new(1, null, double.MaxValue, 1), new(2, 1, double.NaN, 2),
            new(3, 1, 2, double.PositiveInfinity), new(4, double.NegativeInfinity, 2, 3), new(5, 4, 2, 1)).Query();
        Assert.Equal(4, snapshot.WorldScale); Assert.Equal(4, snapshot.Gaps.Length);
        var point = Assert.Single(snapshot.Points);
        Assert.Equal(640, point.X); Assert.Equal(180, point.Y); Assert.Equal(3.75, point.Depth);
        Assert.True(double.IsNaN(snapshot.Gaps[1].Y!.Value));
        Assert.Equal(double.PositiveInfinity, snapshot.Gaps[2].Z);
        Assert.Equal(1, Source().Query().WorldScale);
        Assert.Equal(1, Source(new Point3DSample(1, 0, 0, 0)).Query().WorldScale);
        Assert.Equal(1, Source(new Point3DSample(1, 100, null, 0)).Query().WorldScale);
    }

    [Fact]
    public void ProjectionUsesRightHandedCameraAndUniformPixelScale()
    {
        var source = Source(new(1, 1, 0, 0), new(2, 0, 1, 0), new(3, 0, 0, 1));
        var snapshot = source.Query(new(halfHeight: 1), new(800, 400));
        Assert.Equal(600, snapshot.Points.Single(p => p.Hit.Key == 1).X);
        Assert.Equal(0, snapshot.Points.Single(p => p.Hit.Key == 2).Y);
        Assert.Equal(3, snapshot.Points.Single(p => p.Hit.Key == 3).Depth);
        var yaw = source.Query(new(yaw: Math.PI / 2, halfHeight: 1), new(800, 400));
        Assert.Equal(200, yaw.Points.Single(p => p.Hit.Key == 3).X, 10);
        Assert.Equal(3, yaw.Points.Single(p => p.Hit.Key == 1).Depth, 10);
        var pitch = source.Query(new(pitch: Math.PI / 2, halfHeight: 1), new(800, 400));
        Assert.Equal(400, pitch.Points.Single(p => p.Hit.Key == 3).Y, 10);
        Assert.Equal(3, pitch.Points.Single(p => p.Hit.Key == 2).Depth, 10);
    }

    [Fact]
    public void CenterClippingIncludesFrustumEdgesAndSeparatesGaps()
    {
        var source = Source(new(1, -1, 1, -1), new(2, 1, -1, 1), new(3, 0, 0, 0), new(4, null, 0, 0));
        var all = source.Query(new(halfHeight: 1, near: 3, far: 5), new(100, 100));
        Assert.Equal(3, all.Points.Length); Assert.Equal(0, all.ClippedCount); Assert.Single(all.Gaps);
        Assert.Equal(new long[] { 1, 3, 2 }, all.Points.Select(p => p.Hit.Key));
        var depth = source.Query(new(halfHeight: 1, near: 3.1, far: 4.9), new(100, 100));
        Assert.Equal(3, Assert.Single(depth.Points).Hit.Key); Assert.Equal(2, depth.ClippedCount);
        var zoom = source.Query(new(halfHeight: .5), new(100, 100));
        Assert.Equal(3, Assert.Single(zoom.Points).Hit.Key); Assert.Equal(2, zoom.ClippedCount);
        var pan = source.Query(new(target: new(4, 0, 0)), new(100, 100));
        Assert.Empty(pan.Points); Assert.Equal(3, pan.ClippedCount);
    }

    [Fact]
    public void GeometryBudgetRejectsWholeProjectionAndRestoresRawIdentity()
    {
        var source = Source(new(1, 0, 0, -1), new(2, 0, 0, 1), new(3, null, 0, 0));
        var rejected = source.Query(geometryBudget: 1);
        Assert.Empty(rejected.Points); Assert.Equal(2, rejected.RequiredPointCount);
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, rejected.BudgetStatus); Assert.Single(rejected.Gaps);
        Assert.Null(Point3DChartGeometry.GetHit(rejected, 0));
        var restored = source.Query(geometryBudget: 2);
        Assert.Equal(ChartBudgetStatus.Satisfied, restored.BudgetStatus);
        Assert.Equal(source.Samples.Take(2), restored.Points.Select(p => p.Hit.Sample));
        Assert.Equal(ChartBudgetStatus.Satisfied, Source().Query(geometryBudget: 0).BudgetStatus);
    }

    [Fact]
    public void PickingUsesFrontmostContainingDiskNotNearestCenterAndLastSourceTie()
    {
        var snapshot = Source(new(1, 0, 0, -1), new(2, .01, 0, 1), new(3, .01, 0, 1)).Query();
        Assert.Equal(new long[] { 1, 2, 3 }, snapshot.Points.Select(p => p.Hit.Key));
        Assert.Equal(3, Point3DChartGeometry.HitTest(snapshot, 400, 300)!.Value.Key);
        Assert.Equal(1, Point3DChartGeometry.HitTest(snapshot, 400, 300, 0)!.Value.Key);
        Assert.Null(Point3DChartGeometry.HitTest(snapshot, 400, 306));
        Assert.NotNull(Point3DChartGeometry.HitTest(snapshot, snapshot.Points[2].X + 5, 300));
        Assert.Null(Point3DChartGeometry.HitTest(snapshot, -1, 300, 32));
        Assert.Equal(new long[] { 1, 2, 3 }, Enumerable.Range(0, 3).Select(i => Point3DChartGeometry.GetHit(snapshot, i)!.Value.Key));
        Assert.Null(Point3DChartGeometry.GetHit(snapshot, -1)); Assert.Null(Point3DChartGeometry.GetHit(snapshot, 3));
    }

    [Fact]
    public void OrthographicRaysAreParallelAndUseNormalizedWorldTargetAndAspect()
    {
        var snapshot = Source().Query(new(target: new(1, 2, 3), halfHeight: 2, distance: 4), new(800, 400));
        var center = Point3DChartGeometry.RayAt(snapshot, 400, 200)!.Value;
        Assert.Equal(new Point3DVector(1, 2, 7), center.Origin);
        Assert.Equal(new Point3DVector(0, 0, -1), center.Direction);
        var corner = Point3DChartGeometry.RayAt(snapshot, 800, 0)!.Value;
        Assert.Equal(new Point3DVector(5, 4, 7), corner.Origin); Assert.Equal(center.Direction, corner.Direction);
        Assert.Null(Point3DChartGeometry.RayAt(snapshot, 801, 0));
        var rotated = Source().Query(new(yaw: Math.PI / 2, pitch: Math.PI / 2));
        var ray = Point3DChartGeometry.RayAt(rotated, 400, 300)!.Value;
        Assert.Equal(-1, ray.Direction.Y, 12); Assert.Equal(4, ray.Origin.Y, 12);
    }

    [Fact]
    public void ZeroViewportsAreEmptyAndExtremeAcceptedViewportsRemainFinite()
    {
        foreach (var viewport in new[] { new Point3DViewport(0, 600), new Point3DViewport(800, 0), new Point3DViewport(0, 0) })
        {
            var snapshot = Source(new(1, 0, 0, 0), new(2, null, 0, 0)).Query(viewport: viewport);
            Assert.Empty(snapshot.Points); Assert.Equal(1, snapshot.ClippedCount); Assert.Single(snapshot.Gaps);
            Assert.Equal(0, snapshot.RequiredPointCount); Assert.Null(Point3DChartGeometry.RayAt(snapshot, 0, 0));
            Assert.Null(Point3DChartGeometry.HitTest(snapshot, 0, 0));
        }
        foreach (var viewport in new[] { new Point3DViewport(1, 8192), new Point3DViewport(8192, 1) })
        {
            var snapshot = Source(new Point3DSample(1, 0, 0, 0)).Query(new(halfHeight: 16), viewport);
            Assert.All(snapshot.Points, p => { Assert.True(double.IsFinite(p.X)); Assert.True(double.IsFinite(p.Y)); Assert.True(double.IsFinite(p.Depth)); });
            var ray = Point3DChartGeometry.RayAt(snapshot, viewport.Width, viewport.Height)!.Value;
            Assert.True(double.IsFinite(ray.Origin.X)); Assert.True(double.IsFinite(ray.Origin.Y)); Assert.True(double.IsFinite(ray.Origin.Z));
        }
    }

    [Theory]
    [InlineData(double.MaxValue)]
    [InlineData(double.Epsilon)]
    public void ExtremeRawCoordinatesNormalizeWithoutOverflow(double magnitude)
    {
        var snapshot = Source(new(1, magnitude, magnitude, magnitude), new(2, -magnitude, -magnitude, -magnitude)).Query();
        Assert.Equal(magnitude, snapshot.WorldScale); Assert.Equal(2, snapshot.Points.Length);
        Assert.All(snapshot.Points, p => { Assert.True(double.IsFinite(p.X)); Assert.True(double.IsFinite(p.Y)); Assert.True(double.IsFinite(p.Depth)); });
        Assert.Equal(magnitude, snapshot.Points.Single(p => p.Hit.Key == 1).Hit.X);
        Assert.Equal(640, snapshot.Points.Single(p => p.Hit.Key == 1).X);
    }

    [Fact]
    public void AdmissionCountsGapsRejectsDuplicatesAndStopsInfiniteInputAtCapPlusOne()
    {
        var maximum = new Point3DSource("s", Enumerable.Range(0, 2048).Select(i => new Point3DSample(i, null, 0, 0)));
        Assert.Equal(2048, maximum.Query().Gaps.Length);
        var full = new Point3DSource("s", Enumerable.Range(0, 2048).Select(i => new Point3DSample(i, 0, 0, 0)));
        Assert.Equal(2048, full.Query().Points.Length);
        Assert.Throws<ArgumentException>(() => Source(new(1, 0, 0, 0), new(1, null, 0, 0)));
        var advances = 0; var disposed = false;
        IEnumerable<Point3DSample> Infinite()
        {
            try { while (true) { advances++; yield return new(advances, null, 0, 0); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new Point3DSource("s", Infinite()));
        Assert.Equal(2049, advances); Assert.True(disposed);
    }

    [Fact]
    public void InvalidArgumentsAreRejectedAndBoundaryCameraIsAdmitted()
    {
        Assert.Throws<ArgumentNullException>(() => new Point3DSource("s", null!));
        foreach (var id in new[] { "", " ", new string('x', 257), "\ud800" })
            Assert.Throws<ArgumentException>(() => new Point3DSource(id, []));
        Assert.Equal(256, new Point3DSource(new string('x', 256), []).Id.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DSource("s", [], -1));
        var snapshot = Source().Query();
        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(yaw: bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(pitch: bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(target: new(bad, 0, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(target: new(0, bad, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(target: new(0, 0, bad)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(halfHeight: bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(distance: bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(near: bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(far: bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DViewport(bad, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DViewport(1, bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => Point3DChartGeometry.HitTest(snapshot, bad, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Point3DChartGeometry.HitTest(snapshot, 0, bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => Point3DChartGeometry.HitTest(snapshot, 0, 0, bad));
            Assert.Throws<ArgumentOutOfRangeException>(() => Point3DChartGeometry.RayAt(snapshot, bad, 0));
        }
        foreach (var bad in new[] { -.1, .5, 8193 }) Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DViewport(bad, 1));
        foreach (var bad in new[] { -1d, 0d, 16.01 }) Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(halfHeight: bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(yaw: Math.BitIncrement(Math.PI)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(pitch: Math.BitIncrement(Math.PI / 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(target: new(4.1, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(distance: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(distance: 33));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(near: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(near: 5, far: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Point3DCamera(far: 65));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source().Query(geometryBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Source().Query(geometryBudget: 2049));
        Assert.Throws<ArgumentOutOfRangeException>(() => Point3DChartGeometry.HitTest(snapshot, 0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Point3DChartGeometry.HitTest(snapshot, 0, 0, 33));
        Assert.Throws<ArgumentNullException>(() => Point3DChartGeometry.HitTest(null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => Point3DChartGeometry.GetHit(null!, 0));
        Assert.Throws<ArgumentNullException>(() => Point3DChartGeometry.RayAt(null!, 0, 0));
        _ = new Point3DCamera(-Math.PI, -Math.PI / 2, new(-4, 4, -4), .01, .01, .001, 64);
        _ = new Point3DCamera(Math.PI, Math.PI / 2, new(4, -4, 4), 16, 32, 63, 64);
    }

    private static Point3DSource Source(params Point3DSample[] samples) => new("s", samples);
}
