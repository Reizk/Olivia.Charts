using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class LargeDataSeriesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(0, 520, 1)]
    [InlineData(0, 520, 17)]
    [InlineData(.25, 520, 31)]
    [InlineData(63.5, 448.25, 9)]
    [InlineData(64, 128, 4)]
    [InlineData(10.2, 10.8, 64)]
    [InlineData(-20, 20.5, 7)]
    [InlineData(500.25, 800, 9)]
    [InlineData(521, 800, 5)]
    [InlineData(-12, -.1, 2)]
    [InlineData(400, 450, 4096)]
    public void Query_matches_independent_raw_index_buckets_and_boundary_neighbors(
        double minimum, double maximum, int width)
    {
        var values = Fixture();
        var series = LargeDataSeries.FromValues(7, values);
        var line = series.Query(minimum, maximum, width);
        Assert.Equal(7, line.SeriesId);
        AssertQuery(values, line, minimum, maximum, width);
    }

    [Fact]
    public void Exact_extrema_match_brute_force_across_block_boundaries_and_ties()
    {
        var values = Fixture();
        var series = LargeDataSeries.FromValues(1, values);
        int[] boundaries = [0, 1, 2, 63, 64, 65, 127, 128, 129, 255, 256, 257, 519, 520, 521];
        foreach (var start in boundaries)
        foreach (var end in boundaries.Where(end => end > start))
        {
            var indices = Enumerable.Range(start, end - start).ToArray();
            var minimum = indices.OrderBy(index => values[index]).First();
            var maximum = indices.OrderByDescending(index => values[index]).First();
            Assert.Equal(new LargeDataExtrema(new(minimum, values[minimum]), new(maximum, values[maximum])),
                series.FindExtrema(start, end));
        }

        Assert.Equal(new LargeDataExtrema(new(63, -1000), new(127, 1000)), series.Extrema!.Value);
        var constant = LargeDataSeries.FromValues(2, Enumerable.Repeat(4f, 193).ToArray());
        Assert.Equal(new LargeDataExtrema(new(1, 4), new(1, 4)), constant.FindExtrema(1, 193));
        Assert.Equal(new LargeDataExtrema(new(64, 4), new(64, 4)), constant.FindExtrema(64, 192));
    }

    [Fact]
    public void Fractional_window_without_samples_still_preserves_the_crossing_segment()
    {
        var series = LargeDataSeries.FromValues(3, [10, 20, 30, 40]);
        var line = series.Query(1.2, 1.8, 100);
        Assert.Equal(0, line.VisibleCount);
        Assert.Equal(new[] { new LargeDataPoint(1, 20), new LargeDataPoint(2, 30) }, line.Points.ToArray());
    }

    [Fact]
    public void Frame_counts_real_points_without_extrapolating_a_shorter_series()
    {
        var longValues = Fixture();
        var shortValues = longValues.Take(67).ToArray();
        var source = new LargeDataSourceSnapshot(42,
            [LargeDataSeries.FromValues(1, longValues), LargeDataSeries.FromValues(2, shortValues)]);

        var frame = LargeDataFrame.Query(source, new(10.25, 450.75, -1500, 1500), 19);
        Assert.Equal(42L, frame.SourceVersion);
        Assert.Equal(588L, source.TotalPoints);
        Assert.Equal(source.Series.Sum(series => series.StorageBytes), source.StorageBytes);
        Assert.Equal(19, frame.PixelWidth);
        Assert.Equal(2, frame.Lines.Length);
        Assert.Equal(new[] { 1, 2 }, frame.Lines.Select(line => line.SeriesId));
        AssertQuery(longValues, frame.Lines[0], 10.25, 450.75, 19);
        AssertQuery(shortValues, frame.Lines[1], 10.25, 450.75, 19);
        Assert.Equal(496L, frame.VisiblePoints); // 11..450 and 11..66, inclusively.
        Assert.Equal(frame.Lines.Sum(line => line.Points.Length), frame.DisplayPoints);
        Assert.Equal(520d, source.Extent!.Value.XMaximum);

        var beyondShorter = LargeDataFrame.Query(source, new(400.25, 450.75, -1500, 1500), 11);
        AssertQuery(longValues, beyondShorter.Lines[0], 400.25, 450.75, 11);
        Assert.Empty(beyondShorter.Lines[1].Points);
        Assert.Equal(0, beyondShorter.Lines[1].VisibleCount);
        Assert.Equal(50L, beyondShorter.VisiblePoints);
        Assert.Equal(67, beyondShorter.Lines[1].SourceCount);
    }

    [Fact]
    public void FromValues_owns_its_input_and_reports_only_dense_array_payload()
    {
        var input = Fixture();
        var expected = input.ToArray();
        var series = LargeDataSeries.FromValues(2, input);
        var originalLine = series.Query(0, input.Length - 1, 13);
        var originalExtrema = series.Extrema;
        Array.Fill(input, 99999f);

        Assert.Equal(expected, Enumerable.Range(0, series.Count).Select(series.GetValue));
        Assert.Equal(originalExtrema, series.Extrema);
        Assert.Equal(originalLine.Points.ToArray(), series.Query(0, series.Count - 1, 13).Points.ToArray());
        Assert.Equal((long)expected.Length * sizeof(float) + ((expected.Length + 63L) / 64) * 2 * sizeof(int),
            series.StorageBytes);
        Assert.True(series.AllowResample);
        Assert.Equal("Series 2", series.Name);
    }

    [Fact]
    public void Nonfinite_samples_are_rejected_in_every_block_including_the_partial_tail()
    {
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        foreach (var index in new[] { 0, 63, 64, 128 })
        {
            var values = new float[129];
            values[index] = invalid;
            Assert.Throws<ArgumentException>(() => LargeDataSeries.FromValues(1, values));
        }

        var empty = LargeDataSeries.FromValues(1, []);
        Assert.Equal(0, empty.Count);
        Assert.Equal(0L, empty.StorageBytes);
        Assert.Null(empty.Extrema);
        Assert.Empty(empty.Query(0, 1, 1).Points);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    public void Generated_values_match_the_official_expression_after_binary32_rounding(int seriesId)
    {
        var series = LargeDataSeries.Generate(4097, seriesId);
        Assert.Equal(seriesId, series.Id);
        Assert.Equal(4097, series.Count);
        foreach (var index in new[] { 0, 1, 99, 100, 999, 1000, 4095, 4096 })
        {
            // Keep the official remainder's position: (0.1 * counter) % 10,
            // not 0.1 * (counter % 10). Check the stored float, not a tolerance in double.
            var expected = (float)(Math.Sin(index) + Math.Sin(index / 100.0)
                + 30 * Math.Sin(index / 1000.0)
                + 1000 * (1 + 0.1 * seriesId % 10) * Math.Sin(index / 100000.0 + Math.PI / 6 * seriesId));
            Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(series.GetValue(index)));
        }
        if (seriesId == 1) Assert.Equal(550f, series.GetValue(0));
        if (seriesId == 3) Assert.Equal(1300f, series.GetValue(0));
    }

    [Fact]
    public void Precancelled_operations_and_invalid_ranges_fail_before_returning_data()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var series = LargeDataSeries.FromValues(1, [1, 2, 3]);
        Assert.Throws<OperationCanceledException>(() => LargeDataSeries.Generate(100, 1, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => LargeDataSeries.FromValues(1, new float[] { 1 }, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => series.FindExtrema(0, 3, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => series.Query(0, 2, 10, cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.FindExtrema(1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.FindExtrema(-1, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.FindExtrema(0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.Query(0, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.Query(0, 2, 4097));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.Query(2, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.Query(double.NaN, 2, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.Query(0, double.PositiveInfinity, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => series.Query(-double.MaxValue, double.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LargeDataView(-double.MaxValue, double.MaxValue, 0, 1));
    }

    [Fact]
    public async Task Session_admits_one_producer_without_a_queue_and_counts_only_committed_sources()
    {
        using var generator = new GatedGenerator(blockOnCall: 1);
        using var session = new LargeDataSession(generator.Generate);
        var empty = session.Snapshot;
        var pending = session.AddSeriesAsync(127);
        var idle = session.WhenIdle;
        try
        {
            await generator.Entered.Task.WaitAsync(Timeout);
            Assert.True(session.IsBusy);
            Assert.False(idle.IsCompleted);
            for (var i = 0; i < 8; i++)
                Assert.Equal(LargeDataAddStatus.Busy, (await session.AddSeriesAsync(11)).Status);
            Assert.Same(empty, session.Snapshot);
            Assert.Same(idle, session.WhenIdle);
            Assert.Equal(1, generator.Calls);
            Assert.Equal(0L, empty.Version);
            Assert.Equal(0L, empty.TotalPoints);
        }
        finally
        {
            generator.Release();
            await pending.WaitAsync(Timeout);
        }

        Assert.Equal(LargeDataAddStatus.Added, (await pending).Status);
        Assert.False(session.IsBusy);
        Assert.True(idle.IsCompletedSuccessfully);
        Assert.Equal(1, generator.Calls);
        var first = session.Snapshot;
        Assert.Equal(1L, first.Version);
        Assert.Equal(127L, first.TotalPoints);
        Assert.Equal(1, Assert.Single(first.Series).Id);
        Assert.Equal(LargeDataAddStatus.Added, (await session.AddSeriesAsync(13).WaitAsync(Timeout)).Status);
        Assert.Equal(2, generator.Calls);
        Assert.Equal(2L, session.Snapshot.Version);
        Assert.Equal(140L, session.Snapshot.TotalPoints);
        Assert.Equal(new[] { 127, 13 }, session.Snapshot.Series.Select(series => series.Count));
        Assert.Equal(new[] { 1, 2 }, session.Snapshot.Series.Select(series => series.Id));
        Assert.Equal(127L, first.TotalPoints);
        Assert.Single(first.Series); // An earlier publication remains immutable.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clear_or_dispose_invalidates_a_late_result_even_when_generation_ignores_cancellation(bool dispose)
    {
        using var generator = new GatedGenerator(blockOnCall: 2);
        using var session = new LargeDataSession(generator.Generate);
        Assert.Equal(LargeDataAddStatus.Added, (await session.AddSeriesAsync(23).WaitAsync(Timeout)).Status);
        var previous = session.Snapshot;
        var pending = session.AddSeriesAsync(31);
        var idle = session.WhenIdle;
        try
        {
            await generator.Entered.Task.WaitAsync(Timeout);
            if (dispose) session.Dispose(); else session.Clear();
            Assert.True(generator.BlockedToken.IsCancellationRequested);
            Assert.True(session.IsBusy); // Cancellation cannot free a still-running physical producer.
            Assert.False(idle.IsCompleted);
            Assert.Equal(2L, session.Snapshot.Version);
            Assert.Equal(0L, session.Snapshot.TotalPoints);
            Assert.Empty(session.Snapshot.Series);
            Assert.Null(session.Snapshot.Extent);
            Assert.Equal(dispose ? LargeDataAddStatus.Closed : LargeDataAddStatus.Busy,
                (await session.AddSeriesAsync(7)).Status);
            Assert.Equal(2, generator.Calls);
        }
        finally
        {
            generator.Release();
            await pending.WaitAsync(Timeout);
        }

        Assert.Equal(LargeDataAddStatus.Cancelled, (await pending).Status);
        Assert.False(session.IsBusy);
        Assert.True(idle.IsCompletedSuccessfully);
        Assert.Equal(2L, session.Snapshot.Version);
        Assert.Empty(session.Snapshot.Series);
        Assert.Equal(23L, previous.TotalPoints);
        Assert.Equal(1L, previous.Version);
        if (dispose)
        {
            var closed = session.Snapshot;
            session.Dispose();
            session.Clear();
            Assert.Equal(LargeDataAddStatus.Closed, (await session.AddSeriesAsync(13)).Status);
            Assert.Same(closed, session.Snapshot);
            Assert.Equal(2, generator.Calls);
        }
        else
        {
            Assert.Equal(LargeDataAddStatus.Added, (await session.AddSeriesAsync(13).WaitAsync(Timeout)).Status);
            Assert.Equal(3, generator.Calls);
            Assert.Equal(3L, session.Snapshot.Version);
            Assert.Equal(13L, session.Snapshot.TotalPoints);
            Assert.Equal(3, Assert.Single(session.Snapshot.Series).Id);
        }
    }

    [Fact]
    public async Task Explicit_series_budget_rejects_without_dropping_data_and_navigation_clamps_both_axes()
    {
        using var session = new LargeDataSession();
        for (var i = 0; i < 64; i++)
            Assert.Equal(LargeDataAddStatus.Added, (await session.AddSeriesAsync(1)).Status);
        var full = session.Snapshot;
        Assert.Equal(LargeDataAddStatus.CapacityReached, (await session.AddSeriesAsync(1)).Status);
        Assert.Same(full, session.Snapshot);
        Assert.Equal(64, full.TotalPoints);
        var extent = new LargeDataView(0, 2_499_999, -2000, 2000);
        var zoomed = extent.Zoom(1_000_000, .5, .5).Zoom(1_000_000, .5, .5, yOnly: true).Clamp(extent);
        Assert.Equal(extent.Width / 10_000, zoomed.Width, 6);
        Assert.Equal(extent.Height / 10_000, zoomed.Height, 6);
        Assert.Equal(zoomed, zoomed.ZoomWithin(extent, 2));
        Assert.Equal(zoomed, zoomed.ZoomWithin(extent, 2, yOnly: true));
        Assert.Equal(extent, extent.ZoomWithin(extent, .5));
        var panned = zoomed.Pan(1_000_000, -1_000_000).Clamp(extent);
        Assert.Equal(extent.XMaximum, panned.XMaximum, 6);
        Assert.Equal(extent.YMinimum, panned.YMinimum, 6);
    }

    private static float[] Fixture()
    {
        var values = Enumerable.Range(0, 521).Select(index => (float)(index * 37 % 101 - 50)).ToArray();
        values[63] = values[64] = values[255] = -1000;
        values[127] = values[128] = values[256] = 1000;
        return values;
    }

    private static void AssertQuery(float[] values, LargeDataLine actual, double minimum, double maximum, int width)
    {
        // Enumerate raw source indices independently of the engine's block index and range rounding.
        var visible = Enumerable.Range(0, values.Length).Where(index => index >= minimum && index <= maximum).ToArray();
        var expected = new SortedSet<int>();
        if (values.Length > 0 && maximum >= 0 && minimum <= values.Length - 1)
        {
            expected.Add((int)Math.Clamp(Math.Floor(minimum), 0, values.Length - 1));
            expected.Add((int)Math.Clamp(Math.Ceiling(maximum), 0, values.Length - 1));
            for (var column = 0; column < width; column++)
            {
                var left = minimum + (maximum - minimum) * column / width;
                var right = minimum + (maximum - minimum) * (column + 1) / width;
                var bucket = visible.Where(index => index >= left
                    && (column == width - 1 ? index <= maximum : index < right)).ToArray();
                if (bucket.Length == 0) continue;
                expected.Add(bucket.OrderBy(index => values[index]).First());
                expected.Add(bucket.OrderByDescending(index => values[index]).First());
            }
        }

        Assert.Equal(values.Length, actual.SourceCount);
        Assert.Equal(visible.Length, actual.VisibleCount);
        Assert.Equal(expected.Select(index => new LargeDataPoint(index, values[index])).ToArray(), actual.Points.ToArray());
        Assert.True(actual.Points.Length <= 2 * width + 2);
        Assert.All(actual.Points, point => Assert.Equal(values[point.Index], point.Value));
        Assert.True(actual.Points.Zip(actual.Points.Skip(1), (first, second) => first.Index < second.Index).All(ordered => ordered));
    }

    private sealed class GatedGenerator(int blockOnCall) : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken BlockedToken { get; private set; }

        public LargeDataSeries Generate(int count, int id, CancellationToken token)
        {
            if (Interlocked.Increment(ref _calls) == blockOnCall)
            {
                BlockedToken = token;
                Entered.TrySetResult(true);
                if (!_release.Wait(Timeout)) throw new TimeoutException("The test did not release the gated generator.");
            }
            // Intentionally ignore the borrowed cancellation token: session epoch checks must protect publication.
            return LargeDataSeries.FromValues(id, Enumerable.Range(0, count).Select(index => (float)(id + index)).ToArray());
        }

        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
