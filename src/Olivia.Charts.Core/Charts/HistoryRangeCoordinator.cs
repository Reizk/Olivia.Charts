namespace Olivia.Core.Charts;

/// <summary>A token for one immutable source/range revision, not a data point key or source version.</summary>
public readonly record struct HistoryRangeToken(long SourceIdentity, long Revision);

/// <summary>Independent UTC range state. OverviewVisible is deliberately always Whole in this first slice.
/// Selection is the main plot's requested interval, never its possibly older displayed frame.</summary>
public sealed record HistoryRangeCoordinator
{
    private HistoryRangeCoordinator(long sourceIdentity, long revision, TimeWindow? whole, TimeWindow? selected)
    { SourceIdentity = sourceIdentity; Revision = revision; Whole = whole; SelectedWindow = selected; }

    public static HistoryRangeCoordinator Empty { get; } = new(0, 0, null, null);
    public long SourceIdentity { get; }
    public long Revision { get; }
    public TimeWindow? Whole { get; }
    public TimeWindow? OverviewVisible => Whole;
    public TimeWindow? SelectedWindow { get; }
    public bool FollowsWhole => SelectedWindow == Whole;
    public HistoryRangeToken Token => new(SourceIdentity, Revision);

    /// <summary>Call only after the owning plot accepted this source. Manual windows keep absolute ticks
    /// then clamp; full-domain mode follows the new domain. No fixed-width trailing mode is implied.</summary>
    public HistoryRangeCoordinator AcceptSource(long identity, TimeWindow? whole)
    {
        if (identity <= 0) throw new ArgumentOutOfRangeException(nameof(identity));
        if (identity == SourceIdentity)
        {
            if (whole != Whole) throw new ArgumentException("One source identity must have one extent.", nameof(whole));
            return this;
        }
        if (identity < SourceIdentity) throw new ArgumentOutOfRangeException(nameof(identity));
        var selected = whole is { } extent
            ? FollowsWhole || SelectedWindow is null ? extent : SelectedWindow.Value.ClampTo(extent)
            : (TimeWindow?)null;
        return new(identity, checked(Revision + 1), whole, selected);
    }

    /// <summary>Stale drag/source tokens and equal effective windows are exact no-ops.</summary>
    public HistoryRangeCoordinator Select(TimeWindow requested, HistoryRangeToken? token = null)
    {
        if (token is { } expected && expected != Token || Whole is not { } extent) return this;
        var selected = requested.ClampTo(extent);
        return selected == SelectedWindow ? this
            : new(SourceIdentity, checked(Revision + 1), Whole, selected);
    }
}
