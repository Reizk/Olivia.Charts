using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceProjectionTests
{
    private static readonly Point3DCamera Above = new(pitch: Math.PI / 2);

    [Fact]
    public void ExistingSixParameterRenderEntryPointIsRetained()
    {
        var method = typeof(SurfaceRasterizer).GetMethod(nameof(SurfaceRasterizer.Render),
            [typeof(SurfaceChartSource), typeof(Point3DCamera), typeof(int), typeof(int),
             typeof(double?), typeof(double?)]);
        Assert.NotNull(method);
        var source = Flat(.25);
        var legacy = Assert.IsType<SurfaceRasterSnapshot>(method.Invoke(null, [source, Above, 17, 19, -1d, 1d]));
        AssertSameRaster(SurfaceRasterizer.Render(source, Above, 17, 19, -1, 1, false), legacy);
        Assert.False(legacy.ShowProjection);
        Assert.Empty(legacy.ProjectionPixels);
    }

    [Fact]
    public void HiddenModesPreserveLegacyPixelsDepthAndHitsWithoutAllocatingAPixelMask()
    {
        var source = new SurfaceChartSource("hidden", [-1, 1], [-1, 1], [-.5, 0, .25, .75], 17);
        var baseline = SurfaceRasterizer.Render(source, Above, 37, 29, -1, 1);
        foreach (var placement in Enum.GetValues<SurfaceProjectionPlacement>())
        {
            var hidden = SurfaceRasterizer.Render(source, Above, 37, 29, -1, 1, false, placement);
            Assert.False(hidden.ShowProjection);
            Assert.Equal(placement, hidden.ProjectionPlacement);
            Assert.Null(hidden.ProjectionPlaneValue);
            Assert.Empty(hidden.ProjectionPixels);
            AssertSameRaster(baseline, hidden);
            for (var y = 0; y < hidden.Height; y++)
                for (var x = 0; x < hidden.Width; x++)
                {
                    Assert.False(hidden.IsProjectionAt(x, y));
                    Assert.Equal(baseline.HitTest(x, y), hidden.HitTest(x, y));
                }
        }

        // Exactly 16 bytes/pixel belong to the established BGRA/depth/index buffers.
        // Leave ample room for this four-vertex mesh and array headers, but not an
        // extra 65,536-byte hidden mask. This is one bounded admission/allocation check.
        const int width = 1024, height = 64;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var measured = SurfaceRasterizer.Render(source, Above, width, height, -1, 1, false);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(measured.ProjectionPixels);
        Assert.InRange(allocated, 16L * width * height, 16L * width * height + 16_384);
    }

    [Theory]
    [InlineData(1, SurfaceProjectionPlacement.Top, 1)]
    [InlineData(-1, SurfaceProjectionPlacement.Bottom, -1)]
    public void ConstantProjectionUsesRawColorAndOriginalIdentityEvenAcrossViewportClipping(
        int viewSide, SurfaceProjectionPlacement placement, double plane)
    {
        var source = Flat(.25, 42);
        var camera = new Point3DCamera(pitch: viewSide * Math.PI / 2, halfHeight: .3);
        var frame = SurfaceRasterizer.Render(source, camera, 31, 27, -1, 1, true, placement);
        Assert.True(frame.ShowProjection);
        Assert.Equal(placement, frame.ProjectionPlacement);
        Assert.Equal(plane, frame.ProjectionPlaneValue);
        Assert.Same(source, frame.Source);
        Assert.Equal(42, frame.Source.Version);
        Assert.Equal(2, frame.Triangles.Length);
        Assert.Equal(31 * 27, frame.CoveredPixelCount);
        Assert.Equal(frame.CoveredPixelCount, frame.ProjectionPixels.Count(value => value == 1));
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var pixel = y * frame.Width + x;
                Assert.True(frame.IsProjectionAt(x, y));
                Assert.Null(frame.HitTest(x, y)); // Do not fabricate a height or pick through the plane.
                Assert.InRange(frame.TriangleIndices[pixel], 0, 1);
                Assert.Equal(3, frame.Depths[pixel], 10);
                AssertColorNear(SurfaceRasterizer.ColorAt(.25, -1, 1), PixelColor(frame, pixel));
            }
        foreach (var triangle in frame.Triangles)
            foreach (var vertex in new[] { triangle.A, triangle.B, triangle.C })
                Assert.Equal(source.Vertices[(int)vertex.Key], vertex);
        Assert.False(frame.IsProjectionAt(-1, 0));
        Assert.False(frame.IsProjectionAt(0, -1));
        Assert.False(frame.IsProjectionAt(frame.Width, 0));
        Assert.False(frame.IsProjectionAt(0, frame.Height));
    }

    [Theory]
    [InlineData(1, SurfaceProjectionPlacement.Top)]
    [InlineData(-1, SurfaceProjectionPlacement.Bottom)]
    public void LinearRawHeightFieldKeepsItsAnalyticalColorOnEitherGeometricPlane(
        int viewSide, SurfaceProjectionPlacement placement)
    {
        // h(x,y) = .15 + .30*x + .20*y. Source order is X outer, Y inner.
        var source = new SurfaceChartSource("linear", [-1, 1], [-1, 1], [-.35, .05, .25, .65], 23);
        var camera = new Point3DCamera(yaw: .37, pitch: viewSide * Math.PI / 2,
            target: new(.1, 0, -.1), halfHeight: 1.5);
        var frame = SurfaceRasterizer.Render(source, camera, 59, 47, -1, 1, true, placement);
        var projectionCount = 0;
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                if (!frame.IsProjectionAt(x, y)) continue;
                projectionCount++;
                // Independent ray/constant-height-plane intersection, not HitTest or
                // source-vertex color lookup. World normalization is exactly center=0, scale=1.
                var horizontal = (2 * (x + .5) / frame.Width - 1) * camera.HalfHeight * frame.Width / frame.Height;
                var vertical = (1 - 2 * (y + .5) / frame.Height) * camera.HalfHeight;
                var sy = Math.Sin(camera.Yaw); var cy = Math.Cos(camera.Yaw);
                var sp = Math.Sin(camera.Pitch); var cp = Math.Cos(camera.Pitch);
                var forward = (viewSide - camera.Target.Y - cp * vertical) / sp;
                var argumentX = camera.Target.X + cy * horizontal - sy * sp * vertical + sy * cp * forward;
                var argumentY = camera.Target.Z - sy * horizontal - cy * sp * vertical + cy * cp * forward;
                var rawHeight = .15 + .30 * argumentX + .20 * argumentY;
                var pixel = y * frame.Width + x;
                Assert.InRange(argumentX, -1.000000001, 1.000000001);
                Assert.InRange(argumentY, -1.000000001, 1.000000001);
                Assert.Equal(camera.Distance - forward, frame.Depths[pixel], 9);
                AssertColorNear(SurfaceRasterizer.ColorAt(rawHeight, -1, 1), PixelColor(frame, pixel));
                Assert.Null(frame.HitTest(x, y));
            }
        Assert.True(projectionCount > 300);
        Assert.Equal(frame.CoveredPixelCount, projectionCount);
        Assert.Equal(viewSide, frame.ProjectionPlaneValue);
    }

    [Theory]
    [InlineData(.5, 0, -1)]
    [InlineData(-.5, 0, 1)]
    [InlineData(.2, -2, 1)]
    [InlineData(-.2, 2, -1)]
    [InlineData(0, 0, 1)]
    [InlineData(0, .25, -1)]
    [InlineData(0, -.25, 1)]
    public void AutoUsesCameraEyeHeightIncludingPanAndSelectsOnlyTheFarPlane(
        double pitch, double targetHeight, double expectedPlane)
    {
        var source = Flat(.25);
        var camera = new Point3DCamera(pitch: pitch, target: new(0, targetHeight, 0), halfHeight: 4);
        var automatic = SurfaceRasterizer.Render(source, camera, 43, 39, -1, 1, true);
        var explicitPlacement = expectedPlane < 0 ? SurfaceProjectionPlacement.Bottom : SurfaceProjectionPlacement.Top;
        var explicitFrame = SurfaceRasterizer.Render(source, camera, 43, 39, -1, 1, true, explicitPlacement);
        Assert.True(automatic.ShowProjection);
        Assert.Equal(SurfaceProjectionPlacement.Auto, automatic.ProjectionPlacement);
        Assert.Equal(expectedPlane, automatic.ProjectionPlaneValue);
        AssertSameRaster(explicitFrame, automatic);
        Assert.Equal(explicitFrame.ProjectionPixels.ToArray(), automatic.ProjectionPixels.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ProjectionPreservesIncidentTriangleGapsAndDoesNotInventAFullRectangle(double? missing)
    {
        var source = new SurfaceChartSource("gap", [-1, 1], [-1, 1], [.25, missing, .25, .25], 8);
        var frame = SurfaceRasterizer.Render(source, Above, 64, 64, -1, 1, true, SurfaceProjectionPlacement.Top);
        var triangle = Assert.Single(frame.Triangles);
        Assert.Equal(new long[] { 0, 2, 3 }, new[] { triangle.A.Key, triangle.B.Key, triangle.C.Key });
        Assert.True(frame.CoveredPixelCount > 0);
        Assert.Equal(frame.CoveredPixelCount, frame.ProjectionPixels.Count(value => value == 1));
        Assert.True(frame.IsProjectionAt(44, 20));
        Assert.False(frame.IsProjectionAt(20, 44));
        Assert.Equal(-1, frame.TriangleIndices[44 * 64 + 20]);
        Assert.Equal((byte)0, PixelColor(frame, 44 * 64 + 20).A);
        Assert.Null(frame.HitTest(20, 44));

        var allIncident = new SurfaceChartSource("empty", [-1, 1], [-1, 1], [missing, .25, .25, .25]);
        var empty = SurfaceRasterizer.Render(allIncident, Above, 17, 19, -1, 1, true, SurfaceProjectionPlacement.Top);
        Assert.Empty(empty.Triangles);
        Assert.Equal(0, empty.CoveredPixelCount);
        Assert.Equal(17 * 19, empty.ProjectionPixels.Length);
        Assert.All(empty.ProjectionPixels, value => Assert.Equal((byte)0, value));
        Assert.All(empty.Pixels, value => Assert.Equal((byte)0, value));
        Assert.All(empty.Depths, value => Assert.True(double.IsPositiveInfinity(value)));
        Assert.All(empty.TriangleIndices, value => Assert.Equal(-1, value));
    }

    [Theory]
    [InlineData(SurfaceProjectionPlacement.Top, 1)]
    [InlineData(SurfaceProjectionPlacement.Bottom, -1)]
    public void OneSharedDepthBufferMatchesIndependentSurfaceAndProjectionTriangleOracle(
        SurfaceProjectionPlacement placement, double planeHeight)
    {
        double[] axis = [-1, -1d / 3, 1d / 3, 1];
        double?[] heights = [-.92, .79, .89, -.68, 0, -.04, .95, -.91,
            -.88, .69, .14, -.08, .56, .19, .38, -.49];
        var source = new SurfaceChartSource("folded", axis, axis, heights, 91);
        var camera = new Point3DCamera(yaw: .6, pitch: .55, halfHeight: 1.8);
        var surface = SurfaceRasterizer.Render(source, camera, 72, 64, -1, 1);
        var frame = SurfaceRasterizer.Render(source, camera, 72, 64, -1, 1, true, placement);
        var surfaceWinners = 0; var planeWinners = 0; var overlaps = 0;
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var candidates = new List<Candidate>();
                foreach (var triangle in frame.Triangles)
                {
                    AddCandidate(triangle, false, null);
                    AddCandidate(triangle, true, planeHeight);
                }
                // Exact shared edges belong to either original triangle. Test strict
                // interiors so the oracle does not prescribe a different edge tie rule.
                if (candidates.Count == 0 || candidates.Any(candidate => candidate.MinimumWeight <= 1e-7)) continue;
                var nearest = candidates.OrderBy(candidate => candidate.Depth).ThenBy(candidate => candidate.IsProjection).First();
                var pixel = y * frame.Width + x;
                Assert.Equal(nearest.TriangleIndex, frame.TriangleIndices[pixel]);
                Assert.Equal(nearest.Depth, frame.Depths[pixel], 9);
                Assert.Equal(nearest.IsProjection, frame.IsProjectionAt(x, y));
                Assert.Equal(nearest.IsProjection ? (byte)1 : (byte)0, frame.ProjectionPixels[pixel]);
                AssertColorNear(SurfaceRasterizer.ColorAt(nearest.Value, -1, 1), PixelColor(frame, pixel));
                if (nearest.IsProjection)
                {
                    planeWinners++;
                    Assert.Null(frame.HitTest(x, y));
                }
                else
                {
                    surfaceWinners++;
                    Assert.Equal(surface.HitTest(x, y), frame.HitTest(x, y));
                    var hit = Assert.IsType<SurfaceRasterHit>(frame.HitTest(x, y));
                    Assert.Equal(source.Id, hit.SourceId);
                    Assert.Equal(source.Version, hit.Version);
                    Assert.Equal(nearest.Value, hit.InterpolatedValue, 9);
                }
                if (candidates.Any(candidate => candidate.IsProjection) && candidates.Any(candidate => !candidate.IsProjection))
                    overlaps++;

                void AddCandidate(SurfaceTriangle triangle, bool isProjection, double? height)
                {
                    var a = Project(triangle.A, height, camera, frame.Width, frame.Height);
                    var b = Project(triangle.B, height, camera, frame.Width, frame.Height);
                    var c = Project(triangle.C, height, camera, frame.Width, frame.Height);
                    var area = Edge(a, b, c.X, c.Y);
                    if (Math.Abs(area) < 1e-12) return;
                    var wa = Edge(b, c, x + .5, y + .5) / area;
                    var wb = Edge(c, a, x + .5, y + .5) / area;
                    var wc = 1 - wa - wb;
                    var minimumWeight = Math.Min(wa, Math.Min(wb, wc));
                    if (minimumWeight < -1e-9) return;
                    candidates.Add(new(triangle.Index, isProjection,
                        wa * a.Z + wb * b.Z + wc * c.Z,
                        wa * triangle.A.Z!.Value + wb * triangle.B.Z!.Value + wc * triangle.C.Z!.Value,
                        minimumWeight));
                }
            }
        Assert.True(overlaps > 50, "The fixture must exercise competition, not just disjoint projected footprints.");
        Assert.True(surfaceWinners > 20);
        Assert.True(planeWinners > 20);
        Assert.Equal(frame.Width * frame.Height, frame.ProjectionPixels.Length);
        Assert.Equal(frame.CoveredPixelCount, frame.TriangleIndices.Count(index => index >= 0));
        Assert.Equal(surface.Triangles.ToArray(), frame.Triangles.ToArray());
        for (var pixel = 0; pixel < frame.TriangleIndices.Length; pixel++)
            if (frame.TriangleIndices[pixel] < 0) Assert.Equal((byte)0, frame.ProjectionPixels[pixel]);
    }

    [Theory]
    [InlineData(1, SurfaceProjectionPlacement.Top)]
    [InlineData(-1, SurfaceProjectionPlacement.Bottom)]
    public void EqualDepthLeavesRealSurfacePixelsAndRawHitsInFrontOfProjection(
        double height, SurfaceProjectionPlacement placement)
    {
        var source = Flat(height, 11);
        var camera = new Point3DCamera(yaw: .3, pitch: .7, halfHeight: 1.8);
        var surface = SurfaceRasterizer.Render(source, camera, 48, 41, -1, 1);
        var coincident = SurfaceRasterizer.Render(source, camera, 48, 41, -1, 1, true, placement);
        Assert.True(coincident.CoveredPixelCount > 0);
        AssertSameRaster(surface, coincident);
        Assert.All(coincident.ProjectionPixels, value => Assert.Equal((byte)0, value));
        for (var y = 0; y < coincident.Height; y++)
            for (var x = 0; x < coincident.Width; x++)
                Assert.Equal(surface.HitTest(x, y), coincident.HitTest(x, y));
    }

    [Theory]
    [InlineData(1, SurfaceProjectionPlacement.Top)]
    [InlineData(-1, SurfaceProjectionPlacement.Bottom)]
    public void ProjectionCrossingNearAndFarPlanesSurvivesWithEveryOriginalCornerOutside(
        int viewSide, SurfaceProjectionPlacement placement)
    {
        var source = Flat(-viewSide, 6);
        var camera = new Point3DCamera(pitch: viewSide * Math.PI / 4, distance: 1, near: .3, far: .7, halfHeight: 1.5);
        foreach (var vertex in source.Vertices)
        {
            var cornerDepth = Project(vertex, viewSide, camera, 64, 64).Z;
            Assert.True(cornerDepth < camera.Near || cornerDepth > camera.Far);
        }
        var surface = SurfaceRasterizer.Render(source, camera, 64, 64, -1, 1);
        Assert.Equal(0, surface.CoveredPixelCount);
        var frame = SurfaceRasterizer.Render(source, camera, 64, 64, -1, 1, true, placement);
        Assert.True(frame.CoveredPixelCount > 0);
        Assert.True(frame.CoveredPixelCount < 64 * 64);
        Assert.Equal(2, frame.Triangles.Length);
        Assert.Equal(frame.CoveredPixelCount, frame.ProjectionPixels.Count(value => value == 1));
        for (var pixel = 0; pixel < frame.TriangleIndices.Length; pixel++)
            if (frame.TriangleIndices[pixel] >= 0)
            {
                Assert.InRange(frame.Depths[pixel], camera.Near, camera.Far);
                AssertColorNear(SurfaceRasterizer.ColorAt(-viewSide, -1, 1), PixelColor(frame, pixel));
                Assert.Null(frame.HitTest(pixel % frame.Width, pixel / frame.Width));
            }
    }

    [Theory]
    [InlineData(SurfaceProjectionPlacement.Top)]
    [InlineData(SurfaceProjectionPlacement.Bottom)]
    public void EntirelyDepthClippedProjectionCannotMaskTheVisibleSurface(SurfaceProjectionPlacement placement)
    {
        var source = Flat(0, 7);
        var camera = new Point3DCamera(pitch: Math.PI / 2, distance: 1, near: .25, far: 1.25);
        var surface = SurfaceRasterizer.Render(source, camera, 39, 35, -1, 1);
        var frame = SurfaceRasterizer.Render(source, camera, 39, 35, -1, 1, true, placement);
        Assert.True(surface.CoveredPixelCount > 0);
        AssertSameRaster(surface, frame);
        Assert.All(frame.ProjectionPixels, value => Assert.Equal((byte)0, value));
        Assert.Equal(surface.HitTest(19, 17), frame.HitTest(19, 17));
        Assert.NotNull(frame.HitTest(19, 17));
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(true, -1)]
    [InlineData(false, int.MaxValue)]
    [InlineData(true, int.MaxValue)]
    public void InvalidPlacementRejectsEvenWhenHiddenBeforeRasterAllocation(bool shown, int invalid)
    {
        var source = Flat(0);
        var placement = (SurfaceProjectionPlacement)invalid;
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SurfaceRasterizer.Render(source, Above, 1, 1, -1, 1, shown, placement));
        Assert.Equal("projectionPlacement", error.ParamName);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SurfaceRasterizer.Render(source, Above, 512, 512, -1, 1, shown, placement));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 262_144, "Invalid placement must be rejected before allocating pixel buffers.");
    }

    [Theory]
    [InlineData(0, 17)]
    [InlineData(19, 0)]
    [InlineData(0, 0)]
    public void ZeroAreaFrameRetainsRequestedProjectionMetadataWithoutBuffers(int width, int height)
    {
        var source = new SurfaceChartSource("natural-bounds", [-1, 1], [-1, 1], [-.5, .25, .5, .75]);
        var frame = SurfaceRasterizer.Render(source, Above, width, height, null, null,
            showProjection: true, projectionPlacement: SurfaceProjectionPlacement.Auto);
        Assert.Equal(width, frame.Width);
        Assert.Equal(height, frame.Height);
        Assert.True(frame.ShowProjection);
        Assert.Equal(SurfaceProjectionPlacement.Auto, frame.ProjectionPlacement);
        Assert.Equal(-.5, frame.ProjectionPlaneValue);
        Assert.Equal(-.5, frame.MinimumValue);
        Assert.Equal(.75, frame.MaximumValue);
        Assert.Empty(frame.Pixels);
        Assert.Empty(frame.Depths);
        Assert.Empty(frame.TriangleIndices);
        Assert.Empty(frame.ProjectionPixels);
        Assert.False(frame.IsProjectionAt(0, 0));
        Assert.Null(frame.HitTest(0, 0));
        Assert.Equal(0, frame.CoveredPixelCount);
    }

    [Fact]
    public void RetainedFramesStayImmutableAcrossProjectionModesTicksPauseResizeAndStop()
    {
        using var session = new RealtimeSurfaceSession(15);
        var initialSource = Assert.IsType<SurfaceChartSource>(session.CurrentSource);
        var projected = new RetainedFrame(RenderCurrent(true, SurfaceProjectionPlacement.Top));
        Assert.Contains((byte)1, projected.Frame.ProjectionPixels);
        var hidden = new RetainedFrame(RenderCurrent(false, SurfaceProjectionPlacement.Bottom));
        Assert.Same(initialSource, hidden.Frame.Source);
        Assert.Empty(hidden.Frame.ProjectionPixels);
        Assert.Equal(0, session.ProducedTicks); // Display options must not advance the generator.
        session.Start();
        var runningToken = session.Generation;
        Assert.True(session.Tick(runningToken));
        var next = new RetainedFrame(RenderCurrent(true, SurfaceProjectionPlacement.Auto));
        Assert.Equal(initialSource.Version + 1, next.Frame.Source.Version);
        session.Pause();
        Assert.False(session.Tick(runningToken));
        session.Resize(16);
        var resized = new RetainedFrame(RenderCurrent(true, SurfaceProjectionPlacement.Bottom, 17, 23));
        Assert.True(session.IsPaused);
        Assert.Equal(17, resized.Frame.Source.XCount);
        Assert.Equal(next.Frame.Source.Version + 1, resized.Frame.Source.Version);
        session.Resume();
        Assert.True(session.Tick(session.Generation));
        _ = RenderCurrent(true, SurfaceProjectionPlacement.Top, 25, 21);
        session.Stop();
        Assert.Null(session.CurrentSource);
        Assert.False(session.Tick(session.Generation));
        projected.AssertUnchanged();
        hidden.AssertUnchanged();
        next.AssertUnchanged();
        resized.AssertUnchanged();

        SurfaceRasterSnapshot RenderCurrent(bool show, SurfaceProjectionPlacement placement, int width = 24, int height = 20) =>
            SurfaceRasterizer.Render(Assert.IsType<SurfaceChartSource>(session.CurrentSource), Above,
                width, height, session.MinValue, session.MaxValue, show, placement);
    }

    private static SurfaceChartSource Flat(double value, long version = 0) =>
        new("flat", [-1, 1], [-1, 1], [value, value, value, value], version);

    private static void AssertSameRaster(SurfaceRasterSnapshot expected, SurfaceRasterSnapshot actual)
    {
        Assert.Same(expected.Source, actual.Source);
        Assert.Equal(expected.WorldCenter, actual.WorldCenter);
        Assert.Equal(expected.WorldScale, actual.WorldScale);
        Assert.Equal(expected.MinimumValue, actual.MinimumValue);
        Assert.Equal(expected.MaximumValue, actual.MaximumValue);
        Assert.Equal(expected.CoveredPixelCount, actual.CoveredPixelCount);
        Assert.Equal(expected.Triangles.ToArray(), actual.Triangles.ToArray());
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Equal(expected.Depths.ToArray(), actual.Depths.ToArray());
        Assert.Equal(expected.TriangleIndices.ToArray(), actual.TriangleIndices.ToArray());
    }

    private static (byte B, byte G, byte R, byte A) PixelColor(SurfaceRasterSnapshot frame, int pixel) =>
        (frame.Pixels[pixel * 4], frame.Pixels[pixel * 4 + 1], frame.Pixels[pixel * 4 + 2], frame.Pixels[pixel * 4 + 3]);

    private static void AssertColorNear((byte B, byte G, byte R, byte A) expected, (byte B, byte G, byte R, byte A) actual)
    {
        // Independent interpolation can round on the opposite side of an exact half-byte.
        Assert.InRange(Math.Abs(actual.B - expected.B), 0, 1);
        Assert.InRange(Math.Abs(actual.G - expected.G), 0, 1);
        Assert.InRange(Math.Abs(actual.R - expected.R), 0, 1);
        Assert.Equal(expected.A, actual.A);
    }

    private static Point3DVector Project(SurfaceVertex vertex, double? planeHeight, Point3DCamera camera, int width, int height)
    {
        var sy = Math.Sin(camera.Yaw); var cy = Math.Cos(camera.Yaw);
        var sp = Math.Sin(camera.Pitch); var cp = Math.Cos(camera.Pitch);
        var x = vertex.X - camera.Target.X;
        var y = vertex.Y - camera.Target.Z;
        var z = (planeHeight ?? vertex.Z!.Value) - camera.Target.Y;
        return new((1 + (cy * x - sy * y) / (camera.HalfHeight * width / height)) * width / 2,
            (1 - (-sy * sp * x + cp * z - cy * sp * y) / camera.HalfHeight) * height / 2,
            camera.Distance - (sy * cp * x + sp * z + cy * cp * y));
    }

    private static double Edge(Point3DVector a, Point3DVector b, double x, double y) =>
        (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    private readonly record struct Candidate(int TriangleIndex, bool IsProjection, double Depth, double Value, double MinimumWeight);

    private sealed class RetainedFrame
    {
        private readonly SurfaceChartSource _source;
        private readonly SurfaceVertex[] _vertices;
        private readonly SurfaceTriangle[] _triangles;
        private readonly byte[] _pixels, _projection;
        private readonly double[] _depths;
        private readonly int[] _indices;
        private readonly SurfaceRasterHit?[] _hits;
        private readonly double? _plane;
        private readonly SurfaceProjectionPlacement _placement;
        private readonly bool _shown;
        private readonly int _coverage;

        public RetainedFrame(SurfaceRasterSnapshot frame)
        {
            Frame = frame;
            _source = frame.Source;
            _vertices = frame.Source.Vertices.ToArray();
            _triangles = frame.Triangles.ToArray();
            _pixels = frame.Pixels.ToArray();
            _projection = frame.ProjectionPixels.ToArray();
            _depths = frame.Depths.ToArray();
            _indices = frame.TriangleIndices.ToArray();
            _hits = Enumerable.Range(0, frame.Width * frame.Height)
                .Select(pixel => frame.HitTest(pixel % frame.Width, pixel / frame.Width)).ToArray();
            _plane = frame.ProjectionPlaneValue;
            _placement = frame.ProjectionPlacement;
            _shown = frame.ShowProjection;
            _coverage = frame.CoveredPixelCount;
        }

        public SurfaceRasterSnapshot Frame { get; }

        public void AssertUnchanged()
        {
            Assert.Same(_source, Frame.Source);
            Assert.Equal(_vertices, Frame.Source.Vertices.ToArray());
            Assert.Equal(_triangles, Frame.Triangles.ToArray());
            Assert.Equal(_pixels, Frame.Pixels.ToArray());
            Assert.Equal(_projection, Frame.ProjectionPixels.ToArray());
            Assert.Equal(_depths, Frame.Depths.ToArray());
            Assert.Equal(_indices, Frame.TriangleIndices.ToArray());
            Assert.Equal(_plane, Frame.ProjectionPlaneValue);
            Assert.Equal(_placement, Frame.ProjectionPlacement);
            Assert.Equal(_shown, Frame.ShowProjection);
            Assert.Equal(_coverage, Frame.CoveredPixelCount);
            for (var pixel = 0; pixel < _hits.Length; pixel++)
                Assert.Equal(_hits[pixel], Frame.HitTest(pixel % Frame.Width, pixel / Frame.Width));
        }
    }
}
