using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceFibonacciArcTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DraftOwnershipIsExactAndStaleRefusalPrecedesPayloadValidation()
    {
        var (first, second) = Anchors();
        var arcs = new PriceFibonacciArcs();
        var stale = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginNew());
        var current = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginNew());
        var foreign = Assert.IsType<PriceFibonacciArcDraft>(new PriceFibonacciArcs().BeginNew());
        foreach (var rejected in new[] { stale, current with { }, foreign,
            new PriceFibonacciArcDraft(current.Revision, null, null), null! })
        {
            Assert.False(arcs.Commit(rejected, default, default));
            Assert.False(arcs.Cancel(rejected));
            Assert.Same(current, arcs.Draft);
            Assert.Equal(2, arcs.Revision);
        }
        Assert.True(arcs.Commit(current, first, second));
        Assert.False(arcs.Commit(current, first, second));
        Assert.Null(arcs.Draft);
        Assert.Equal(1, Assert.Single(arcs.Items).Id);
    }

    [Fact]
    public void InvalidCurrentAnchorsRetainDraftSelectionAndCommittedSnapshot()
    {
        var (first, second) = Anchors();
        var arcs = new PriceFibonacciArcs();
        var original = Add(arcs, first, second);
        var snapshot = arcs.Items;
        var draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginEdit(original.Id));
        var revision = arcs.Revision;
        var sameInstant = second with { Hit = second.Hit with { Bar = new PriceBar(90,
            first.Hit.Timestamp.ToOffset(TimeSpan.FromHours(2)), 8, 8, 8, 8) } };
        foreach (var invalid in new[] { default(PriceTrendAnchor), first,
            second with { Level = (PriceTrendValueLevel)99 },
            second with { Hit = second.Hit with { SeriesId = "foreign" } },
            second with { Hit = second.Hit with { DatasetVersion = 42 } },
            second with { Hit = second.Hit with { SourceVersion = -1 } },
            second with { Hit = second.Hit with { Bar = new PriceBar(first.Hit.Key, second.Hit.Timestamp, 8, 8, 8, 8) } },
            second with { Hit = second.Hit with { Bar = new PriceBar(90, second.Hit.Timestamp, null, null, null, null) } },
            sameInstant })
        {
            Assert.ThrowsAny<ArgumentException>(() => arcs.Commit(draft, first, invalid));
            Assert.Equal(revision, arcs.Revision);
            Assert.Equal(snapshot, arcs.Items);
            Assert.Same(draft, arcs.Draft);
            Assert.Equal(original.Id, arcs.SelectedId);
        }
        Assert.True(arcs.Commit(draft, second, first));
        Assert.Equal(new PriceFibonacciArc(original.Id, second, first), Assert.Single(arcs.Items));
        Assert.Same(original, Assert.Single(snapshot));
    }

    [Fact]
    public void CapacityAndConcurrentCommitAreBoundedWithoutTouchingOtherAnalysisStores()
    {
        var (first, second) = Anchors();
        var arcs = new PriceFibonacciArcs();
        var trends = new PriceTrendLines();
        var retracements = new PriceFibonacciRetracements();
        var fans = new PriceFibonacciFans();
        Assert.True(trends.Commit(Assert.IsType<PriceTrendLineDraft>(trends.BeginNew()), first, second));
        Assert.True(retracements.Commit(Assert.IsType<PriceFibonacciRetracementDraft>(retracements.BeginNew()), first, second));
        Assert.True(fans.Commit(Assert.IsType<PriceFibonacciFanDraft>(fans.BeginNew()), first, second));
        var fanSnapshot = fans.Items;
        var trendSnapshot = trends.Items;
        var retracementSnapshot = retracements.Items;
        var draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginNew());
        var successes = 0;
        Parallel.For(0, 32, _ => { if (arcs.Commit(draft, first, second)) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
        while (arcs.Items.Length < PriceFibonacciArcs.MaximumArcs) Add(arcs, first, second);
        Assert.Equal(32, arcs.Items.Length);
        draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginEdit(1));
        var revision = arcs.Revision;
        Assert.Null(arcs.BeginNew(true, true, true));
        Assert.Same(draft, arcs.Draft);
        Assert.Equal(revision, arcs.Revision);
        Assert.True(arcs.Commit(draft, second, first));
        Assert.True(arcs.RemoveSelected());
        Assert.Equal(33, Add(arcs, first, second).Id);
        arcs.Clear();
        Assert.Equal(fanSnapshot, fans.Items);
        Assert.Equal(trendSnapshot, trends.Items);
        Assert.Equal(retracementSnapshot, retracements.Items);
        Assert.Equal(34, Add(arcs, first, second).Id);
    }

    [Fact]
    public void SelectionCancellationRemovalAndNoOpsPreserveSnapshotsAndInvalidateDrafts()
    {
        var (first, second) = Anchors();
        var arcs = new PriceFibonacciArcs();
        Assert.False(arcs.ClearSelection());
        Assert.False(arcs.RemoveSelected());
        arcs.Clear();
        Assert.Equal(0, arcs.Revision);
        var original = Add(arcs, first, second);
        var snapshot = arcs.Items;
        var draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginEdit(original.Id));
        Assert.True(arcs.Cancel(draft));
        Assert.Equal(original.Id, arcs.SelectedId);
        var revision = arcs.Revision;
        Assert.True(arcs.Select(original.Id));
        Assert.Null(arcs.BeginEdit(999));
        Assert.False(arcs.Select(999));
        Assert.Equal(revision, arcs.Revision);
        draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginNew());
        Assert.Null(arcs.SelectedId);
        Assert.True(arcs.Select(original.Id));
        Assert.False(arcs.Commit(draft, first, second));
        draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginEdit(original.Id));
        Assert.True(arcs.ClearSelection());
        Assert.False(arcs.Cancel(draft));
        Assert.Equal(snapshot, arcs.Items);
        draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginNew());
        Assert.False(arcs.RemoveSelected());
        Assert.Null(arcs.Draft);
        Assert.False(arcs.Cancel(draft));
        Assert.True(arcs.Select(original.Id));
        Assert.True(arcs.RemoveSelected());
        Assert.Same(original, Assert.Single(snapshot));
        Assert.Empty(arcs.Items);
        draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginNew());
        arcs.Clear();
        Assert.False(arcs.Commit(draft, first, second));
    }

    [Fact]
    public void OptionsRequireExactRetainedArcAndPreserveAuthorshipSelectionAndEditFlags()
    {
        var (first, second) = Anchors();
        var arcs = new PriceFibonacciArcs();
        var draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginNew(false, true, true));
        Assert.True(draft.ShowLevel100);
        Assert.False(draft.ShowLevel23_6);
        Assert.True(draft.ShowLevel76_4);
        Assert.True(arcs.Commit(draft, first, second));
        var original = Assert.Single(arcs.Items);
        var snapshot = arcs.Items;
        var revision = arcs.Revision;
        Assert.True(arcs.SetOptions(original, false, true, true));
        Assert.Equal(revision, arcs.Revision);
        Assert.Same(original, Assert.Single(arcs.Items));
        foreach (var rejected in new[] { original with { }, Add(new PriceFibonacciArcs(), first, second), null! })
            Assert.False(arcs.SetOptions(rejected, true, false, false));
        draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginEdit(original.Id));
        Assert.Same(original, draft.Original);
        Assert.True(draft.ShowLevel100);
        Assert.False(draft.ShowLevel23_6);
        Assert.True(draft.ShowLevel76_4);
        Assert.False(arcs.SetOptions(original, true, false, false));
        Assert.True(arcs.Cancel(draft));
        Assert.Same(original, Assert.Single(arcs.Items));
        draft = Assert.IsType<PriceFibonacciArcDraft>(arcs.BeginEdit(original.Id));
        Assert.True(arcs.Commit(draft, second, first));
        var edited = Assert.Single(arcs.Items);
        Assert.Equal(second, edited.First);
        Assert.Equal(first, edited.Second);
        Assert.True(edited.ShowLevel100);
        Assert.False(edited.ShowLevel23_6);
        Assert.True(edited.ShowLevel76_4);
        Assert.False(arcs.SetOptions(original, true, false, false));
        var selected = Add(arcs, first, second);
        revision = arcs.Revision;
        var successes = 0;
        Parallel.For(0, 16, _ => { if (arcs.SetOptions(edited, true, false, false)) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
        var changed = arcs.Items[0];
        Assert.Equal(revision + 1, arcs.Revision);
        Assert.False(changed.ShowLevel100);
        Assert.True(changed.ShowLevel23_6);
        Assert.False(changed.ShowLevel76_4);
        Assert.Equal(edited.Id, changed.Id);
        Assert.Equal(edited.First, changed.First);
        Assert.Equal(edited.Second, changed.Second);
        Assert.Equal(selected.Id, arcs.SelectedId);
        Assert.Same(original, Assert.Single(snapshot));
        Assert.True(original.ShowLevel100);
        Assert.True(original.ShowLevel76_4);
    }

    [Fact]
    public void OriginalOhlcValuesAndAggregateSlotsRemainSeparateFromRadiusAndProvenance()
    {
        var source = Source([new PriceBar(0, Epoch, 6, 9, 3, 7), Bar(1, 100),
            new PriceBar(5, Epoch.AddMinutes(5), 8, 12, 4, 10), Bar(6, 200)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 0, PriceTrendValueLevel.Open),
            Capture(raw, 2, PriceTrendValueLevel.High));
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var current = aggregation.Source.Query(new(fixedDomain: new(0, 200)));
        var result = PriceFibonacciArcGeometry.Project(arc, current, 200, 100, aggregation);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.Equal(0, result.First!.Value.OriginalIndex);
        Assert.Equal(2, result.Second!.Value.OriginalIndex);
        Assert.Equal(0, result.First!.Value.DisplayIndex);
        Assert.Equal(1, result.Second!.Value.DisplayIndex);
        Assert.Equal(arc.First.Hit, result.First!.Value.Hit);
        Assert.Equal(arc.Second.Hit, result.Second!.Value.Hit);
        Assert.Equal(100, aggregation.Source.Bars[0].Close);
        Assert.Equal(200, aggregation.Source.Bars[1].High);
        Assert.Equal(6, arc.First.Value);
        Assert.Equal(12, arc.Second.Value);
        // Display positions are (50,97) and (150,94), not aggregate OHLC values or raw indexes.
        Assert.Equal(Math.Sqrt(100 * 100 + 3 * 3) / 2, result.Levels[1].RadiusDips!.Value, 12);
        AssertSuppressed(PriceTrendProjectionStatus.AggregationMismatch,
            PriceFibonacciArcGeometry.Project(arc, raw, 200, 100, aggregation));
        var stale = PriceTimeAggregator.Aggregate(Source(source.Bars.ToArray(), sourceVersion: 1), PriceTimeInterval.Minute5);
        AssertSuppressed(PriceTrendProjectionStatus.AggregationMismatch,
            PriceFibonacciArcGeometry.Project(arc, current, 200, 100, stale));
    }

    [Fact]
    public void SameAggregateSlotSuppressesEvenDistinctPricesWithoutMutatingAuthorship()
    {
        var source = Source([Bar(0, 2), Bar(1, 8)]);
        var raw = source.Query();
        var arcs = new PriceFibonacciArcs();
        var arc = Add(arcs, Capture(raw, 0), Capture(raw, 1));
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var result = PriceFibonacciArcGeometry.Project(arc, aggregation.Source.Query(), 200, 100, aggregation);
        AssertSuppressed(PriceTrendProjectionStatus.CollapsedAggregateSlot, result);
        Assert.Equal(0, result.First!.Value.DisplayIndex);
        Assert.Equal(0, result.Second!.Value.DisplayIndex);
        Assert.Equal(arc.First.Hit, result.First!.Value.Hit);
        Assert.Equal(arc.Second.Hit, result.Second!.Value.Hit);
        Assert.Same(arc, Assert.Single(arcs.Items));
        Assert.Equal(PriceTrendProjectionStatus.Visible, PriceFibonacciArcGeometry.Project(arc, raw, 200, 100).Status);
    }

    [Fact]
    public void LaterSourceVersionResolvesOriginalKeysRatherThanStaleIndexes()
    {
        var source = Source([Bar(0, 1), Bar(5, 2), Bar(6, 3), Bar(10, 4)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 1), Capture(raw, 3));
        var next = Source(source.Bars.Skip(1).ToArray(), sourceVersion: 9);
        var aggregation = PriceTimeAggregator.Aggregate(next, PriceTimeInterval.Minute5);
        var result = PriceFibonacciArcGeometry.Project(arc, aggregation.Source.Query(), 200, 100, aggregation);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.Equal(0, result.First!.Value.OriginalIndex);
        Assert.Equal(2, result.Second!.Value.OriginalIndex);
        Assert.Equal(0, result.First!.Value.DisplayIndex);
        Assert.Equal(1, result.Second!.Value.DisplayIndex);
        Assert.Equal(9, result.First!.Value.Hit.SourceVersion);
        Assert.Equal(0, arc.First.Hit.SourceVersion);
        Assert.Equal(2, arc.First.Value);
    }

    [Fact]
    public void IdentityTimestampAndSelectedOhlcBitsAreRevalidatedWithoutReauthoring()
    {
        var bars = new[] { new PriceBar(0, Epoch, 1, 10, 0, 2), new PriceBar(1, Epoch.AddMinutes(1), 7, 10, 0, 8) };
        var raw = Source(bars).Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 0), Capture(raw, 1));
        var cases = new (PriceChartSource Source, PriceTrendProjectionStatus Status)[]
        {
            (Source(bars, seriesId: "foreign"), PriceTrendProjectionStatus.DatasetMismatch),
            (Source(bars, datasetVersion: 8), PriceTrendProjectionStatus.DatasetMismatch),
            (Source([bars[1]]), PriceTrendProjectionStatus.MissingAnchor),
            (Source([new PriceBar(0, Epoch.AddTicks(1), 1, 10, 0, 2), bars[1]]), PriceTrendProjectionStatus.MissingAnchor),
            (Source([new PriceBar(0, Epoch.ToOffset(TimeSpan.FromHours(1)), 1, 10, 0, 2), bars[1]]), PriceTrendProjectionStatus.MissingAnchor),
            (Source([new PriceBar(0, Epoch, null, null, null, null), bars[1]]), PriceTrendProjectionStatus.GapAnchor),
            (Source([new PriceBar(0, Epoch, 1, 10, 0, 3), bars[1]]), PriceTrendProjectionStatus.ChangedAnchorValue)
        };
        foreach (var test in cases)
        {
            var result = PriceFibonacciArcGeometry.Project(arc, test.Source.Query(), 200, 100);
            AssertSuppressed(test.Status, result);
            Assert.Same(arc, result.Arc);
            Assert.Equal(2, arc.First.Value);
        }
        var otherFields = Source([new PriceBar(0, Epoch, 5, 20, -1, 2, 99), bars[1]], sourceVersion: 7);
        Assert.Equal(PriceTrendProjectionStatus.Visible, PriceFibonacciArcGeometry.Project(arc, otherFields.Query(), 200, 100).Status);
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var signed = Source([Bar(0, 1), new PriceBar(1, Epoch.AddMinutes(1), 0, 0, negativeZero, negativeZero)]);
        arc = new(1, Capture(signed.Query(), 0), Capture(signed.Query(), 1));
        var changed = Source([Bar(0, 1), Bar(1, 0)], sourceVersion: 1);
        AssertSuppressed(PriceTrendProjectionStatus.ChangedAnchorValue,
            PriceFibonacciArcGeometry.Project(arc, changed.Query(), 200, 100));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(arc.Second.Value));
    }

    [Fact]
    public void EmptyNoDomainAndSnapshotBudgetRefusalRetainResolvedOriginalAnchors()
    {
        var source = Source([Bar(0, 2), new PriceBar(1, Epoch.AddMinutes(1), null, null, null, null), Bar(2, 8)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 0), Capture(raw, 2));
        foreach (var (snapshot, status) in new[]
        {
            (source.Query(new(barCount: 0)), PriceTrendProjectionStatus.EmptyViewport),
            (source.Query(new(1, 1)), PriceTrendProjectionStatus.NoDomain),
            (source.Query(new(geometryBudget: 0)), PriceTrendProjectionStatus.GeometryBudgetExceeded)
        })
        {
            var result = PriceFibonacciArcGeometry.Project(arc, snapshot, 200, 100);
            AssertSuppressed(status, result);
            Assert.Equal(arc.First.Hit, result.First!.Value.Hit);
            Assert.Equal(arc.Second.Hit, result.Second!.Value.Hit);
        }
    }

    [Theory]
    [InlineData(false, false, false, 3)]
    [InlineData(true, false, false, 4)]
    [InlineData(false, true, false, 4)]
    [InlineData(false, false, true, 4)]
    [InlineData(true, true, true, 6)]
    public void LevelsAreFixed382500618AndOnlyOptional2367641000(bool early, bool late, bool hundred, int count)
    {
        var source = Source([Bar(0, 50), Bar(1, 50)]);
        var arc = new PriceFibonacciArc(1, Capture(source.Query(), 0), Capture(source.Query(), 1))
            { ShowLevel23_6 = early, ShowLevel76_4 = late, ShowLevel100 = hundred };
        var result = PriceFibonacciArcGeometry.Project(arc, source.Query(new(fixedDomain: new(0, 100))), 200, 200);
        Assert.Equal(count, result.Levels.Length);
        Assert.Contains(result.Levels, level => level.Ratio == .382);
        Assert.Contains(result.Levels, level => level.Ratio == .5);
        Assert.Contains(result.Levels, level => level.Ratio == .618);
        Assert.Equal(early, result.Levels.Any(level => level.Ratio == .236));
        Assert.Equal(late, result.Levels.Any(level => level.Ratio == .764));
        Assert.Equal(hundred, result.Levels.Any(level => level.Ratio == 1));
        Assert.DoesNotContain(result.Levels, level => level.Ratio == 0 || level.Ratio > 1);
        Assert.All(result.Levels, level => Assert.Equal(100 * level.Ratio, level.RadiusDips!.Value, 12));
        var defaults = new PriceFibonacciArc(2, arc.First, arc.Second);
        var draft = Assert.IsType<PriceFibonacciArcDraft>(new PriceFibonacciArcs().BeginNew());
        Assert.False(defaults.ShowLevel23_6 || defaults.ShowLevel76_4 || defaults.ShowLevel100);
        Assert.False(draft.ShowLevel23_6 || draft.ShowLevel76_4 || draft.ShowLevel100);
    }

    [Fact]
    public void EuclideanPlotDipMetricRespondsToAnisotropicLayoutAndReversalRecenters()
    {
        var source = Source([Bar(0, 50), Bar(1, 50), Bar(2, 75), Bar(3, 50)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 1), Capture(raw, 2));
        var viewport = source.Query(new(fixedDomain: new(0, 100)));
        var square = PriceFibonacciArcGeometry.Project(arc, viewport, 400, 400).Levels.Single(level => level.Ratio == .5);
        var wide = PriceFibonacciArcGeometry.Project(arc, viewport, 800, 200).Levels.Single(level => level.Ratio == .5);
        Assert.Equal(Math.Sqrt(100 * 100 + 100 * 100) / 2, square.RadiusDips!.Value, 12);
        Assert.Equal(Math.Sqrt(200 * 200 + 50 * 50) / 2, wide.RadiusDips!.Value, 12);
        // A full circle has all four cardinal samples, not a direction-dependent semicircle.
        AssertCircle(square, 150, 200, 400, 400);
        var reversed = PriceFibonacciArcGeometry.Project(arc with { First = arc.Second, Second = arc.First }, viewport, 400, 400);
        var reverseHalf = reversed.Levels.Single(level => level.Ratio == .5);
        Assert.Equal(square.RadiusDips, reverseHalf.RadiusDips);
        AssertCircle(reverseHalf, 250, 100, 400, 400);
        Assert.Equal(2, reversed.First!.Value.OriginalIndex);
        Assert.Equal(1, reversed.Second!.Value.OriginalIndex);
        Assert.Equal(arc.Second.Hit, reversed.First!.Value.Hit);
    }

    [Fact]
    public void IrregularTimestampsDoNotChangeOrdinalGeometry()
    {
        var regular = Source([Bar(0, 10), Bar(1, 20)]);
        var irregular = Source([Bar(0, 10), new PriceBar(1, Epoch.AddDays(365), 20, 20, 20, 20)]);
        PriceFibonacciArcProjection Project(PriceChartSource source)
        {
            var raw = source.Query();
            return PriceFibonacciArcGeometry.Project(new(1, Capture(raw, 0), Capture(raw, 1)), raw, 200, 100);
        }
        var a = Project(regular);
        var b = Project(irregular);
        Assert.Equal(a.Levels.Select(level => level.RadiusDips), b.Levels.Select(level => level.RadiusDips));
        Assert.Equal(a.Levels.SelectMany(level => level.Segments), b.Levels.SelectMany(level => level.Segments));
    }

    [Fact]
    public void ClippingUsesOffscreenOriginalCenterAndEveryChordStaysInsideBothAxes()
    {
        var source = Source([Bar(0, 50), Bar(1, 50), Bar(2, 50), Bar(3, 50), Bar(4, 50)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 0), Capture(raw, 4)) { ShowLevel100 = true };
        var viewport = source.Query(new(2, 1, fixedDomain: new(0, 100)));
        var result = PriceFibonacciArcGeometry.Project(arc, viewport, 100, 100);
        // Center X is -150 DIP, not clamped to 0; 50% radius is 200 DIP.
        var half = result.Levels.Single(level => level.Ratio == .5);
        Assert.Equal(200, half.RadiusDips);
        Assert.Equal(PriceFibonacciArcLevelStatus.Visible, half.Status);
        Assert.All(result.Levels.SelectMany(level => level.Segments), AssertFiniteUnit);
        Assert.Contains(half.Segments, segment => segment.Start.Y == 0 || segment.End.Y == 0);
        Assert.Contains(half.Segments, segment => segment.Start.Y == 1 || segment.End.Y == 1);
        foreach (var point in half.Segments.SelectMany(segment => new[] { segment.Start, segment.End }))
        {
            // Clipped chord endpoints are within the sagitta tolerance, not exactly on the circle.
            var dx = point.X * 100 + 150;
            var dy = point.Y * 100 - 50;
            Assert.InRange(200 - Math.Sqrt(dx * dx + dy * dy), -1e-10, PriceFibonacciArcGeometry.MaximumChordErrorDips);
        }
        Assert.Equal(PriceFibonacciArcLevelStatus.OutsideViewport, result.Levels.Single(level => level.Ratio == 1).Status);
        Assert.Equal(arc.First.Hit, result.First!.Value.Hit);
    }

    [Fact]
    public void SegmentBudgetRefusesRatherThanCoarsensAndRetainsEnabledLevelMetadata()
    {
        var source = Source([Bar(0, 50), Bar(1, 50)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 0), Capture(raw, 1))
            { ShowLevel23_6 = true, ShowLevel76_4 = true, ShowLevel100 = true };
        var result = PriceFibonacciArcGeometry.Project(arc, source.Query(new(fixedDomain: new(0, 100))), 10_000_000, 100);
        Assert.Equal(PriceTrendProjectionStatus.GeometryBudgetExceeded, result.Status);
        Assert.Equal(PriceFibonacciArcGeometryIssue.SegmentBudgetExceeded, result.GeometryIssue);
        Assert.Equal(6, result.Levels.Length);
        Assert.All(result.Levels, level =>
        {
            Assert.Equal(PriceFibonacciArcLevelStatus.GeometryBudgetExceeded, level.Status);
            Assert.True(level.RadiusDips > 0);
            Assert.Empty(level.Segments);
        });
        Assert.Same(arc, result.Arc);
        Assert.Equal(arc.First.Hit, result.First!.Value.Hit);
    }

    [Fact]
    public void VisibleSmallLevelsSurviveLargerLevelBudgetRefusal()
    {
        var source = Source([Bar(0, 50), Bar(1, 50)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 0), Capture(raw, 1)) { ShowLevel23_6 = true, ShowLevel100 = true };
        var result = PriceFibonacciArcGeometry.Project(arc, source.Query(new(fixedDomain: new(0, 100))), 500_000, 100);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.Equal(PriceFibonacciArcLevelStatus.Visible, result.Levels.Single(level => level.Ratio == .236).Status);
        Assert.Equal(59_000, result.Levels.Single(level => level.Ratio == .236).RadiusDips);
        Assert.Equal(PriceFibonacciArcLevelStatus.GeometryBudgetExceeded, result.Levels.Single(level => level.Ratio == 1).Status);
        Assert.All(result.Levels, level => Assert.InRange(level.Segments.Length, 0, PriceFibonacciArcGeometry.MaximumSegmentsPerLevel));
        Assert.All(result.Levels.SelectMany(level => level.Segments), AssertFiniteUnit);
    }

    [Fact]
    public void SinglePointTangencyProducesNoFakeLineButAdjacentLargerCircleRemainsVisible()
    {
        var source = Source([Bar(0, 50), Bar(1, 50), Bar(2, 50)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 0), Capture(raw, 1));
        var viewport = source.Query(new(1, 1, fixedDomain: new(0, 100)));
        var result = PriceFibonacciArcGeometry.Project(arc, viewport, 100, 100);
        var half = result.Levels.Single(level => level.Ratio == .5);
        Assert.Equal(50, half.RadiusDips);
        Assert.Equal(PriceFibonacciArcLevelStatus.OutsideViewport, half.Status);
        Assert.Empty(half.Segments);
        Assert.Equal(PriceFibonacciArcLevelStatus.Visible, result.Levels.Single(level => level.Ratio == .618).Status);
    }

    [Fact]
    public void InvalidLayoutAndUnrepresentableMappingNeverClampOrReturnNonfiniteGeometry()
    {
        var source = Source([Bar(0, 2), Bar(1, 8)]);
        var raw = source.Query();
        var arc = new PriceFibonacciArc(1, Capture(raw, 0), Capture(raw, 1));
        foreach (var size in new[] { 0, -1, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            foreach (var pair in new[] { (Width: size, Height: 100d), (Width: 100d, Height: size) })
            {
                var result = PriceFibonacciArcGeometry.Project(arc, raw, pair.Width, pair.Height);
                AssertSuppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded, result);
                Assert.Equal(PriceFibonacciArcGeometryIssue.InvalidPlotSize, result.GeometryIssue);
                Assert.Same(arc, result.Arc);
                Assert.Equal(arc.First.Hit, result.First!.Value.Hit);
            }
        }
        var tooNarrow = source.Query(new(fixedDomain: new(0, double.Epsilon)));
        var overflow = PriceFibonacciArcGeometry.Project(arc, tooNarrow, 100, 100);
        AssertSuppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded, overflow);
        Assert.Equal(PriceFibonacciArcGeometryIssue.UnrepresentableAnchor, overflow.GeometryIssue);
        var underflow = PriceFibonacciArcGeometry.Project(arc, raw, double.Epsilon, double.Epsilon);
        AssertSuppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded, underflow);
        Assert.Equal(PriceFibonacciArcGeometryIssue.UnrepresentableAnchor, underflow.GeometryIssue);
        var flatOutside = PriceFibonacciArcGeometry.Project(arc, source.Query(new(fixedDomain: new(5, 5))), 100, 100);
        AssertSuppressed(PriceTrendProjectionStatus.GeometryBudgetExceeded, flatOutside);
        Assert.Equal(PriceFibonacciArcGeometryIssue.UnrepresentableAnchor, flatOutside.GeometryIssue);
    }

    [Fact]
    public void OppositeExtremeAndSubnormalPricesUseExactMappingBeforeDipDistance()
    {
        foreach (var pair in new[] { (First: double.MaxValue, Second: -double.MaxValue),
            (First: 4 * double.Epsilon, Second: 0d) })
        {
            var source = Source([Bar(0, pair.First), Bar(1, pair.Second)]);
            var raw = source.Query();
            var arc = new PriceFibonacciArc(1, Capture(raw, 0), Capture(raw, 1));
            var result = PriceFibonacciArcGeometry.Project(arc, raw, 200, 100);
            Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
            Assert.Equal(Math.Sqrt(100 * 100 + 100 * 100) / 2, result.Levels[1].RadiusDips!.Value, 12);
            Assert.All(result.Levels.SelectMany(level => level.Segments), AssertFiniteUnit);
        }
        var constant = Source([Bar(0, 7), Bar(1, 7)]);
        var snapshot = constant.Query();
        var flat = new PriceFibonacciArc(1, Capture(snapshot, 0), Capture(snapshot, 1));
        var half = PriceFibonacciArcGeometry.Project(flat, snapshot, 200, 200).Levels[1];
        Assert.Equal(50, half.RadiusDips);
        AssertCircle(half, 50, 100, 200, 200);
    }

    private static void AssertCircle(PriceFibonacciArcLevelProjection level, double centerX, double centerY,
        double width, double height)
    {
        Assert.Equal(PriceFibonacciArcLevelStatus.Visible, level.Status);
        Assert.InRange(level.Segments.Length, 8, PriceFibonacciArcGeometry.MaximumSegmentsPerLevel);
        var radius = level.RadiusDips!.Value;
        var points = level.Segments.SelectMany(segment => new[] { segment.Start, segment.End }).ToArray();
        foreach (var cardinal in new[] { (centerX + radius, centerY), (centerX, centerY + radius),
            (centerX - radius, centerY), (centerX, centerY - radius) })
            Assert.Contains(points, point => Math.Abs(point.X * width - cardinal.Item1) < 1e-9 &&
                Math.Abs(point.Y * height - cardinal.Item2) < 1e-9);
        Assert.Equal(level.Segments[0].Start, level.Segments[^1].End);
        foreach (var segment in level.Segments)
        {
            AssertFiniteUnit(segment);
            foreach (var point in new[] { segment.Start, segment.End })
            {
                var dx = point.X * width - centerX;
                var dy = point.Y * height - centerY;
                Assert.Equal(radius, Math.Sqrt(dx * dx + dy * dy), 9);
            }
            var mx = (segment.Start.X + segment.End.X) * width / 2 - centerX;
            var my = (segment.Start.Y + segment.End.Y) * height / 2 - centerY;
            var error = radius - Math.Sqrt(mx * mx + my * my);
            Assert.InRange(error, 0, PriceFibonacciArcGeometry.MaximumChordErrorDips);
        }
    }

    private static PriceChartSource Source(PriceBar[] bars, long datasetVersion = 7, long sourceVersion = 0,
        string seriesId = "raw-prices") => new(seriesId, "Original prices", bars, datasetVersion, sourceVersion);

    private static PriceBar Bar(int index, double value) => new(index, Epoch.AddMinutes(index), value, value, value, value);

    private static PriceTrendAnchor Capture(PriceChartSnapshot raw, int barIndex,
        PriceTrendValueLevel level = PriceTrendValueLevel.Close)
        => Assert.IsType<PriceTrendAnchor>(PriceTrendGeometry.CreateAnchor(raw, barIndex, level));

    private static (PriceTrendAnchor First, PriceTrendAnchor Second) Anchors()
    {
        var raw = Source([Bar(0, 2), Bar(1, 8)]).Query();
        return (Capture(raw, 0), Capture(raw, 1));
    }

    private static PriceFibonacciArc Add(PriceFibonacciArcs store, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        var draft = Assert.IsType<PriceFibonacciArcDraft>(store.BeginNew());
        Assert.True(store.Commit(draft, first, second));
        return store.Items[^1];
    }

    private static void AssertSuppressed(PriceTrendProjectionStatus expected, PriceFibonacciArcProjection result)
    {
        Assert.Equal(expected, result.Status);
        Assert.Empty(result.Levels);
    }

    private static void AssertFiniteUnit(HistoryChartLine segment)
    {
        foreach (var coordinate in new[] { segment.Start.X, segment.Start.Y, segment.End.X, segment.End.Y })
        {
            Assert.True(double.IsFinite(coordinate));
            Assert.InRange(coordinate, 0, 1);
        }
    }
}
