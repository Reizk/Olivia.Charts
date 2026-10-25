using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceFibonacciRetracementTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyExactCurrentDraftCanCommitAndStaleRejectionPrecedesValidation()
    {
        var (first, second) = Anchors();
        var store = new PriceFibonacciRetracements();
        var stale = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginNew());
        var draft = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginNew());
        var foreign = Assert.IsType<PriceFibonacciRetracementDraft>(new PriceFibonacciRetracements().BeginNew());
        foreach (var rejected in new[] { stale, draft with { }, foreign, new PriceFibonacciRetracementDraft(draft.Revision, null, null) })
        {
            Assert.False(store.Commit(rejected, default, default));
            Assert.False(store.Cancel(rejected));
        }
        Assert.False(store.Commit(null!, default, default));
        Assert.False(store.Cancel(null!));
        Assert.Equal(2, store.Revision);
        Assert.Same(draft, store.Draft);
        Assert.Empty(store.Items);
        Assert.True(store.Commit(draft, first, second));
        Assert.False(store.Commit(draft, first, second));
        Assert.Equal(1, Assert.Single(store.Items).Id);
    }

    [Fact]
    public void InvalidEndpointsNeverPartiallyReplaceAnEditOrConsumeItsToken()
    {
        var (first, second) = Anchors();
        var store = new PriceFibonacciRetracements();
        var original = Add(store, first, second);
        var old = store.Items;
        var draft = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginEdit(original.Id));
        Assert.Same(original, draft.Original);
        var revision = store.Revision;
        var sameInstant = second with { Hit = second.Hit with { Bar = new PriceBar(50,
            first.Hit.Timestamp.ToOffset(TimeSpan.FromHours(3)), 8, 8, 8, 8) } };
        foreach (var invalid in new[] { default(PriceTrendAnchor), first,
            second with { Level = (PriceTrendValueLevel)(-1) },
            second with { Hit = second.Hit with { SeriesId = "another" } },
            second with { Hit = second.Hit with { DatasetVersion = 99 } },
            second with { Hit = second.Hit with { SourceVersion = -1 } },
            second with { Hit = second.Hit with { Bar = new PriceBar(first.Hit.Key, second.Hit.Timestamp, 8, 8, 8, 8) } },
            second with { Hit = second.Hit with { Bar = new PriceBar(50, second.Hit.Timestamp, null, null, null, null) } },
            sameInstant })
        {
            Assert.ThrowsAny<ArgumentException>(() => store.Commit(draft, first, invalid));
            Assert.Equal(revision, store.Revision);
            Assert.Same(draft, store.Draft);
            Assert.Equal(old, store.Items);
            Assert.Equal(original.Id, store.SelectedId);
        }
        Assert.True(store.Commit(draft, second, first));
        Assert.Equal(new PriceFibonacciRetracement(original.Id, second, first), Assert.Single(store.Items));
        Assert.Same(original, Assert.Single(old));
    }

    [Fact]
    public void CancellationSelectionRemovalAndClearKeepImmutableSnapshotsAndMonotonicIds()
    {
        var (first, second) = Anchors();
        var store = new PriceFibonacciRetracements();
        var original = Add(store, first, second);
        var old = store.Items;
        var edit = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginEdit(original.Id));
        Assert.True(store.Cancel(edit));
        Assert.Equal(original.Id, store.SelectedId);
        Assert.Equal(old, store.Items);
        var fresh = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginNew());
        Assert.Null(store.SelectedId);
        Assert.True(store.Select(original.Id));
        Assert.False(store.Commit(fresh, first, second));
        edit = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginEdit(original.Id));
        var revision = store.Revision;
        Assert.Null(store.BeginEdit(99));
        Assert.False(store.Select(99));
        Assert.Equal(revision, store.Revision);
        Assert.True(store.ClearSelection());
        Assert.False(store.Cancel(edit));
        Assert.Equal(old, store.Items);
        Assert.True(store.Select(original.Id));
        Assert.True(store.RemoveSelected());
        Assert.Same(original, Assert.Single(old));
        Assert.Empty(store.Items);
        var next = Add(store, first, second);
        Assert.Equal(original.Id + 1, next.Id);
        edit = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginEdit(next.Id));
        store.Clear();
        Assert.False(store.Commit(edit, first, second));
        Assert.Equal(next.Id + 1, Add(store, first, second).Id);
        fresh = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginNew());
        Assert.False(store.RemoveSelected());
        Assert.Null(store.Draft);
        Assert.False(store.Cancel(fresh));
        Assert.Single(store.Items);
    }

    [Fact]
    public void CapacityRefusalPreservesActiveEditAndConcurrentCommitConsumesOneToken()
    {
        var (first, second) = Anchors();
        var store = new PriceFibonacciRetracements();
        var draft = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginNew());
        var successes = 0;
        Parallel.For(0, 32, _ => { if (store.Commit(draft, first, second)) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
        Assert.Single(store.Items);
        while (store.Items.Length < PriceFibonacciRetracements.MaximumRetracements) Add(store, first, second);
        Assert.Equal(32, store.Items.Length);
        draft = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginEdit(1));
        var revision = store.Revision;
        Assert.Null(store.BeginNew());
        Assert.Same(draft, store.Draft);
        Assert.Equal(revision, store.Revision);
        Assert.True(store.Commit(draft, second, first));
        Assert.True(store.RemoveSelected());
        Assert.Equal(33, Add(store, first, second).Id);
    }

    [Fact]
    public void OptionsReplaceWholeSetUsingExactRetainedObjectAndPreserveEditsAndCancellation()
    {
        var (first, second) = Anchors();
        var store = new PriceFibonacciRetracements();
        var draft = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginNew(true, false, true));
        Assert.True(draft.ShowLevel23_6);
        Assert.False(draft.ShowLevel76_4);
        Assert.True(draft.ShowAdditionalLevels);
        Assert.True(store.Commit(draft, first, second));
        var original = Assert.Single(store.Items);
        var old = store.Items;
        var revision = store.Revision;
        Assert.True(store.SetOptions(original, true, false, true));
        Assert.Equal(revision, store.Revision);
        Assert.Same(original, Assert.Single(store.Items));
        Assert.False(store.SetOptions(null!, false, true, false));
        Assert.False(store.SetOptions(original with { }, false, true, false));
        var foreign = Add(new PriceFibonacciRetracements(), first, second);
        Assert.False(store.SetOptions(foreign, true, true, true));

        draft = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginEdit(original.Id));
        revision = store.Revision;
        Assert.False(store.SetOptions(original, false, true, false));
        Assert.Equal(revision, store.Revision);
        Assert.Same(draft, store.Draft);
        Assert.True(draft.ShowLevel23_6);
        Assert.False(draft.ShowLevel76_4);
        Assert.True(draft.ShowAdditionalLevels);
        Assert.True(store.Cancel(draft));
        Assert.Same(original, Assert.Single(store.Items));
        draft = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginEdit(original.Id));
        Assert.True(store.Commit(draft, second, first));
        var edited = Assert.Single(store.Items);
        Assert.Equal(second, edited.First);
        Assert.Equal(first, edited.Second);
        Assert.True(edited.ShowLevel23_6);
        Assert.False(edited.ShowLevel76_4);
        Assert.True(edited.ShowAdditionalLevels);
        Assert.False(store.SetOptions(original, false, true, false));

        var selected = Add(store, first, second);
        revision = store.Revision;
        var successes = 0;
        Parallel.For(0, 16, _ =>
        {
            if (store.SetOptions(edited, false, true, false)) Interlocked.Increment(ref successes);
        });
        Assert.Equal(1, successes);
        var changed = store.Items[0];
        Assert.Equal(revision + 1, store.Revision);
        Assert.False(changed.ShowLevel23_6);
        Assert.True(changed.ShowLevel76_4);
        Assert.False(changed.ShowAdditionalLevels);
        Assert.Equal(edited.Id, changed.Id);
        Assert.Equal(edited.First, changed.First);
        Assert.Equal(edited.Second, changed.Second);
        Assert.Equal(selected.Id, store.SelectedId);
        Assert.Same(original, Assert.Single(old));
        Assert.True(original.ShowLevel23_6);
        Assert.True(original.ShowAdditionalLevels);
        Assert.False(store.SetOptions(edited, true, false, true));
    }

    [Fact]
    public void DefaultFlagsAndFiveFixedRatiosUseFirstAs100AndSecondAsZero()
    {
        var source = Source([Bar(0, 110), Bar(1, 10)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1));
        Assert.False(set.ShowLevel23_6);
        Assert.False(set.ShowLevel76_4);
        Assert.False(set.ShowAdditionalLevels);
        var draft = Assert.IsType<PriceFibonacciRetracementDraft>(new PriceFibonacciRetracements().BeginNew());
        Assert.False(draft.ShowLevel23_6);
        Assert.False(draft.ShowLevel76_4);
        Assert.False(draft.ShowAdditionalLevels);
        var result = PriceFibonacciRetracementGeometry.Project(set, source.Query(new(fixedDomain: new(0, 120))));
        Assert.Equal(new[] { 0d, .382, .5, .618, 1 }, result.Levels.Select(level => level.Ratio));
        Assert.Equal(new[] { 10d, 48.2, 60, 71.8, 110 }, result.Levels.Select(level => level.Price!.Value));
        Assert.Same(set, result.Retracement);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.All(result.Levels, level =>
        {
            Assert.Equal(PriceFibonacciRetracementLevelStatus.Visible, level.Status);
            var segment = Assert.IsType<HistoryChartLine>(level.Segment);
            Assert.Equal(.25, segment.Start.X);
            Assert.Equal(.75, segment.End.X);
            Assert.Equal(segment.Start.Y, segment.End.Y);
            AssertFiniteUnit(segment);
        });
    }

    [Theory]
    [InlineData(true, false, false, 6)]
    [InlineData(false, true, false, 6)]
    [InlineData(false, false, true, 8)]
    [InlineData(true, true, true, 10)]
    public void OptionalFlagsIndependentlyAddExactlyTheirDocumentedRatios(bool early, bool late, bool additional, int count)
    {
        var source = Source([Bar(0, 110), Bar(1, 10)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1))
            { ShowLevel23_6 = early, ShowLevel76_4 = late, ShowAdditionalLevels = additional };
        var result = PriceFibonacciRetracementGeometry.Project(set, source.Query(new(fixedDomain: new(0, 500))));
        Assert.Equal(count, result.Levels.Length);
        Assert.Equal(early, result.Levels.Any(level => level.Ratio == .236));
        Assert.Equal(late, result.Levels.Any(level => level.Ratio == .764));
        foreach (var (ratio, price) in new[] { (.236, 33.6), (.764, 86.4), (1.618, 171.8), (2.618, 271.8), (4.236, 433.6) })
        {
            var level = result.Levels.SingleOrDefault(level => level.Ratio == ratio);
            if (ratio > 1) Assert.Equal(additional, level is not null);
            if (level is not null) Assert.Equal(price, level.Price);
        }
    }

    [Fact]
    public void ReversingAnchorsReversesPriceRolesAndXDirectionWithoutSortingChronologyOrPrice()
    {
        var source = Source([Bar(0, 110), new PriceBar(1, Epoch.AddDays(3), 10, 10, 10, 10)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 1), Capture(raw, 0))
            { ShowLevel23_6 = true, ShowLevel76_4 = true, ShowAdditionalLevels = true };
        var result = PriceFibonacciRetracementGeometry.Project(set, source.Query(new(fixedDomain: new(-400, 120))));
        Assert.Equal(new[] { 110d, 86.4, 71.8, 60, 48.2, 33.6, 10, -51.8, -151.8, -313.6 },
            result.Levels.Select(level => level.Price!.Value));
        Assert.All(result.Levels, level =>
        {
            var segment = Assert.IsType<HistoryChartLine>(level.Segment);
            Assert.Equal(.75, segment.Start.X);
            Assert.Equal(.25, segment.End.X);
        });
        Assert.Equal(1, result.First!.Value.OriginalIndex);
        Assert.Equal(0, result.Second!.Value.OriginalIndex);
        Assert.Equal(set.First.Hit, result.First!.Value.Hit);
        Assert.Equal(set.Second.Hit, result.Second!.Value.Hit);
    }

    [Fact]
    public void LevelsAreBoundedBetweenDisplayCentersAndClipXWithoutExtension()
    {
        var source = Source([Bar(0, 20), Bar(1, 40), Bar(2, 60), Bar(3, 80), Bar(4, 100)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 1), Capture(raw, 3));
        var full = PriceFibonacciRetracementGeometry.Project(set, raw);
        Assert.All(full.Levels, level =>
        {
            var segment = Assert.IsType<HistoryChartLine>(level.Segment);
            Assert.Equal(.3, segment.Start.X);
            Assert.Equal(.7, segment.End.X);
        });
        var clipped = PriceFibonacciRetracementGeometry.Project(set, source.Query(new(2, 1, fixedDomain: new(0, 100))));
        Assert.All(clipped.Levels, level =>
        {
            var segment = Assert.IsType<HistoryChartLine>(level.Segment);
            Assert.Equal(0, segment.Start.X);
            Assert.Equal(1, segment.End.X);
            AssertFiniteUnit(segment);
        });
        foreach (var start in new[] { 0, 4 })
        {
            var outside = PriceFibonacciRetracementGeometry.Project(set, source.Query(new(start, 1, fixedDomain: new(0, 100))));
            Assert.Equal(PriceTrendProjectionStatus.OutsideViewport, outside.Status);
            Assert.All(outside.Levels, level =>
            {
                Assert.Equal(PriceFibonacciRetracementLevelStatus.OutsideViewport, level.Status);
                Assert.Null(level.Segment);
            });
        }
    }

    [Fact]
    public void AdditionalLevelCanBeVisibleWhenEntireAnchorDiagonalIsOutsideViewport()
    {
        var source = Source([Bar(0, 10), Bar(1, 20)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1)) { ShowAdditionalLevels = true };
        var current = source.Query(new(fixedDomain: new(0, 5)));
        Assert.Equal(PriceTrendProjectionStatus.OutsideViewport,
            PriceTrendGeometry.Project(new(1, set.First, set.Second), current).Status);
        var result = PriceFibonacciRetracementGeometry.Project(set, current);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        var level = Assert.Single(result.Levels.Where(level => level.Status == PriceFibonacciRetracementLevelStatus.Visible));
        Assert.Equal(1.618, level.Ratio);
        Assert.Equal(3.82, level.Price);
        AssertFiniteUnit(Assert.IsType<HistoryChartLine>(level.Segment));
        Assert.Equal(7, result.Levels.Count(level => level.Status == PriceFibonacciRetracementLevelStatus.OutsideViewport));
    }

    [Fact]
    public void ExactOppositeSignExtremeArithmeticKeepsFixedLevelsFiniteAndMarksUnrepresentableExtras()
    {
        var source = Source([Bar(0, double.MaxValue), Bar(1, -double.MaxValue)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1))
            { ShowLevel23_6 = true, ShowLevel76_4 = true, ShowAdditionalLevels = true };
        var result = PriceFibonacciRetracementGeometry.Project(set, raw);
        Assert.Equal(10, result.Levels.Length);
        Assert.Equal(-double.MaxValue, result.Levels[0].Price);
        Assert.Equal(0, result.Levels.Single(level => level.Ratio == .5).Price);
        Assert.Equal(double.MaxValue, result.Levels.Single(level => level.Ratio == 1).Price);
        foreach (var level in result.Levels.Where(level => level.Ratio <= 1))
        {
            Assert.True(double.IsFinite(level.Price!.Value));
            var segment = Assert.IsType<HistoryChartLine>(level.Segment);
            AssertFiniteUnit(segment);
        }
        Assert.Equal(.618, result.Levels.Single(level => level.Ratio == .382).Segment!.Value.Start.Y);
        Assert.Equal(.382, result.Levels.Single(level => level.Ratio == .618).Segment!.Value.Start.Y);
        Assert.All(result.Levels.Where(level => level.Ratio > 1), level =>
        {
            Assert.Equal(PriceFibonacciRetracementLevelStatus.OutOfRange, level.Status);
            Assert.Null(level.Price);
            Assert.Null(level.Segment);
        });
        var reversed = PriceFibonacciRetracementGeometry.Project(set with { First = set.Second, Second = set.First }, raw);
        Assert.All(reversed.Levels.Where(level => level.Ratio > 1), level =>
            Assert.Equal(PriceFibonacciRetracementLevelStatus.OutOfRange, level.Status));
    }

    [Fact]
    public void ExactFiniteRangeChecksDoNotClampOptionalExtrapolationsToMaximum()
    {
        var source = Source([Bar(0, double.MaxValue), Bar(1, Math.BitDecrement(double.MaxValue))]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1)) { ShowAdditionalLevels = true };
        var result = PriceFibonacciRetracementGeometry.Project(set, raw);
        Assert.All(result.Levels.Where(level => level.Ratio > 1), level =>
        {
            Assert.Equal(PriceFibonacciRetracementLevelStatus.OutOfRange, level.Status);
            Assert.Null(level.Price);
            Assert.Null(level.Segment);
        });
        Assert.All(result.Levels.Where(level => level.Ratio <= 1), level => Assert.NotNull(level.Price));
    }

    [Fact]
    public void ExactClippingDoesNotInventBoundaryLevelsWhenRoundedPricesEqualBounds()
    {
        var source = Source([Bar(0, 110), Bar(1, 10)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1));
        // Binary64 48.2 is above exact 241/5; binary64 71.8 is below exact 359/5.
        var result = PriceFibonacciRetracementGeometry.Project(set, source.Query(new(fixedDomain: new(48.2, 71.8))));
        foreach (var ratio in new[] { .382, .618 })
        {
            var level = result.Levels.Single(level => level.Ratio == ratio);
            Assert.Equal(ratio == .382 ? 48.2 : 71.8, level.Price);
            Assert.Equal(PriceFibonacciRetracementLevelStatus.OutsideViewport, level.Status);
            Assert.Null(level.Segment);
        }
        Assert.Equal(.5, Assert.Single(result.Levels.Where(level => level.Segment.HasValue)).Ratio);
    }

    [Fact]
    public void SubnormalPricesRoundOnceTiesToEvenWhileGeometryUsesExactValue()
    {
        foreach (var (first, second, price, y) in new[]
        {
            (double.Epsilon, 0d, 0d, .875),
            (2 * double.Epsilon, double.Epsilon, 2 * double.Epsilon, .625),
            (-double.Epsilon, 0d, -0d, .625)
        })
        {
            var source = Source([Bar(0, first), Bar(1, second)]);
            var raw = source.Query();
            var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1));
            var domain = first < 0 ? new HistoryValueDomain(-2 * double.Epsilon, 2 * double.Epsilon) :
                new HistoryValueDomain(0, 4 * double.Epsilon);
            var level = PriceFibonacciRetracementGeometry.Project(set, source.Query(new(fixedDomain: domain)))
                .Levels.Single(level => level.Ratio == .5);
            Assert.Equal(price, level.Price);
            Assert.Equal(y, Assert.IsType<HistoryChartLine>(level.Segment).Start.Y);
        }
    }

    [Fact]
    public void EqualPriceAnchorsKeepAllLevelsAndConstantDomainCenterWithoutExtensions()
    {
        var source = Source([Bar(0, 7), Bar(1, 7)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1))
            { ShowLevel23_6 = true, ShowLevel76_4 = true, ShowAdditionalLevels = true };
        var result = PriceFibonacciRetracementGeometry.Project(set, raw);
        Assert.Equal(10, result.Levels.Length);
        Assert.All(result.Levels, level =>
        {
            Assert.Equal(7, level.Price);
            Assert.Equal(new HistoryChartLine(new(.25, .5), new(.75, .5)), level.Segment);
        });
    }

    [Fact]
    public void AggregateMappingUsesOriginalAnchorPricesAndRecordsNotAggregateOhlc()
    {
        var source = Source([Bar(0, 6), Bar(1, 100), Bar(5, 8), Bar(6, 200)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 2));
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var result = PriceFibonacciRetracementGeometry.Project(set,
            aggregation.Source.Query(new(fixedDomain: new(0, 200))), aggregation);
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.Equal(0, result.First!.Value.OriginalIndex);
        Assert.Equal(2, result.Second!.Value.OriginalIndex);
        Assert.Equal(0, result.First!.Value.DisplayIndex);
        Assert.Equal(1, result.Second!.Value.DisplayIndex);
        Assert.Equal(set.First.Hit, result.First!.Value.Hit);
        Assert.Equal(set.Second.Hit, result.Second!.Value.Hit);
        Assert.Equal(100, aggregation.Source.Bars[0].Close);
        Assert.Equal(200, aggregation.Source.Bars[1].Close);
        Assert.Equal(new[] { 8d, 7.236, 7, 6.764, 6 }, result.Levels.Select(level => level.Price!.Value));
        Assert.All(result.Levels, level =>
        {
            var segment = Assert.IsType<HistoryChartLine>(level.Segment);
            Assert.Equal(.25, segment.Start.X);
            Assert.Equal(.75, segment.End.X);
        });
        AssertSuppressed(PriceTrendProjectionStatus.AggregationMismatch,
            PriceFibonacciRetracementGeometry.Project(set, raw, aggregation));
    }

    [Fact]
    public void CollapsedAggregateSlotSuppressesWholeSetAndRawProjectionRestoresAuthoredRecords()
    {
        var source = Source([Bar(0, 2), Bar(1, 8)]);
        var raw = source.Query();
        var store = new PriceFibonacciRetracements();
        var set = Add(store, Capture(raw, 0), Capture(raw, 1));
        var before = PriceFibonacciRetracementGeometry.Project(set, raw);
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var result = PriceFibonacciRetracementGeometry.Project(set, aggregation.Source.Query(), aggregation);
        AssertSuppressed(PriceTrendProjectionStatus.CollapsedAggregateSlot, result);
        Assert.Equal(0, result.First!.Value.DisplayIndex);
        Assert.Equal(0, result.Second!.Value.DisplayIndex);
        Assert.Equal(set.First.Hit, result.First!.Value.Hit);
        Assert.Equal(set.Second.Hit, result.Second!.Value.Hit);
        Assert.Same(set, Assert.Single(store.Items));
        var restored = PriceFibonacciRetracementGeometry.Project(set, raw);
        Assert.Equal(before.Levels.ToArray(), restored.Levels.ToArray());
        Assert.Equal(PriceTrendProjectionStatus.Visible, restored.Status);
    }

    [Fact]
    public void SourceVersionAdvanceRetainsOriginalProvenanceAndResolvesShiftedIndexes()
    {
        var source = Source([Bar(0, 1), Bar(1, 2), Bar(2, 3), Bar(3, 4)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 1), Capture(raw, 3));
        var updated = Source(source.Bars.Skip(1).ToArray(), sourceVersion: 9);
        var result = PriceFibonacciRetracementGeometry.Project(set, updated.Query());
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.Equal(0, result.First!.Value.OriginalIndex);
        Assert.Equal(2, result.Second!.Value.OriginalIndex);
        Assert.Equal(9, result.First!.Value.Hit.SourceVersion);
        Assert.Equal(0, set.First.Hit.SourceVersion);
        Assert.Equal(2, set.First.Value);
    }

    [Fact]
    public void MismatchEvictionGapTimestampOffsetAndSelectedValueChangesSuppressWithoutLosingRecord()
    {
        var bars = new[] { new PriceBar(0, Epoch, 1, 10, 0, 2), new PriceBar(1, Epoch.AddMinutes(1), 7, 10, 0, 8) };
        var source = Source(bars);
        var raw = source.Query();
        var store = new PriceFibonacciRetracements();
        var set = Add(store, Capture(raw, 0), Capture(raw, 1));
        var cases = new (PriceChartSource Source, PriceTrendProjectionStatus Status)[]
        {
            (Source(bars, seriesId: "other"), PriceTrendProjectionStatus.DatasetMismatch),
            (Source(bars, datasetVersion: 8), PriceTrendProjectionStatus.DatasetMismatch),
            (Source([bars[1]]), PriceTrendProjectionStatus.MissingAnchor),
            (Source([new PriceBar(0, Epoch.AddTicks(1), 1, 10, 0, 2), bars[1]]), PriceTrendProjectionStatus.MissingAnchor),
            (Source([new PriceBar(0, Epoch.ToOffset(TimeSpan.FromHours(1)), 1, 10, 0, 2), bars[1]]), PriceTrendProjectionStatus.MissingAnchor),
            (Source([new PriceBar(0, Epoch, null, null, null, null), bars[1]]), PriceTrendProjectionStatus.GapAnchor),
            (Source([new PriceBar(0, Epoch, 1, 10, 0, 3), bars[1]]), PriceTrendProjectionStatus.ChangedAnchorValue)
        };
        foreach (var test in cases)
        {
            var result = PriceFibonacciRetracementGeometry.Project(set, test.Source.Query());
            AssertSuppressed(test.Status, result);
            Assert.Same(set, result.Retracement);
            Assert.Same(set, Assert.Single(store.Items));
            Assert.Equal(2, set.First.Value);
        }
        var otherFieldsChanged = Source([new PriceBar(0, Epoch, 5, 20, -1, 2, 99), bars[1]], sourceVersion: 7);
        Assert.Equal(PriceTrendProjectionStatus.Visible,
            PriceFibonacciRetracementGeometry.Project(set, otherFieldsChanged.Query()).Status);
    }

    [Fact]
    public void EndpointSignedZeroBitsSurviveProjectionAndChangingThemSuppressesTheSet()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var source = Source([new PriceBar(0, Epoch, 0, 0, negativeZero, negativeZero), Bar(1, 1)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 1));
        var endpoint = PriceFibonacciRetracementGeometry.Project(set, raw).Levels.Single(level => level.Ratio == 1);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(endpoint.Price!.Value));
        var changed = Source([Bar(0, 0), Bar(1, 1)], sourceVersion: 1);
        var result = PriceFibonacciRetracementGeometry.Project(set, changed.Query());
        AssertSuppressed(PriceTrendProjectionStatus.ChangedAnchorValue, result);
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(result.First!.Value.Hit.Bar.Close!.Value));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(set.First.Value));
    }

    [Fact]
    public void EmptyNoDomainAndBudgetRefusedFramesKeepResolvableOriginalProvenance()
    {
        var source = Source([Bar(0, 2), new PriceBar(1, Epoch.AddMinutes(1), null, null, null, null), Bar(2, 8)]);
        var raw = source.Query();
        var set = new PriceFibonacciRetracement(1, Capture(raw, 0), Capture(raw, 2));
        var cases = new (PriceChartSnapshot Snapshot, PriceTrendProjectionStatus Status)[]
        {
            (source.Query(new(barCount: 0)), PriceTrendProjectionStatus.EmptyViewport),
            (source.Query(new(1, 1)), PriceTrendProjectionStatus.NoDomain),
            (source.Query(new(geometryBudget: 0)), PriceTrendProjectionStatus.GeometryBudgetExceeded)
        };
        foreach (var test in cases)
        {
            var result = PriceFibonacciRetracementGeometry.Project(set, test.Snapshot);
            AssertSuppressed(test.Status, result);
            Assert.Equal(set.First.Hit, result.First!.Value.Hit);
            Assert.Equal(set.Second.Hit, result.Second!.Value.Hit);
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

    private static PriceFibonacciRetracement Add(PriceFibonacciRetracements store, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        var draft = Assert.IsType<PriceFibonacciRetracementDraft>(store.BeginNew());
        Assert.True(store.Commit(draft, first, second));
        return store.Items[^1];
    }

    private static void AssertSuppressed(PriceTrendProjectionStatus expected, PriceFibonacciRetracementProjection result)
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
