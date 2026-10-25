using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class HistoryRangeCoordinatorTests
{
    [Fact]
    public void Whole_mode_follows_growth_and_manual_range_keeps_absolute_ticks_then_clamps()
    {
        var state = HistoryRangeCoordinator.Empty.AcceptSource(1, new(100, 200));
        Assert.Equal(state.Whole, state.OverviewVisible);
        state = state.AcceptSource(2, new(100, 400));
        Assert.Equal(new TimeWindow(100, 400), state.SelectedWindow);
        state = state.Select(new(150, 190));
        state = state.AcceptSource(3, new(0, 900));
        Assert.Equal(new TimeWindow(150, 190), state.SelectedWindow);
        state = state.AcceptSource(4, new(0, 160));
        Assert.Equal(new TimeWindow(120, 160), state.SelectedWindow);
        state = state.AcceptSource(5, new(0, 20));
        Assert.Equal(state.Whole, state.SelectedWindow);
    }

    [Fact]
    public void Duplicate_and_stale_actions_preserve_reference_revision_and_source_identity()
    {
        var state = HistoryRangeCoordinator.Empty.AcceptSource(1, new(0, 100));
        var token = state.Token;
        Assert.Same(state, state.Select(new(0, 100)));
        Assert.Same(state, state.AcceptSource(1, new(0, 100)));
        state = state.Select(new(20, 50), token);
        Assert.Same(state, state.Select(new(30, 60), token));
        var oldSource = state.Token;
        state = state.AcceptSource(2, new(0, 100));
        Assert.Same(state, state.Select(new(30, 60), oldSource));
        Assert.Throws<ArgumentException>(() => state.AcceptSource(2, new(0, 200)));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.AcceptSource(1, new(0, 100)));
    }

    [Fact]
    public void Empty_single_tick_maximum_tick_and_half_open_windows_are_exact()
    {
        var maximum = TimeWindow.MaximumExclusiveTicks;
        var source = new HistorySeries([new(1, maximum - 1, 1), new(2, maximum - 1, 2)], 1);
        var state = HistoryRangeCoordinator.Empty.AcceptSource(1, source.Extent);
        Assert.Equal(new TimeWindow(maximum - 1, maximum), state.Whole);
        Assert.Equal(1, state.Whole!.Value.DurationTicks);
        Assert.Equal(2, source.ReadWindow(state.Whole.Value).Samples.Length);
        state = state.Select(new(maximum, maximum));
        Assert.Equal(new TimeWindow(maximum, maximum), state.SelectedWindow);
        Assert.False(state.SelectedWindow!.Value.Contains(maximum - 1));
        state = state.AcceptSource(2, null);
        Assert.Null(state.SelectedWindow); Assert.Null(state.Whole); Assert.Null(state.OverviewVisible);
        Assert.Same(state, state.Select(new(0, 1)));
        state = state.AcceptSource(3, new(12, 13));
        Assert.Equal(new TimeWindow(12, 13), state.SelectedWindow);
    }
}
