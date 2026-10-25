using System.Diagnostics;
using System.Text.Json;
using Olivia.Core.Charts;
using Xunit;
using Xunit.Abstractions;

namespace Olivia.Core.Tests;

/// <summary>A short, default-size model/raster workload in the original Core test project.
/// This uses manual pacing, not the UI dispatcher or a display/compositor FPS measurement.</summary>
public sealed class SurfaceShortRunTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "SurfaceScaleCheck")]
    public void Default_grid_short_run_keeps_one_current_source_and_rejects_closed_ticks()
        => Run(50);

    [Fact]
    [Trait("Category", "SurfaceScaleCheck")]
    public void Maximum_grid_short_run_keeps_one_current_source_and_rejects_closed_ticks()
        => Run(100);

    [Fact]
    [Trait("Category", "SurfaceScaleCheck")]
    public void Maximum_perspective_lit_grid_short_run_keeps_real_mesh_and_projection()
        => Run(100, litPerspective: true);

    [Fact]
    [Trait("Category", "SurfaceScaleCheck")]
    public void Maximum_decorated_grid_short_run_keeps_full_mesh_and_bounded_axis_geometry()
        => Run(100, litPerspective: true, graduatedAxes: true);

    [Fact]
    [Trait("Category", "SurfaceScaleCheck")]
    public void Maximum_themed_grid_short_run_keeps_real_planes_and_bounded_edge_samples()
        => Run(100, litPerspective: true, graduatedAxes: true, themedMaterial: true);

    private void Run(int size, bool litPerspective = false, bool graduatedAxes = false, bool themedMaterial = false)
    {
        using var session = new RealtimeSurfaceSession(size);
        var vertices = (size + 1) * (size + 1);
        var triangles = 2 * size * size;
        session.Start();
        var generation = session.Generation;
        var options = litPerspective ? SurfaceRenderingOptions.LitPerspective : SurfaceRenderingOptions.Legacy;
        if (themedMaterial) options = options with
        {
            Material = new(new(79, 129, 189), new(192, 80, 77), new(155, 187, 89), new(255, 255, 255)),
            HeightSideMarginFraction = .1,
            EdgeAntialiasing = true
        };
        var camera = litPerspective
            ? new Point3DCamera(yaw: 40 * Math.PI / 180, pitch: 20 * Math.PI / 180, halfHeight: 4 * Math.Tan(Math.PI / 8))
            : new Point3DCamera(yaw: .65, pitch: .55);
        long axisAllocated = 0;
        var axisTime = TimeSpan.Zero;
        var maximumAxisSpans = 0;
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var heapBefore = GC.GetTotalMemory(false);
        var elapsed = Stopwatch.StartNew();
        var compute = TimeSpan.Zero;
        var next = TimeSpan.Zero;
        var frames = 0;
        var covered = 0;
        var edgeSampledPixels = 0;
        long generationAllocated = 0, rasterAllocated = 0;
        SurfaceRasterSnapshot? last = null;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
        {
            var workStart = Stopwatch.GetTimestamp();
            var generationStart = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(session.Tick(generation));
            generationAllocated += GC.GetAllocatedBytesForCurrentThread() - generationStart;
            var source = Assert.IsType<SurfaceChartSource>(session.CurrentSource);
            if (graduatedAxes && frames == 0)
                camera = SurfaceCameraLayout.Create(source, session.MinValue, session.MaxValue, 640, 480);
            Assert.Equal(vertices, source.Vertices.Length);
            var rasterStart = GC.GetAllocatedBytesForCurrentThread();
            var raster = SurfaceRasterizer.Render(source, camera, 640, 480, session.MinValue, session.MaxValue,
                litPerspective, SurfaceProjectionPlacement.Auto,
                options);
            rasterAllocated += GC.GetAllocatedBytesForCurrentThread() - rasterStart;
            if (graduatedAxes)
            {
                var axisStart = Stopwatch.GetTimestamp(); var axisBytes = GC.GetAllocatedBytesForCurrentThread();
                var axes = SurfaceAxisReference.Build(raster, true);
                axisAllocated += GC.GetAllocatedBytesForCurrentThread() - axisBytes;
                axisTime += Stopwatch.GetElapsedTime(axisStart);
                maximumAxisSpans = Math.Max(maximumAxisSpans, axes.Spans.Length);
                Assert.Same(raster, axes.Raster);
                Assert.InRange(axes.Lines.Length, 1, SurfaceAxisReference.MaximumDecoratedLineCount);
                Assert.InRange(axes.Ticks.Length, 0, 3 * SurfaceAxisReference.MaximumTicksPerAxis);
            }
            Assert.Same(source, raster.Source);
            Assert.Equal(triangles, raster.Triangles.Length);
            if (themedMaterial)
            {
                Assert.Contains(raster.TriangleIndices, index => index < -1);
                Assert.Equal(640 * 480, raster.Depths.Length);
                Assert.Equal(640 * 480, raster.TriangleIndices.Length);
            }
            covered = raster.CoveredPixelCount;
            edgeSampledPixels = raster.EdgeSampledPixelCount;
            Assert.True(covered > 0, "The real triangle surface must cover pixels.");
            last = raster;
            frames++;
            compute += Stopwatch.GetElapsedTime(workStart);
            next += RealtimeSurfaceSession.TickInterval;
            var remaining = next - elapsed.Elapsed;
            if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
            else next = elapsed.Elapsed; // No catch-up queue or unbounded backlog.
        }
        elapsed.Stop();
        var version = Assert.IsType<SurfaceRasterSnapshot>(last).Source.Version;
        Assert.Equal(frames, session.ProducedTicks);
        session.Stop();
        Assert.False(session.Tick(generation));
        Assert.Null(session.CurrentSource);
        Assert.True(session.IsStopped);
        var result = new
        {
            scope = "Manual 40ms-paced model + real 640x480 CPU depth raster; no UI dispatcher or display FPS claim",
            size, perspective = litPerspective, lighting = litPerspective, xoyProjection = litPerspective,
            graduatedAxes, themedMaterial, heightSideMarginFraction = options.HeightSideMarginFraction,
            edgeAntialiasing = options.EdgeAntialiasing,
            axisComputeSeconds = axisTime.TotalSeconds, axisAllocatedBytes = axisAllocated, maximumAxisSpans,
            frames, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            synchronousComputeSeconds = compute.TotalSeconds,
            configuredIntervalMilliseconds = RealtimeSurfaceSession.TickInterval.TotalMilliseconds,
            rawVerticesPerFrame = vertices, trianglesPerFrame = triangles, lastCoveredPixels = covered, edgeSampledPixels,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocatedBefore,
            generationAllocatedBytes = generationAllocated, rasterAllocatedBytes = rasterAllocated,
            surfaceVertexBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<SurfaceVertex>(),
            surfaceTriangleBytes = System.Runtime.CompilerServices.Unsafe.SizeOf<SurfaceTriangle>(),
            heapStartBytes = heapBefore, heapEndBytes = GC.GetTotalMemory(false),
            lastSourceVersion = version, lateTickRejected = true, sourceReleased = true
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        var destination = Environment.GetEnvironmentVariable("OLIVIA_SURFACE_PROBE_OUTPUT");
        if (!string.IsNullOrEmpty(destination))
        {
            Directory.CreateDirectory(destination);
            var filename = themedMaterial ? "surface-themed-max-short-run.json" : graduatedAxes ? "surface-decorated-max-short-run.json" : litPerspective ? "surface-perspective-lit-max-short-run.json"
                : size == 50 ? "surface-short-run.json" : "surface-max-short-run.json";
            File.WriteAllText(Path.Combine(destination, filename), json + "\n");
        }
    }
}
