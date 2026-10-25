using Olivia.Core.Editing;
using Xunit;
namespace Olivia.Core.Tests;
public sealed class SparklineTests
{
    [Fact]
    public void Literal_snapshot_statistics_and_all_modes_preserve_values()
    {
        decimal[] values = [-5, -3.5m, -2, -.5m, 0, .5m, 1, 2.5m, 4, 5]; var s = new SparklineSnapshot(values); values[0] = 900;
        Assert.Equal(10, s.Count); Assert.Equal(-5, s.Minimum); Assert.Equal(5, s.Maximum); Assert.Equal("2", s.SumExact);
        Assert.Equal(5, s.Positive); Assert.Equal(4, s.Negative); Assert.Equal(1, s.Zero);
        foreach (var mode in Enum.GetValues<SparklineMode>()) { Assert.Equal(10, s.Geometry(mode).Points.Count); Assert.Equal("2", s.SumExact); }
        Assert.Equal(new decimal[] { -5, -3.5m, -2, -.5m, 0, .5m, 1, 2.5m, 4, 5 }, s.Values);
    }
    [Fact]
    public void Geometry_has_literal_zero_baseline_and_mode_specific_positions()
    {
        var s = new SparklineSnapshot([-2, 0, 4]);
        foreach (var mode in new[] { SparklineMode.Line, SparklineMode.Area })
        {
            var g = s.Geometry(mode); Assert.Equal(2d / 3, g.Baseline, 12);
            Assert.Equal(new[] { 0d, .5, 1 }, g.Points.Select(p => p.X));
            Assert.Equal(1, g.Points[0].Y); Assert.Equal(2d / 3, g.Points[1].Y, 12); Assert.Equal(0, g.Points[2].Y);
        }
        var bar = s.Geometry(SparklineMode.Bar); Assert.Equal(1d / 6, bar.Points[0].X, 12); Assert.Equal(.5, bar.Points[1].X); Assert.Equal(5d / 6, bar.Points[2].X, 12); Assert.Equal(.7 / 3, bar.BarWidth);
        var sign = s.Geometry(SparklineMode.WinLoss); Assert.Equal(.5, sign.Baseline); Assert.Equal(new[] { 1d, .5, 0 }, sign.Points.Select(p => p.Y));
    }
    [Fact]
    public void Exact_sum_retains_all_decimal_digits_without_cancellation_loss()
    {
        Assert.Equal("1000000.0000000000000000000000000001", new SparklineSnapshot([1000000, .0000000000000000000000000001m]).SumExact);
        Assert.Equal("0.0000000000000000000000000001", new SparklineSnapshot([1000000, .0000000000000000000000000001m, -1000000]).SumExact);
        Assert.Equal("-0.0000000000000000000000000001", new SparklineSnapshot([-.0000000000000000000000000001m]).SumExact);
        Assert.Equal("0", new SparklineSnapshot([0m, -0.00m]).SumExact);
    }
    [Fact]
    public void Empty_single_zero_constants_and_tiny_values_have_finite_geometry()
    {
        var empty = new SparklineSnapshot([]); Assert.Null(empty.Minimum); Assert.Null(empty.Maximum); Assert.Equal("0", empty.SumExact);
        foreach (var mode in Enum.GetValues<SparklineMode>()) { Assert.Empty(empty.Geometry(mode).Points); Assert.Equal(.5, empty.Geometry(mode).Baseline); }
        Assert.Equal(new SparklinePoint(.5, 0), new SparklineSnapshot([5]).Geometry(SparklineMode.Line).Points[0]);
        Assert.Equal(new[] { .5, .5, .5 }, new SparklineSnapshot([0, 0, 0]).Geometry(SparklineMode.Area).Points.Select(p => p.Y));
        Assert.Equal(new[] { 0d, 0, 0 }, new SparklineSnapshot([2, 2, 2]).Geometry(SparklineMode.Bar).Points.Select(p => p.Y));
        Assert.Equal(0, new SparklineSnapshot([-2, -2]).Geometry(SparklineMode.Line).Baseline);
        foreach (var mode in Enum.GetValues<SparklineMode>()) foreach (var p in new SparklineSnapshot([1e-28m, -1e-28m]).Geometry(mode).Points) Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y));
    }
    [Fact]
    public void Bounds_are_inclusive_and_invalid_input_is_not_repaired()
    {
        Assert.Equal(32, new SparklineSnapshot(Enumerable.Repeat(1000000m, 32)).Count);
        Assert.Equal("32000000", new SparklineSnapshot(Enumerable.Repeat(1000000m, 32)).SumExact);
        Assert.Equal("-32000000", new SparklineSnapshot(Enumerable.Repeat(-1000000m, 32)).SumExact);
        Assert.Throws<ArgumentException>(() => new SparklineSnapshot([decimal.MinValue]));
        Assert.Throws<ArgumentException>(() => new SparklineSnapshot(Enumerable.Repeat(0m, 33)));
        Assert.Throws<ArgumentException>(() => new SparklineSnapshot([1000000.00000001m])); Assert.Throws<ArgumentException>(() => new SparklineSnapshot([-1000000.00000001m]));
        Assert.Throws<ArgumentException>(() => new SparklineSnapshot([decimal.MaxValue])); Assert.Throws<ArgumentNullException>(() => new SparklineSnapshot(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparklineSnapshot([]).Geometry((SparklineMode)42));
    }
    [Fact]
    public void Session_noops_availability_close_reentry_and_observer_failure()
    {
        var s = new SparklineSession(new([1])); var count = 0;
        s.Changed += (_, _) => { count++; Assert.False(s.SetMode(SparklineMode.Area)); };
        s.Changed += (_, _) => throw new InvalidOperationException();
        Assert.False(s.Replace(new([1]))); Assert.True(s.SetMode(SparklineMode.Bar)); Assert.Equal(1, count); Assert.Equal(1, s.CallbackFailureCount);
        s.SetReadOnly(true); Assert.False(s.Replace(new([2]))); s.SetReadOnly(false); s.SetEnabled(false); Assert.False(s.SetMode(SparklineMode.Line)); s.SetEnabled(true);
        s.Changed += (_, _) => s.Close(); var closed = 0; s.Closed += (_, _) => closed++;
        Assert.True(s.Replace(new([2]))); Assert.True(s.IsClosed); Assert.Equal(1, closed); Assert.Equal("2", s.Snapshot.SumExact);
        s.Close(); Assert.False(s.SetEnabled(true)); Assert.False(s.SetMode(SparklineMode.Line)); Assert.Equal(1, closed);
    }
    [Fact]
    public void Owner_thread_is_enforced()
    {
        var s = new SparklineSession(new([])); Exception? error = null;
        var thread = new Thread(() => { try { _ = s.Snapshot; } catch (Exception e) { error = e; } }); thread.Start(); thread.Join(); Assert.IsType<InvalidOperationException>(error);
    }
}
