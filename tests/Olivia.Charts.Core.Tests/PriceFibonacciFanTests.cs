using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceFibonacciFanTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DraftOwnershipIsExactAndStaleRefusalPrecedesPayloadValidation()
    {
        var (first, second) = Anchors();
        var fans = new PriceFibonacciFans();
        var stale = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginNew());
        var current = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginNew());
        var foreign = Assert.IsType<PriceFibonacciFanDraft>(new PriceFibonacciFans().BeginNew());
        foreach (var rejected in new[] { stale, current with { }, foreign,
            new PriceFibonacciFanDraft(current.Revision, null, null), null! })
        {
            Assert.False(fans.Commit(rejected, default, default));
            Assert.False(fans.Cancel(rejected));
            Assert.Same(current, fans.Draft);
            Assert.Equal(2, fans.Revision);
        }
        Assert.True(fans.Commit(current, first, second));
        Assert.False(fans.Commit(current, first, second));
        Assert.Null(fans.Draft);
        Assert.Equal(1, Assert.Single(fans.Items).Id);
    }

    [Fact]
    public void InvalidCurrentAnchorsRetainDraftSelectionAndCommittedSnapshot()
    {
        var (first, second) = Anchors();
        var fans = new PriceFibonacciFans();
        var original = Add(fans, first, second);
        var snapshot = fans.Items;
        var draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginEdit(original.Id));
        var revision = fans.Revision;
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
            Assert.ThrowsAny<ArgumentException>(() => fans.Commit(draft, first, invalid));
            Assert.Equal(revision, fans.Revision);
            Assert.Equal(snapshot, fans.Items);
            Assert.Same(draft, fans.Draft);
            Assert.Equal(original.Id, fans.SelectedId);
        }
        Assert.True(fans.Commit(draft, second, first));
        Assert.Equal(new PriceFibonacciFan(original.Id, second, first), Assert.Single(fans.Items));
        Assert.Same(original, Assert.Single(snapshot));
    }

    [Fact]
    public void CapacityAndConcurrentCommitAreBoundedWithoutTouchingOtherAnalysisStores()
    {
        var (first, second) = Anchors();
        var fans = new PriceFibonacciFans();
        var trends = new PriceTrendLines();
        var retracements = new PriceFibonacciRetracements();
        Assert.True(trends.Commit(Assert.IsType<PriceTrendLineDraft>(trends.BeginNew()), first, second));
        Assert.True(retracements.Commit(Assert.IsType<PriceFibonacciRetracementDraft>(retracements.BeginNew()), first, second));
        var trendSnapshot = trends.Items;
        var retracementSnapshot = retracements.Items;
        var draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginNew());
        var successes = 0;
        Parallel.For(0, 32, _ => { if (fans.Commit(draft, first, second)) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
        while (fans.Items.Length < PriceFibonacciFans.MaximumFans) Add(fans, first, second);
        Assert.Equal(32, fans.Items.Length);
        draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginEdit(1));
        var revision = fans.Revision;
        Assert.Null(fans.BeginNew(true, true, true));
        Assert.Same(draft, fans.Draft);
        Assert.Equal(revision, fans.Revision);
        Assert.True(fans.Commit(draft, second, first));
        Assert.True(fans.RemoveSelected());
        Assert.Equal(33, Add(fans, first, second).Id);
        fans.Clear();
        Assert.Equal(trendSnapshot, trends.Items);
        Assert.Equal(retracementSnapshot, retracements.Items);
        Assert.Equal(34, Add(fans, first, second).Id);
    }

    [Fact]
    public void SelectionCancellationRemovalAndNoOpsPreserveSnapshotsAndInvalidateDrafts()
    {
        var (first, second) = Anchors();
        var fans = new PriceFibonacciFans();
        Assert.False(fans.ClearSelection());
        Assert.False(fans.RemoveSelected());
        fans.Clear();
        Assert.Equal(0, fans.Revision);
        var original = Add(fans, first, second);
        var snapshot = fans.Items;
        var draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginEdit(original.Id));
        Assert.True(fans.Cancel(draft));
        Assert.Equal(original.Id, fans.SelectedId);
        var revision = fans.Revision;
        Assert.True(fans.Select(original.Id));
        Assert.Null(fans.BeginEdit(999));
        Assert.False(fans.Select(999));
        Assert.Equal(revision, fans.Revision);
        draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginNew());
        Assert.Null(fans.SelectedId);
        Assert.True(fans.Select(original.Id));
        Assert.False(fans.Commit(draft, first, second));
        draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginEdit(original.Id));
        Assert.True(fans.ClearSelection());
        Assert.False(fans.Cancel(draft));
        Assert.Equal(snapshot, fans.Items);
        draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginNew());
        Assert.False(fans.RemoveSelected());
        Assert.Null(fans.Draft);
        Assert.False(fans.Cancel(draft));
        Assert.True(fans.Select(original.Id));
        Assert.True(fans.RemoveSelected());
        Assert.Same(original, Assert.Single(snapshot));
        Assert.Empty(fans.Items);
        draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginNew());
        fans.Clear();
        Assert.False(fans.Commit(draft, first, second));
    }

    [Fact]
    public void OptionsRequireExactRetainedFanAndPreserveAuthorshipSelectionAndEditFlags()
    {
        var (first, second) = Anchors();
        var fans = new PriceFibonacciFans();
        var draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginNew(true, false, true));
        Assert.True(draft.ShowLevel0);
        Assert.False(draft.ShowLevel23_6);
        Assert.True(draft.ShowLevel76_4);
        Assert.True(fans.Commit(draft, first, second));
        var original = Assert.Single(fans.Items);
        var snapshot = fans.Items;
        var revision = fans.Revision;
        Assert.True(fans.SetOptions(original, true, false, true));
        Assert.Equal(revision, fans.Revision);
        Assert.Same(original, Assert.Single(fans.Items));
        foreach (var rejected in new[] { original with { }, Add(new PriceFibonacciFans(), first, second), null! })
            Assert.False(fans.SetOptions(rejected, false, true, false));
        draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginEdit(original.Id));
        Assert.Same(original, draft.Original);
        Assert.True(draft.ShowLevel0);
        Assert.False(draft.ShowLevel23_6);
        Assert.True(draft.ShowLevel76_4);
        Assert.False(fans.SetOptions(original, false, true, false));
        Assert.True(fans.Cancel(draft));
        Assert.Same(original, Assert.Single(fans.Items));
        draft = Assert.IsType<PriceFibonacciFanDraft>(fans.BeginEdit(original.Id));
        Assert.True(fans.Commit(draft, second, first));
        var edited = Assert.Single(fans.Items);
        Assert.Equal(second, edited.First);
        Assert.Equal(first, edited.Second);
        Assert.True(edited.ShowLevel0);
        Assert.False(edited.ShowLevel23_6);
        Assert.True(edited.ShowLevel76_4);
        Assert.False(fans.SetOptions(original, false, true, false));
        var selected = Add(fans, first, second);
        revision = fans.Revision;
        var successes = 0;
        Parallel.For(0, 16, _ => { if (fans.SetOptions(edited, false, true, false)) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
        var changed = fans.Items[0];
        Assert.Equal(revision + 1, fans.Revision);
        Assert.False(changed.ShowLevel0);
        Assert.True(changed.ShowLevel23_6);
        Assert.False(changed.ShowLevel76_4);
        Assert.Equal(edited.Id, changed.Id);
        Assert.Equal(edited.First, changed.First);
        Assert.Equal(edited.Second, changed.Second);
        Assert.Equal(selected.Id, fans.SelectedId);
        Assert.Same(original, Assert.Single(snapshot));
        Assert.True(original.ShowLevel0);
        Assert.True(original.ShowLevel76_4);
    }

    [Fact]
    public void OriginalOhlcFieldsAndAggregateMappingNeverBecomeAggregateAnchors()
    {
        var bars = new[]
        {
            new PriceBar(0, Epoch, 6, 9, 3, 7), Bar(1, 100),
            new PriceBar(5, Epoch.AddMinutes(5), 8, 12, 4, 10), Bar(6, 200)
        };
        var source = Source(bars);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0, PriceTrendValueLevel.Open),
            Capture(raw, 2, PriceTrendValueLevel.High));
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var result = PriceFibonacciFanGeometry.Project(fan,
            aggregation.Source.Query(new(fixedDomain: new(0, 200))), aggregation);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.Equal(0, result.First!.Value.OriginalIndex);
        Assert.Equal(2, result.Second!.Value.OriginalIndex);
        Assert.Equal(0, result.First!.Value.DisplayIndex);
        Assert.Equal(1, result.Second!.Value.DisplayIndex);
        Assert.Equal(fan.First.Hit, result.First!.Value.Hit);
        Assert.Equal(fan.Second.Hit, result.Second!.Value.Hit);
        Assert.Equal(100, aggregation.Source.Bars[0].Close);
        Assert.Equal(200, aggregation.Source.Bars[1].High);
        Assert.Equal(6, fan.First.Value);
        Assert.Equal(12, fan.Second.Value);
        Assert.Equal(new[] { 9.708, 9, 8.292 }, result.Levels.Select(level => level.SecondPrice!.Value));
        Assert.Equal(new HistoryChartLine(new(.25, .97), new(1, .9475)),
            result.Levels.Single(level => level.Ratio == .5).Segment);
        AssertSuppressed(PriceTrendProjectionStatus.AggregationMismatch,
            PriceFibonacciFanGeometry.Project(fan, raw, aggregation));
        var sameValuesDifferentSource = Source(bars, sourceVersion: 1);
        var staleMapping = PriceTimeAggregator.Aggregate(sameValuesDifferentSource, PriceTimeInterval.Minute5);
        AssertSuppressed(PriceTrendProjectionStatus.AggregationMismatch,
            PriceFibonacciFanGeometry.Project(fan, aggregation.Source.Query(), staleMapping));
    }

    [Fact]
    public void SameAggregateSlotSuppressesAllLevelsWithoutMutatingAuthoredFan()
    {
        var source = Source([Bar(0, 2), Bar(1, 8)]);
        var raw = source.Query();
        var fans = new PriceFibonacciFans();
        var fan = Add(fans, Capture(raw, 0), Capture(raw, 1));
        var before = PriceFibonacciFanGeometry.Project(fan, raw);
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var result = PriceFibonacciFanGeometry.Project(fan, aggregation.Source.Query(), aggregation);
        AssertSuppressed(PriceTrendProjectionStatus.CollapsedAggregateSlot, result);
        Assert.Equal(0, result.First!.Value.DisplayIndex);
        Assert.Equal(0, result.Second!.Value.DisplayIndex);
        Assert.Equal(fan.First.Hit, result.First!.Value.Hit);
        Assert.Equal(fan.Second.Hit, result.Second!.Value.Hit);
        Assert.Same(fan, Assert.Single(fans.Items));
        Assert.Equal(before.Levels.ToArray(), PriceFibonacciFanGeometry.Project(fan, raw).Levels.ToArray());
    }

    [Fact]
    public void AdvancingSourceVersionResolvesShiftedOriginalAndAggregateIndexes()
    {
        var source = Source([Bar(0, 1), Bar(5, 2), Bar(6, 3), Bar(10, 4)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 1), Capture(raw, 3));
        var next = Source(source.Bars.Skip(1).ToArray(), sourceVersion: 9);
        var aggregation = PriceTimeAggregator.Aggregate(next, PriceTimeInterval.Minute5);
        var result = PriceFibonacciFanGeometry.Project(fan, aggregation.Source.Query(), aggregation);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.Equal(0, result.First!.Value.OriginalIndex);
        Assert.Equal(2, result.Second!.Value.OriginalIndex);
        Assert.Equal(0, result.First!.Value.DisplayIndex);
        Assert.Equal(1, result.Second!.Value.DisplayIndex);
        Assert.Equal(9, result.First!.Value.Hit.SourceVersion);
        Assert.Equal(0, fan.First.Hit.SourceVersion);
        Assert.Equal(2, fan.First.Value);
    }

    [Fact]
    public void ProvenanceChangesSuppressWithoutReplacingOriginalRecordOrSelectedValue()
    {
        var bars = new[] { new PriceBar(0, Epoch, 1, 10, 0, 2), new PriceBar(1, Epoch.AddMinutes(1), 7, 10, 0, 8) };
        var raw = Source(bars).Query();
        var fans = new PriceFibonacciFans();
        var fan = Add(fans, Capture(raw, 0), Capture(raw, 1));
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
            var result = PriceFibonacciFanGeometry.Project(fan, test.Source.Query());
            AssertSuppressed(test.Status, result);
            Assert.Same(fan, result.Fan);
            Assert.Same(fan, Assert.Single(fans.Items));
            Assert.Equal(2, fan.First.Value);
        }
        var otherFields = Source([new PriceBar(0, Epoch, 5, 20, -1, 2, 99), bars[1]], sourceVersion: 7);
        Assert.Equal(PriceTrendProjectionStatus.Visible, PriceFibonacciFanGeometry.Project(fan, otherFields.Query()).Status);
    }

    [Fact]
    public void EmptyNoDomainAndBudgetRefusalRetainResolvedProvenance()
    {
        var source = Source([Bar(0, 2), new PriceBar(1, Epoch.AddMinutes(1), null, null, null, null), Bar(2, 8)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 2));
        foreach (var (snapshot, status) in new[]
        {
            (source.Query(new(barCount: 0)), PriceTrendProjectionStatus.EmptyViewport),
            (source.Query(new(1, 1)), PriceTrendProjectionStatus.NoDomain),
            (source.Query(new(geometryBudget: 0)), PriceTrendProjectionStatus.GeometryBudgetExceeded)
        })
        {
            var result = PriceFibonacciFanGeometry.Project(fan, snapshot);
            AssertSuppressed(status, result);
            Assert.Equal(fan.First.Hit, result.First!.Value.Hit);
            Assert.Equal(fan.Second.Hit, result.Second!.Value.Hit);
        }
    }

    [Fact]
    public void DefaultThreeRaysUseFirstOriginAndSecondReferencePriceWithoutHundredOrExtras()
    {
        var source = Source([Bar(0, 110), Bar(1, 10)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1));
        var draft = Assert.IsType<PriceFibonacciFanDraft>(new PriceFibonacciFans().BeginNew());
        Assert.False(fan.ShowLevel0);
        Assert.False(fan.ShowLevel23_6);
        Assert.False(fan.ShowLevel76_4);
        Assert.False(draft.ShowLevel0);
        Assert.False(draft.ShowLevel23_6);
        Assert.False(draft.ShowLevel76_4);
        var result = PriceFibonacciFanGeometry.Project(fan, source.Query(new(fixedDomain: new(0, 120))));
        Assert.Equal(new[] { .382, .5, .618 }, result.Levels.Select(level => level.Ratio));
        Assert.Equal(new[] { 48.2, 60, 71.8 }, result.Levels.Select(level => level.SecondPrice!.Value));
        Assert.Same(fan, result.Fan);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        // The right viewport edge is 1.5 authored-X spans after the first anchor.
        var expectedEndY = new[] { 102.7 / 120, 85d / 120, 67.3 / 120 };
        for (var i = 0; i < result.Levels.Length; i++)
        {
            var level = result.Levels[i];
            Assert.Equal(PriceFibonacciFanLevelStatus.Visible, level.Status);
            var segment = Assert.IsType<HistoryChartLine>(level.Segment);
            Assert.Equal(.25, segment.Start.X);
            Assert.Equal(1d / 12, segment.Start.Y);
            Assert.Equal(1, segment.End.X);
            Assert.Equal(expectedEndY[i], segment.End.Y, 14);
            AssertFiniteUnit(segment);
        }
    }

    [Theory]
    [InlineData(false, false, false, 3)]
    [InlineData(true, false, false, 4)]
    [InlineData(false, true, false, 4)]
    [InlineData(false, false, true, 4)]
    [InlineData(true, true, true, 6)]
    public void OptionalFlagsAddOnlyZero236And764(bool zero, bool early, bool late, int count)
    {
        var source = Source([Bar(0, 110), Bar(1, 10)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1))
            { ShowLevel0 = zero, ShowLevel23_6 = early, ShowLevel76_4 = late };
        var result = PriceFibonacciFanGeometry.Project(fan, source.Query(new(fixedDomain: new(-100, 200))));
        Assert.Equal(count, result.Levels.Length);
        Assert.Equal(zero, result.Levels.Any(level => level.Ratio == 0));
        Assert.Equal(early, result.Levels.Any(level => level.Ratio == .236));
        Assert.Equal(late, result.Levels.Any(level => level.Ratio == .764));
        Assert.DoesNotContain(result.Levels, level => level.Ratio >= 1);
        foreach (var (ratio, price) in new[] { (0d, 10d), (.236, 33.6), (.764, 86.4) })
        {
            var level = result.Levels.SingleOrDefault(level => level.Ratio == ratio);
            if (level is not null) Assert.Equal(price, level.SecondPrice);
        }
    }

    [Fact]
    public void ReversedAuthorshipMovesOriginAndExtendsLeftInsteadOfSortingOrUsingPositiveX()
    {
        var source = Source([Bar(0, 20), Bar(1, 40), Bar(2, 60), Bar(3, 80), Bar(4, 100)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 1), Capture(raw, 3));
        var viewport = source.Query(new(fixedDomain: new(0, 100)));
        var forward = PriceFibonacciFanGeometry.Project(fan, viewport).Levels.Single(level => level.Ratio == .5);
        var reverse = PriceFibonacciFanGeometry.Project(fan with { First = fan.Second, Second = fan.First }, viewport);
        var reversed = reverse.Levels.Single(level => level.Ratio == .5);
        Assert.Equal(60, forward.SecondPrice);
        Assert.Equal(60, reversed.SecondPrice);
        Assert.Equal(new HistoryChartLine(new(.3, .6), new(1, .25)), forward.Segment);
        Assert.Equal(new HistoryChartLine(new(.7, .2), new(0, .55)), reversed.Segment);
        Assert.Equal(3, reverse.First!.Value.OriginalIndex);
        Assert.Equal(1, reverse.Second!.Value.OriginalIndex);
        Assert.Equal(fan.Second.Hit, reverse.First!.Value.Hit);
        Assert.Equal(fan.First.Hit, reverse.Second!.Value.Hit);
        Assert.All(reverse.Levels, level => Assert.True(level.Segment!.Value.Start.X > level.Segment.Value.End.X));
    }

    [Fact]
    public void RaysCrossViewportWithOriginOutsideButNeverExtendBehindOrigin()
    {
        var source = Source([Bar(0, 10), Bar(1, 20), Bar(2, 30), Bar(3, 40), Bar(4, 50), Bar(5, 60)]);
        var raw = source.Query();
        var forward = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1));
        var clipped = PriceFibonacciFanGeometry.Project(forward, source.Query(new(3, 2, fixedDomain: new(0, 50))));
        Assert.Equal(new HistoryChartLine(new(0, .55), new(1, .35)), clipped.Levels.Single(level => level.Ratio == .5).Segment);
        var reversed = new PriceFibonacciFan(2, Capture(raw, 5), Capture(raw, 4));
        clipped = PriceFibonacciFanGeometry.Project(reversed, source.Query(new(1, 2, fixedDomain: new(0, 100))));
        Assert.Equal(new HistoryChartLine(new(1, .525), new(0, .625)), clipped.Levels.Single(level => level.Ratio == .5).Segment);
        var awayRight = new PriceFibonacciFan(3, Capture(raw, 3), Capture(raw, 4));
        var awayLeft = new PriceFibonacciFan(4, Capture(raw, 2), Capture(raw, 1));
        foreach (var result in new[]
        {
            PriceFibonacciFanGeometry.Project(awayRight, source.Query(new(0, 1, fixedDomain: new(0, 100)))),
            PriceFibonacciFanGeometry.Project(awayLeft, source.Query(new(4, 1, fixedDomain: new(0, 100))))
        })
        {
            Assert.Equal(PriceTrendProjectionStatus.OutsideViewport, result.Status);
            Assert.Equal(3, result.Levels.Length);
            Assert.All(result.Levels, level =>
            {
                Assert.Equal(PriceFibonacciFanLevelStatus.OutsideViewport, level.Status);
                Assert.Null(level.Segment);
                Assert.NotNull(level.SecondPrice);
            });
        }
    }

    [Fact]
    public void EachRayClipsInYIndependentlyEvenWhenZeroLineMissesTheViewport()
    {
        var source = Source([Bar(0, 0), Bar(1, 100), Bar(2, 100), Bar(3, 100), Bar(4, 100)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1)) { ShowLevel0 = true };
        var result = PriceFibonacciFanGeometry.Project(fan, source.Query(new(2, 1, fixedDomain: new(60, 100))));
        var zero = result.Levels.Single(level => level.Ratio == 0);
        Assert.Equal(PriceFibonacciFanLevelStatus.OutsideViewport, zero.Status);
        Assert.Null(zero.Segment);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        var half = result.Levels.Single(level => level.Ratio == .5);
        Assert.Equal(50, half.SecondPrice); // Reference point is below this viewport but the ray enters it.
        Assert.Equal(new HistoryChartLine(new(0, .625), new(.5, 0)), half.Segment);
        Assert.All(result.Levels.Where(level => level.Segment.HasValue), level => AssertFiniteUnit(level.Segment!.Value));
    }

    [Fact]
    public void OppositeExtremePricesAvoidOverflowAndClipBeforeBinary64Conversion()
    {
        var source = Source([Bar(0, double.MaxValue), Bar(1, -double.MaxValue)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1))
            { ShowLevel0 = true, ShowLevel23_6 = true, ShowLevel76_4 = true };
        var result = PriceFibonacciFanGeometry.Project(fan, raw);
        Assert.Equal(6, result.Levels.Length);
        Assert.Equal(-double.MaxValue, result.Levels[0].SecondPrice);
        Assert.Equal(0, result.Levels.Single(level => level.Ratio == .5).SecondPrice);
        Assert.Equal(new HistoryChartLine(new(.25, 0), new(.75, 1)), result.Levels[0].Segment);
        Assert.Equal(new HistoryChartLine(new(.25, 0), new(1, .75)), result.Levels.Single(level => level.Ratio == .5).Segment);
        foreach (var level in result.Levels)
        {
            Assert.Equal(PriceFibonacciFanLevelStatus.Visible, level.Status);
            Assert.True(double.IsFinite(level.SecondPrice!.Value));
            AssertFiniteUnit(Assert.IsType<HistoryChartLine>(level.Segment));
        }
        var reversed = PriceFibonacciFanGeometry.Project(fan with { First = fan.Second, Second = fan.First }, raw);
        Assert.Equal(new HistoryChartLine(new(.75, 1), new(.25, 0)), reversed.Levels[0].Segment);
        Assert.Equal(new HistoryChartLine(new(.75, 1), new(0, .25)), reversed.Levels.Single(level => level.Ratio == .5).Segment);
    }

    [Fact]
    public void SubnormalReferenceRoundingNeverFlattensExactFanGeometry()
    {
        var cases = new[]
        {
            (First: double.Epsilon, Second: 0d, Price: 0d, Start: .75, End: .9375),
            (First: 2 * double.Epsilon, Second: double.Epsilon, Price: 2 * double.Epsilon, Start: .5, End: .6875),
            (First: -double.Epsilon, Second: 0d, Price: -0d, Start: .75, End: .5625)
        };
        foreach (var test in cases)
        {
            var source = Source([Bar(0, test.First), Bar(1, test.Second)]);
            var raw = source.Query();
            var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1));
            var domain = test.First < 0 ? new HistoryValueDomain(-2 * double.Epsilon, 2 * double.Epsilon) :
                new HistoryValueDomain(0, 4 * double.Epsilon);
            var half = PriceFibonacciFanGeometry.Project(fan, source.Query(new(fixedDomain: domain)))
                .Levels.Single(level => level.Ratio == .5);
            Assert.Equal(BitConverter.DoubleToInt64Bits(test.Price), BitConverter.DoubleToInt64Bits(half.SecondPrice!.Value));
            var segment = Assert.IsType<HistoryChartLine>(half.Segment);
            Assert.Equal(test.Start, segment.Start.Y);
            Assert.Equal(test.End, segment.End.Y);
            Assert.Equal(1, segment.End.X);
            AssertFiniteUnit(segment);
        }
    }

    [Fact]
    public void ConstantDomainKeepsCoincidentHorizontalRaysButSuppressesSinglePointCrossings()
    {
        var source = Source([Bar(0, 7), Bar(1, 7)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1))
            { ShowLevel0 = true, ShowLevel23_6 = true, ShowLevel76_4 = true };
        var result = PriceFibonacciFanGeometry.Project(fan, raw);
        Assert.Equal(6, result.Levels.Length);
        Assert.All(result.Levels, level =>
        {
            Assert.Equal(7, level.SecondPrice);
            Assert.Equal(new HistoryChartLine(new(.25, .5), new(1, .5)), level.Segment);
        });
        var sloped = Source([Bar(0, 0), Bar(1, 10)]);
        raw = sloped.Query();
        fan = new(1, Capture(raw, 0), Capture(raw, 1));
        result = PriceFibonacciFanGeometry.Project(fan, sloped.Query(new(fixedDomain: new(5, 5))));
        Assert.Equal(PriceTrendProjectionStatus.OutsideViewport, result.Status);
        Assert.All(result.Levels, level => Assert.Null(level.Segment));
    }

    [Fact]
    public void LevelZeroRetainsSecondSignedZeroBitsAndSelectedBitChangesSuppressFan()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var source = Source([Bar(0, 1), new PriceBar(1, Epoch.AddMinutes(1), 0, 0, negativeZero, negativeZero)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1)) { ShowLevel0 = true };
        var zero = PriceFibonacciFanGeometry.Project(fan, raw).Levels.Single(level => level.Ratio == 0);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(zero.SecondPrice!.Value));
        var changed = Source([Bar(0, 1), Bar(1, 0)], sourceVersion: 1);
        var result = PriceFibonacciFanGeometry.Project(fan, changed.Query());
        AssertSuppressed(PriceTrendProjectionStatus.ChangedAnchorValue, result);
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(result.Second!.Value.Hit.Bar.Close!.Value));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(fan.Second.Value));
    }

    [Fact]
    public void ExactBoundaryIntersectionSurvivesRoundedReferenceAndIrregularTimestampSpacing()
    {
        var source = Source([Bar(0, 110), new PriceBar(1, Epoch.AddDays(100), 10, 10, 10, 10)]);
        var raw = source.Query();
        var fan = new PriceFibonacciFan(1, Capture(raw, 0), Capture(raw, 1));
        var result = PriceFibonacciFanGeometry.Project(fan, source.Query(new(fixedDomain: new(17, 17.3))));
        var level = result.Levels.Single(level => level.Ratio == .382);
        Assert.Equal(48.2, level.SecondPrice);
        // The exact ordinal ray reaches 173/10 at X=2. Binary64 17.3 is greater by
        // 1/1407374883553280. Rounding Q first makes the endpoint too high and loses
        // this real intersection; timestamp distance must not replace ordinal X.
        Assert.Equal(PriceFibonacciFanLevelStatus.Visible, level.Status);
        var segment = Assert.IsType<HistoryChartLine>(level.Segment);
        Assert.Equal(0, segment.Start.Y);
        Assert.Equal(1, segment.End.X);
        Assert.Equal(1d / 422212465065985, segment.End.Y);
        AssertFiniteUnit(segment);
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

    private static PriceFibonacciFan Add(PriceFibonacciFans store, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        var draft = Assert.IsType<PriceFibonacciFanDraft>(store.BeginNew());
        Assert.True(store.Commit(draft, first, second));
        return store.Items[^1];
    }

    private static void AssertSuppressed(PriceTrendProjectionStatus expected, PriceFibonacciFanProjection result)
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
