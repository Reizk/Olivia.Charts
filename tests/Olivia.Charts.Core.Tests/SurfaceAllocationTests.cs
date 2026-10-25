using System.Collections;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class SurfaceAllocationTests
{
    private static readonly Point3DCamera Top = new(pitch: Math.PI / 2);

    [Fact]
    public void Compact_triangles_keep_constructor_with_deconstruction_and_value_equality_contracts()
    {
        var source = new SurfaceChartSource("compact", [-1, 1], [-1, 1], [-1, 0, .5, 1]);
        var frame = SurfaceRasterizer.Render(source, Top, 24, 24, -1, 1);
        var actual = frame.Triangles[0];
        var explicitValue = new SurfaceTriangle(Index: actual.Index, A: actual.A, B: actual.B, C: actual.C);
        Assert.Equal(explicitValue, actual); Assert.True(explicitValue == actual);
        Assert.Equal(explicitValue.GetHashCode(), actual.GetHashCode());
        Assert.Equal(explicitValue.ToString(), actual.ToString());
        var (index, a, b, c) = actual;
        Assert.Equal(actual.Index, index); Assert.Equal(actual.A, a); Assert.Equal(actual.B, b); Assert.Equal(actual.C, c);
        var changed = actual with { Index = 7, A = c, B = a, C = b };
        Assert.Equal(new SurfaceTriangle(7, c, a, b), changed);
        Assert.Equal(explicitValue, actual); Assert.Equal(actual, frame.Triangles[0]);
        var initialized = new SurfaceTriangle { Index = 7, A = c, B = a, C = b };
        Assert.Equal(changed, initialized);
        Assert.Equal(new SurfaceTriangle(0, default, default, default), default(SurfaceTriangle));
    }

    [Fact]
    public void Maximum_mesh_topology_uses_less_than_one_megabyte_without_copying_raw_vertex_triplets()
    {
        using var session = new RealtimeSurfaceSession(100);
        // Zero viewport isolates real 20,000-triangle mesh ownership from pixel buffers.
        var source = session.CurrentSource!;
        var start = GC.GetAllocatedBytesForCurrentThread();
        var frame = SurfaceRasterizer.Render(source, Top, 0, 0, session.MinValue, session.MaxValue);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(20_000, frame.Triangles.Length);
        Assert.InRange(allocated, 1, 1_000_000);
        foreach (var triangle in frame.Triangles)
        {
            Assert.Equal(source.Vertices[(int)triangle.A.Key], triangle.A);
            Assert.Equal(source.Vertices[(int)triangle.B.Key], triangle.B);
            Assert.Equal(source.Vertices[(int)triangle.C.Key], triangle.C);
        }
    }

    [Fact]
    public void Cancelled_render_does_not_allocate_frame_buffers_or_change_a_published_snapshot()
    {
        using var session = new RealtimeSurfaceSession(100);
        var retained = new RetainedRaster(RenderCurrent(session, 32, 32));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<OperationCanceledException>(() => SurfaceRasterizer.Render(session.CurrentSource!, Top,
            1024, 1024, session.MinValue, session.MaxValue, true, SurfaceProjectionPlacement.Auto, cancellation.Token));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 1_048_576,
            "A cancelled request must not allocate its megapixel buffers.");
        // Reuse private projection scratch storage for a different camera and projection.
        SurfaceRasterizer.Render(session.CurrentSource!, new(yaw: .4, pitch: .5), 48, 48,
            session.MinValue, session.MaxValue, true, SurfaceProjectionPlacement.Top, CancellationToken.None);
        retained.AssertUnchanged();
    }

    [Theory]
    [InlineData(4, -1, 5, 4)]
    [InlineData(3, -1, 4, 3)]
    [InlineData(6, -1, 5, 4)]
    [InlineData(4, 2, 3, 2)]
    public void HeightCopyDisposesExactlyOnceOnSuccessShortOverflowAndIteratorFailure(
        int count, int throwAt, int expectedMoves, int expectedReads)
    {
        var heights = new HeightEnumerable(count, throwAt);
        SurfaceChartSource Create() => new("enumerated", [-1, 1], [-1, 1], heights);

        if (throwAt >= 0)
            Assert.Same(heights.Failure, Assert.Throws<InvalidOperationException>(() => Create()));
        else if (count != 4)
        {
            var error = Assert.Throws<ArgumentException>(() => Create());
            Assert.Equal("values", error.ParamName);
        }
        else
            Assert.Equal(new double?[] { 0, 1, 2, 3 }, Create().Values.ToArray());

        Assert.Equal(1, heights.Enumerations);
        Assert.Equal(expectedMoves, heights.MoveCalls);
        // Overflow detects the first extra MoveNext without reading its Current.
        Assert.Equal(expectedReads, heights.CurrentReads);
        Assert.Equal(1, heights.DisposeCalls);
    }

    [Fact]
    public void NullHeightsKeepTheOriginalArgumentExceptionContract()
    {
        var error = Assert.Throws<ArgumentNullException>(() =>
            new SurfaceChartSource("null-heights", [-1, 1], [-1, 1], null!));
        Assert.Equal("values", error.ParamName);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void FullAndGappedMeshesOwnTheirInputAndSurviveLaterRenders(bool gap, int triangleCount)
    {
        double[] x = [-1, 1];
        double[] y = [-1, 1];
        double?[] heights = [-1, gap ? null : 0, .5, 1];
        var source = new SurfaceChartSource("caller-owned", x, y, heights, 7);
        var retained = new RetainedRaster(SurfaceRasterizer.Render(source, Top, 32, 32, -1, 1));
        Assert.Equal(triangleCount, retained.Frame.Triangles.Length);
        Assert.Equal(new long[] { 0, 2, 3 }, Keys(retained.Frame.Triangles[0]));
        if (!gap) Assert.Equal(new long[] { 0, 3, 1 }, Keys(retained.Frame.Triangles[1]));

        x[0] = -2;
        y[0] = -3;
        heights[0] = .25;
        heights[1] = .75;
        retained.AssertUnchanged();

        var laterSource = new SurfaceChartSource("caller-owned", x, y, heights, 8);
        var later = SurfaceRasterizer.Render(laterSource, Top, 32, 32, -1, 1);
        Assert.NotSame(source, later.Source);
        Assert.Equal(2, later.Triangles.Length);
        var resized = SurfaceRasterizer.Render(laterSource, Top, 23, 17, -1, 1);
        Assert.Equal(23 * 17, resized.Depths.Length);
        retained.AssertUnchanged();
    }

    [Fact]
    public void PublishedSourceRasterAndRawHitSurviveTicksPauseResizeAndStop()
    {
        using var session = new RealtimeSurfaceSession(15);
        var initial = new RetainedRaster(RenderCurrent(session, 32, 32));
        session.Start();
        var runningToken = session.Generation;
        Assert.True(session.Tick(runningToken));
        var next = new RetainedRaster(RenderCurrent(session, 32, 32));
        Assert.Equal(initial.Frame.Source.Version + 1, next.Frame.Source.Version);
        initial.AssertUnchanged();

        session.Pause();
        var pausedToken = session.Generation;
        Assert.False(session.Tick(runningToken));
        Assert.False(session.Tick(pausedToken));
        Assert.Same(next.Frame.Source, session.CurrentSource);
        session.Resize(16);
        var resized = new RetainedRaster(RenderCurrent(session, 24, 20));
        Assert.Equal(17, resized.Frame.Source.XCount);
        Assert.Equal(17, resized.Frame.Source.YCount);
        Assert.Equal(next.Frame.Source.Version + 1, resized.Frame.Source.Version);
        Assert.True(session.IsPaused);
        Assert.False(session.Tick(pausedToken));
        Assert.False(session.Tick(session.Generation));
        initial.AssertUnchanged();
        next.AssertUnchanged();

        session.Resume();
        var resumedToken = session.Generation;
        Assert.True(session.Tick(resumedToken));
        var later = RenderCurrent(session, 32, 32);
        Assert.Equal(resized.Frame.Source.Version + 1, later.Source.Version);
        session.Stop();
        Assert.True(session.IsStopped);
        Assert.Null(session.CurrentSource);
        Assert.False(session.Tick(resumedToken));
        Assert.False(session.Tick(session.Generation));
        initial.AssertUnchanged();
        next.AssertUnchanged();
        resized.AssertUnchanged();
    }

    private static SurfaceRasterSnapshot RenderCurrent(RealtimeSurfaceSession session, int width, int height) =>
        SurfaceRasterizer.Render(Assert.IsType<SurfaceChartSource>(session.CurrentSource), Top,
            width, height, session.MinValue, session.MaxValue);

    private static long[] Keys(SurfaceTriangle triangle) => [triangle.A.Key, triangle.B.Key, triangle.C.Key];

    private sealed class RetainedRaster
    {
        private readonly SurfaceChartSource _source;
        private readonly double[] _x, _y;
        private readonly double?[] _values;
        private readonly SurfaceVertex[] _vertices;
        private readonly SurfaceTriangle[] _triangles;
        private readonly byte[] _pixels;
        private readonly double[] _depths;
        private readonly int[] _indices;
        private readonly int _hitX, _hitY, _covered;
        private readonly SurfaceRasterHit _hit;

        public RetainedRaster(SurfaceRasterSnapshot frame)
        {
            Frame = frame;
            _source = frame.Source;
            _x = _source.XArguments.ToArray();
            _y = _source.YArguments.ToArray();
            _values = _source.Values.ToArray();
            _vertices = _source.Vertices.ToArray();
            _triangles = frame.Triangles.ToArray();
            _pixels = frame.Pixels.ToArray();
            _depths = frame.Depths.ToArray();
            _indices = frame.TriangleIndices.ToArray();
            _covered = frame.CoveredPixelCount;
            var pixel = Array.FindIndex(_indices, index => index >= 0);
            Assert.InRange(pixel, 0, _indices.Length - 1);
            _hitX = pixel % frame.Width;
            _hitY = pixel / frame.Width;
            _hit = Assert.IsType<SurfaceRasterHit>(frame.HitTest(_hitX, _hitY));
            Assert.Equal(_source.Id, _hit.SourceId);
            Assert.Equal(_source.Version, _hit.Version);
            Assert.Equal(_vertices[(int)_hit.A.Key], _hit.A);
            Assert.Equal(_vertices[(int)_hit.B.Key], _hit.B);
            Assert.Equal(_vertices[(int)_hit.C.Key], _hit.C);
        }

        public SurfaceRasterSnapshot Frame { get; }

        public void AssertUnchanged()
        {
            Assert.Same(_source, Frame.Source);
            Assert.Equal(_x, _source.XArguments.ToArray());
            Assert.Equal(_y, _source.YArguments.ToArray());
            Assert.Equal(_values, _source.Values.ToArray());
            Assert.Equal(_vertices, _source.Vertices.ToArray());
            Assert.Equal(_triangles, Frame.Triangles.ToArray());
            Assert.Equal(_pixels, Frame.Pixels.ToArray());
            Assert.Equal(_depths, Frame.Depths.ToArray());
            Assert.Equal(_indices, Frame.TriangleIndices.ToArray());
            Assert.Equal(_covered, Frame.CoveredPixelCount);
            Assert.Equal(_hit, Assert.IsType<SurfaceRasterHit>(Frame.HitTest(_hitX, _hitY)));
        }
    }

    private sealed class HeightEnumerable(int count, int throwAt) : IEnumerable<double?>
    {
        public int Enumerations { get; private set; }
        public int MoveCalls { get; private set; }
        public int CurrentReads { get; private set; }
        public int DisposeCalls { get; private set; }
        public InvalidOperationException Failure { get; } = new("height iterator failed");

        public IEnumerator<double?> GetEnumerator()
        {
            Enumerations++;
            return new Enumerator(this);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Enumerator(HeightEnumerable owner) : IEnumerator<double?>
        {
            private int _index = -1;
            public double? Current
            {
                get { owner.CurrentReads++; return _index; }
            }
            object IEnumerator.Current => Current!;

            public bool MoveNext()
            {
                owner.MoveCalls++;
                _index++;
                if (_index == owner.ThrowAt) throw owner.Failure;
                return _index < owner.Count;
            }

            public void Reset() => throw new NotSupportedException();
            public void Dispose() => owner.DisposeCalls++;
        }

        private int Count => count;
        private int ThrowAt => throwAt;
    }
}
