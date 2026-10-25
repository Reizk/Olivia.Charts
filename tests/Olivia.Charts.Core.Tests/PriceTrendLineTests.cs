using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceTrendLineTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PriceTrendValueLevel.Low, 3d)]
    [InlineData(PriceTrendValueLevel.High, 9d)]
    [InlineData(PriceTrendValueLevel.Open, 4d)]
    [InlineData(PriceTrendValueLevel.Close, 7d)]
    public void CaptureRetainsExactOriginalHitAndSelectedOhlcField(PriceTrendValueLevel level, double expected)
    {
        var bar = new PriceBar(17, Epoch.ToOffset(TimeSpan.FromMinutes(345)), 4, 9, 3, 7, 50);
        var source = Source([bar, Bar(18, 8)], sourceVersion: 42);
        var snapshot = source.Query();
        var anchor = Capture(snapshot, 0, level);
        Assert.Equal(snapshot.Candles[0].Hit, anchor.Hit);
        Assert.Equal(level, anchor.Level);
        Assert.Equal(expected, anchor.Value);
        Assert.Equal(42, anchor.Hit.SourceVersion);
        Assert.Equal(bar.Timestamp.Ticks, anchor.Hit.Timestamp.Ticks);
        Assert.Equal(bar.Timestamp.Offset, anchor.Hit.Timestamp.Offset);
        Assert.False(new PriceTrendLine(1, anchor, Capture(snapshot, 1)).ExtrapolateToInfinity);
    }

    [Fact]
    public void CaptureRequiresVisibleOriginalCandleAndVisibleSelectedLevel()
    {
        var source = Source([new PriceBar(0, Epoch, 4, 10, 0, 6),
            new PriceBar(1, Epoch.AddMinutes(1), null, null, null, null), Bar(2, 20)]);
        var clipped = source.Query(new(fixedDomain: new(3, 7)));
        Assert.Single(clipped.Candles);
        Assert.NotNull(PriceTrendGeometry.CreateAnchor(clipped, 0, PriceTrendValueLevel.Close));
        Assert.Null(PriceTrendGeometry.CreateAnchor(clipped, 0, PriceTrendValueLevel.High));
        Assert.Null(PriceTrendGeometry.CreateAnchor(clipped, 0, PriceTrendValueLevel.Low));
        Assert.Null(PriceTrendGeometry.CreateAnchor(clipped, 1, PriceTrendValueLevel.Close));
        Assert.Null(PriceTrendGeometry.CreateAnchor(clipped, 2, PriceTrendValueLevel.Close));
        Assert.Null(PriceTrendGeometry.CreateAnchor(clipped, -1, PriceTrendValueLevel.Close));
        Assert.Null(PriceTrendGeometry.CreateAnchor(source.Query(new(barStart: 2)), 0, PriceTrendValueLevel.Close));
        Assert.Null(PriceTrendGeometry.CreateAnchor(source.Query(new(geometryBudget: 0)), 0, PriceTrendValueLevel.Close));
        Assert.Null(PriceTrendGeometry.CreateAnchor(source.Query(new(barCount: 0)), 0, PriceTrendValueLevel.Close));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceTrendGeometry.CreateAnchor(clipped, 0, (PriceTrendValueLevel)99));
    }

    [Fact]
    public void OnlyExactCurrentDraftCanCommitAndStaleRejectionPrecedesValidation()
    {
        var (first, second) = Anchors();
        var store = new PriceTrendLines();
        var stale = Assert.IsType<PriceTrendLineDraft>(store.BeginNew());
        var draft = Assert.IsType<PriceTrendLineDraft>(store.BeginNew());
        var foreign = Assert.IsType<PriceTrendLineDraft>(new PriceTrendLines().BeginNew());
        foreach (var rejected in new[] { stale, draft with { }, foreign, new PriceTrendLineDraft(draft.Revision, null, null) })
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
        var store = new PriceTrendLines();
        var original = Add(store, first, second);
        var old = store.Items;
        var draft = Assert.IsType<PriceTrendLineDraft>(store.BeginEdit(original.Id));
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
        Assert.Equal(new PriceTrendLine(original.Id, second, first), Assert.Single(store.Items));
        Assert.Same(original, Assert.Single(old));
    }

    [Fact]
    public void CancellationSelectionRemovalAndClearKeepImmutableSnapshotsAndMonotonicIds()
    {
        var (first, second) = Anchors();
        var store = new PriceTrendLines();
        var original = Add(store, first, second);
        var old = store.Items;
        var edit = Assert.IsType<PriceTrendLineDraft>(store.BeginEdit(original.Id));
        Assert.True(store.Cancel(edit));
        Assert.Equal(original.Id, store.SelectedId);
        Assert.Equal(old, store.Items);
        var fresh = Assert.IsType<PriceTrendLineDraft>(store.BeginNew());
        Assert.Null(store.SelectedId);
        Assert.True(store.Select(original.Id));
        Assert.False(store.Commit(fresh, first, second));
        edit = Assert.IsType<PriceTrendLineDraft>(store.BeginEdit(original.Id));
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
        edit = Assert.IsType<PriceTrendLineDraft>(store.BeginEdit(next.Id));
        store.Clear();
        Assert.False(store.Commit(edit, first, second));
        Assert.Equal(next.Id + 1, Add(store, first, second).Id);
        fresh = Assert.IsType<PriceTrendLineDraft>(store.BeginNew());
        Assert.False(store.RemoveSelected());
        Assert.Null(store.Draft);
        Assert.False(store.Cancel(fresh));
        Assert.Single(store.Items);
    }

    [Fact]
    public void CapacityRefusalPreservesActiveEditAndConcurrentCommitConsumesOneToken()
    {
        var (first, second) = Anchors();
        var store = new PriceTrendLines();
        var draft = Assert.IsType<PriceTrendLineDraft>(store.BeginNew());
        var successes = 0;
        Parallel.For(0, 32, _ => { if (store.Commit(draft, first, second)) Interlocked.Increment(ref successes); });
        Assert.Equal(1, successes);
        Assert.Single(store.Items);
        while (store.Items.Length < PriceTrendLines.MaximumLines) Add(store, first, second);
        Assert.Equal(32, store.Items.Length);
        draft = Assert.IsType<PriceTrendLineDraft>(store.BeginEdit(1));
        var revision = store.Revision;
        Assert.Null(store.BeginNew());
        Assert.Same(draft, store.Draft);
        Assert.Equal(revision, store.Revision);
        Assert.True(store.Commit(draft, second, first));
        Assert.True(store.RemoveSelected());
        Assert.Equal(33, Add(store, first, second).Id);
    }

    [Fact]
    public void ExtensionFlagSurvivesDraftEditsAndExactInstanceTogglePreservesStoreState()
    {
        var (first, second) = Anchors();
        var store = new PriceTrendLines();
        var finiteDraft = Assert.IsType<PriceTrendLineDraft>(store.BeginNew());
        Assert.False(finiteDraft.ExtrapolateToInfinity);
        Assert.True(store.Cancel(finiteDraft));
        var draft = Assert.IsType<PriceTrendLineDraft>(store.BeginNew(extrapolateToInfinity: true));
        Assert.True(draft.ExtrapolateToInfinity);
        Assert.True(store.Commit(draft, first, second));
        var original = Assert.Single(store.Items);
        Assert.True(original.ExtrapolateToInfinity);
        var snapshot = store.Items;
        var revision = store.Revision;
        Assert.True(store.SetExtrapolateToInfinity(original, true));
        Assert.Equal(revision, store.Revision);
        Assert.Same(original, Assert.Single(store.Items));
        Assert.False(store.SetExtrapolateToInfinity(original with { }, false));
        Assert.False(store.SetExtrapolateToInfinity(null!, false));
        Assert.Equal(revision, store.Revision);

        draft = Assert.IsType<PriceTrendLineDraft>(store.BeginEdit(original.Id));
        Assert.True(draft.ExtrapolateToInfinity);
        revision = store.Revision;
        Assert.False(store.SetExtrapolateToInfinity(original, false));
        Assert.Equal(revision, store.Revision);
        Assert.Same(draft, store.Draft);
        Assert.Same(original, Assert.Single(store.Items));
        Assert.True(store.Commit(draft, second, first));
        var edited = Assert.Single(store.Items);
        Assert.True(edited.ExtrapolateToInfinity);
        Assert.False(store.SetExtrapolateToInfinity(original, false));

        var selected = Add(store, first, second);
        Assert.False(selected.ExtrapolateToInfinity);
        revision = store.Revision;
        Assert.True(store.SetExtrapolateToInfinity(edited, false));
        var changed = store.Items[0];
        Assert.Equal(revision + 1, store.Revision);
        Assert.Equal(edited.Id, changed.Id);
        Assert.Equal(edited.First, changed.First);
        Assert.Equal(edited.Second, changed.Second);
        Assert.False(changed.ExtrapolateToInfinity);
        Assert.Equal(selected.Id, store.SelectedId);
        Assert.Null(store.Draft);
        Assert.Same(original, Assert.Single(snapshot));
        Assert.True(original.ExtrapolateToInfinity);
        Assert.False(store.SetExtrapolateToInfinity(edited, true));
        Assert.True(store.SetExtrapolateToInfinity(changed, false));
        Assert.Equal(revision + 1, store.Revision);
        var successes = 0;
        Parallel.For(0, 8, _ =>
        {
            if (store.SetExtrapolateToInfinity(changed, true)) Interlocked.Increment(ref successes);
        });
        Assert.Equal(1, successes);
        Assert.Equal(revision + 2, store.Revision);
        Assert.True(store.Items[0].ExtrapolateToInfinity);
        Assert.Equal(selected.Id, store.SelectedId);
    }

    [Fact]
    public void PositiveRayExtendsBeyondLargerXForEitherAnchorOrderAndAllSlopeSigns()
    {
        foreach (var (left, right, startY, endY) in new[]
        {
            (2d, 4d, .8, .1), (8d, 6d, .2, .9), (4d, 4d, .6, .6)
        })
        {
            var source = Source([Bar(0, 0), Bar(1, left), Bar(2, right), Bar(3, 0), Bar(4, 0)]);
            var raw = source.Query();
            var first = Capture(raw, 1);
            var second = Capture(raw, 2);
            foreach (var reversed in new[] { false, true })
            {
                var line = new PriceTrendLine(1, reversed ? second : first, reversed ? first : second)
                    { ExtrapolateToInfinity = true };
                var projection = PriceTrendGeometry.Project(line, source.Query(new(fixedDomain: new(0, 10))));
                Assert.Equal(PriceTrendProjectionStatus.Visible, projection.Status);
                var segment = Assert.IsType<HistoryChartLine>(projection.Segment);
                Assert.Equal(new HistoryChartPoint(.3, startY), segment.Start);
                Assert.Equal(new HistoryChartPoint(1, endY), segment.End);
                AssertFiniteUnit(segment);
                Assert.Equal(line.First.Hit, projection.First!.Value.Hit);
                Assert.Equal(line.Second.Hit, projection.Second!.Value.Hit);
                Assert.Same(line, projection.Line);
            }
        }
    }

    [Fact]
    public void PositiveRayCanEnterAfterBothEndpointsButNeverExtendsBeforeTheLesserX()
    {
        var source = Source([Bar(0, -6), Bar(1, -4), Bar(2, 0), Bar(3, 2), Bar(4, 2)]);
        var raw = source.Query();
        var finite = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 1));
        var rightViewport = source.Query(new(3, 2, fixedDomain: new(0, 4)));
        AssertSuppressed(PriceTrendProjectionStatus.OutsideViewport, PriceTrendGeometry.Project(finite, rightViewport));
        var segment = Assert.IsType<HistoryChartLine>(PriceTrendGeometry.Project(
            finite with { ExtrapolateToInfinity = true }, rightViewport).Segment);
        Assert.Equal(new HistoryChartPoint(.25, 1), segment.Start);
        Assert.Equal(new HistoryChartPoint(1, .25), segment.End);

        var leftViewport = source.Query(new(0, 2, fixedDomain: new(0, 4)));
        var later = new PriceTrendLine(2, Capture(raw, 4), Capture(raw, 3)) { ExtrapolateToInfinity = true };
        AssertSuppressed(PriceTrendProjectionStatus.OutsideViewport, PriceTrendGeometry.Project(later, leftViewport));
    }

    [Fact]
    public void PositiveRayClipsAtValueBoundaryWithoutFollowingItOrInventingPointSegments()
    {
        foreach (var (first, second, minimum, maximum, startY, endY) in new[]
        {
            (2d, 4d, 0d, 5d, .6, 0d), (8d, 6d, 5d, 10d, .4, 1d)
        })
        {
            var source = Source([Bar(0, first), Bar(1, second), Bar(2, 0), Bar(3, 0), Bar(4, 0)]);
            var raw = source.Query();
            var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 1)) { ExtrapolateToInfinity = true };
            var result = PriceTrendGeometry.Project(line, source.Query(new(fixedDomain: new(minimum, maximum))));
            var segment = Assert.IsType<HistoryChartLine>(result.Segment);
            Assert.Equal(new HistoryChartPoint(.1, startY), segment.Start);
            Assert.Equal(new HistoryChartPoint(.4, endY), segment.End);
            AssertFiniteUnit(segment);
            AssertSuppressed(PriceTrendProjectionStatus.OutsideViewport,
                PriceTrendGeometry.Project(line, source.Query(new(fixedDomain: new(5, 5)))));
        }
        var horizontalSource = Source([Bar(0, 7), Bar(1, 7)]);
        var horizontalRaw = horizontalSource.Query();
        var horizontal = new PriceTrendLine(1, Capture(horizontalRaw, 0), Capture(horizontalRaw, 1))
            { ExtrapolateToInfinity = true };
        AssertSuppressed(PriceTrendProjectionStatus.OutsideViewport,
            PriceTrendGeometry.Project(horizontal, horizontalSource.Query(new(fixedDomain: new(0, 5)))));
    }

    [Fact]
    public void OrdinalCentersIgnoreElapsedTimeAndPreserveAuthoredDirection()
    {
        var source = Source([new PriceBar(1, Epoch, 2, 2, 2, 2),
            new PriceBar(2, Epoch.AddDays(3), 8, 8, 8, 8)]);
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 1), Capture(raw, 0));
        var projection = PriceTrendGeometry.Project(line, source.Query(new(fixedDomain: new(0, 10))));
        Assert.Equal(PriceTrendProjectionStatus.Visible, projection.Status);
        var segment = Assert.IsType<HistoryChartLine>(projection.Segment);
        Assert.Equal(new HistoryChartPoint(.75, .2), segment.Start);
        Assert.Equal(new HistoryChartPoint(.25, .8), segment.End);
        Assert.Equal(1, projection.First!.Value.OriginalIndex);
        Assert.Equal(0, projection.Second!.Value.DisplayIndex);
        Assert.Same(line, projection.Line);
    }

    [Fact]
    public void OffscreenEndpointsStillClipAcrossVisibleViewportWithoutEndpointClamping()
    {
        var source = Source(Enumerable.Range(0, 5).Select(index => Bar(index, 2.5 * index)).ToArray());
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 4));
        var projection = PriceTrendGeometry.Project(line, source.Query(new(2, 1, fixedDomain: new(0, 10))));
        var segment = Assert.IsType<HistoryChartLine>(projection.Segment);
        Assert.Equal(new HistoryChartPoint(0, .625), segment.Start);
        Assert.Equal(new HistoryChartPoint(1, .375), segment.End);
        Assert.Equal(0, projection.First!.Value.OriginalIndex);
        Assert.Equal(4, projection.Second!.Value.OriginalIndex);
        Assert.Equal(0, line.First.Value);
        Assert.Equal(10, line.Second.Value);
        Assert.Equal(segment, PriceTrendGeometry.Project(line with { ExtrapolateToInfinity = true },
            source.Query(new(2, 1, fixedDomain: new(0, 10)))).Segment);
    }

    [Fact]
    public void FiniteSegmentDoesNotInventRayOrBoundaryHuggingGeometry()
    {
        var source = Source([Bar(0, 20), Bar(1, 30), Bar(2, 40)]);
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 1));
        var below = PriceTrendGeometry.Project(line, source.Query(new(fixedDomain: new(0, 10))));
        AssertSuppressed(PriceTrendProjectionStatus.OutsideViewport, below);
        var pastEndpoint = PriceTrendGeometry.Project(line, source.Query(new(2, 1, fixedDomain: new(20, 50))));
        AssertSuppressed(PriceTrendProjectionStatus.OutsideViewport, pastEndpoint);
        Assert.False(line.ExtrapolateToInfinity);
    }

    [Fact]
    public void ExactClippingPreservesExtremeCrossingsEvenWhenIntersectionXCoordinatesRoundTogether()
    {
        var source = Source([Bar(0, -double.MaxValue), Bar(1, double.MaxValue)]);
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 1));
        foreach (var extrapolate in new[] { false, true })
        foreach (var domain in new[] { new HistoryValueDomain(-1, 1),
            new HistoryValueDomain(-double.Epsilon, double.Epsilon),
            new HistoryValueDomain(-double.MaxValue, double.MaxValue) })
        {
            var result = PriceTrendGeometry.Project(line with { ExtrapolateToInfinity = extrapolate },
                source.Query(new(fixedDomain: domain)));
            Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
            var segment = Assert.IsType<HistoryChartLine>(result.Segment);
            AssertFiniteUnit(segment);
            Assert.Equal(1, segment.Start.Y);
            Assert.Equal(0, segment.End.Y);
            if (domain.Maximum < double.MaxValue)
            {
                Assert.Equal(.5, segment.Start.X);
                Assert.Equal(.5, segment.End.X);
            }
        }
    }

    [Fact]
    public void ProjectionRetainsRepresentableSubnormalNearTopAndConstantDomainCenter()
    {
        var source = Source([Bar(0, 0), Bar(1, double.Epsilon)]);
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 1));
        var segment = Assert.IsType<HistoryChartLine>(PriceTrendGeometry.Project(line,
            source.Query(new(fixedDomain: new(-1, double.Epsilon)))).Segment);
        Assert.Equal(double.Epsilon, segment.Start.Y);
        Assert.Equal(0, segment.End.Y);
        Assert.Equal(segment, PriceTrendGeometry.Project(line with { ExtrapolateToInfinity = true },
            source.Query(new(fixedDomain: new(-1, double.Epsilon)))).Segment);
        source = Source([Bar(0, 7), Bar(1, 7)]);
        raw = source.Query();
        segment = Assert.IsType<HistoryChartLine>(PriceTrendGeometry.Project(
            new(1, Capture(raw, 0), Capture(raw, 1)), raw).Segment);
        Assert.Equal(new HistoryChartPoint(.25, .5), segment.Start);
        Assert.Equal(new HistoryChartPoint(.75, .5), segment.End);
        segment = Assert.IsType<HistoryChartLine>(PriceTrendGeometry.Project(
            new(1, Capture(raw, 1), Capture(raw, 0)) { ExtrapolateToInfinity = true }, raw).Segment);
        Assert.Equal(new HistoryChartPoint(.25, .5), segment.Start);
        Assert.Equal(new HistoryChartPoint(1, .5), segment.End);
    }

    [Fact]
    public void AggregationMapsOriginalContainingRangesWithoutReplacingOriginalPriceOrHit()
    {
        var source = Source([Bar(0, 6), Bar(1, 100), Bar(5, 8), Bar(6, 200)]);
        var raw = source.Query(new(fixedDomain: new(0, 200)));
        var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 2));
        var originalProjection = PriceTrendGeometry.Project(line, raw);
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var result = PriceTrendGeometry.Project(line, aggregation.Source.Query(new(fixedDomain: new(0, 200))), aggregation);
        var segment = Assert.IsType<HistoryChartLine>(result.Segment);
        Assert.Equal(new HistoryChartPoint(.25, .97), segment.Start);
        Assert.Equal(new HistoryChartPoint(.75, .96), segment.End);
        Assert.Equal(6, line.First.Value);
        Assert.Equal(8, line.Second.Value);
        Assert.Equal(100, aggregation.Source.Bars[0].Close);
        Assert.Equal(200, aggregation.Source.Bars[1].Close);
        Assert.Equal(0, result.First!.Value.OriginalIndex);
        Assert.Equal(2, result.Second!.Value.OriginalIndex);
        Assert.Equal(1, result.Second!.Value.DisplayIndex);
        Assert.Equal(line.Second.Hit, result.Second!.Value.Hit);
        Assert.Equal(originalProjection, PriceTrendGeometry.Project(line, raw));
        var ray = line with { ExtrapolateToInfinity = true };
        var extended = PriceTrendGeometry.Project(ray, aggregation.Source.Query(new(fixedDomain: new(0, 200))), aggregation);
        segment = Assert.IsType<HistoryChartLine>(extended.Segment);
        Assert.Equal(new HistoryChartPoint(.25, .97), segment.Start);
        Assert.Equal(new HistoryChartPoint(1, .955), segment.End);
        Assert.Equal(result.First, extended.First);
        Assert.Equal(result.Second, extended.Second);
        Assert.Same(ray, extended.Line);
        AssertSuppressed(PriceTrendProjectionStatus.AggregationMismatch,
            PriceTrendGeometry.Project(line, raw, aggregation));
    }

    [Fact]
    public void AggregateSlotCollapseIsExplicitAndRawRestoreRecoversUnchangedLine()
    {
        var source = Source([Bar(0, 2), Bar(1, 8)]);
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 1));
        var before = PriceTrendGeometry.Project(line, raw);
        var aggregation = PriceTimeAggregator.Aggregate(source, PriceTimeInterval.Minute5);
        var collapsed = PriceTrendGeometry.Project(line, aggregation.Source.Query(), aggregation);
        AssertSuppressed(PriceTrendProjectionStatus.CollapsedAggregateSlot, collapsed);
        Assert.Equal(0, collapsed.First!.Value.DisplayIndex);
        Assert.Equal(0, collapsed.Second!.Value.DisplayIndex);
        Assert.Equal(line.First.Hit, collapsed.First!.Value.Hit);
        Assert.Equal(line.Second.Hit, collapsed.Second!.Value.Hit);
        Assert.Same(line, collapsed.Line);
        Assert.Equal(before, PriceTrendGeometry.Project(line, raw));
        var ray = line with { ExtrapolateToInfinity = true };
        AssertSuppressed(PriceTrendProjectionStatus.CollapsedAggregateSlot,
            PriceTrendGeometry.Project(ray, aggregation.Source.Query(), aggregation));
        Assert.Equal(PriceTrendProjectionStatus.Visible, PriceTrendGeometry.Project(ray, raw).Status);
    }

    [Fact]
    public void NewSourceVersionResolvesCurrentOriginalProvenanceAndShiftedOrdinalIndexes()
    {
        var source = Source([Bar(0, 1), Bar(1, 2), Bar(2, 3), Bar(3, 4)]);
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 1), Capture(raw, 3));
        var updated = Source(source.Bars.Skip(1).ToArray(), sourceVersion: 9);
        var result = PriceTrendGeometry.Project(line, updated.Query());
        Assert.Equal(PriceTrendProjectionStatus.Visible, result.Status);
        Assert.Equal(0, result.First!.Value.OriginalIndex);
        Assert.Equal(2, result.Second!.Value.OriginalIndex);
        Assert.Equal(9, result.First!.Value.Hit.SourceVersion);
        Assert.Equal(0, line.First.Hit.SourceVersion);
        Assert.Equal(2, line.First.Value);
    }

    [Fact]
    public void IdentityTimestampOffsetEvictionGapAndValueChangesSuppressWithoutMutatingAnchors()
    {
        var bars = new[] { new PriceBar(0, Epoch, 1, 10, 0, 2), new PriceBar(1, Epoch.AddMinutes(1), 7, 10, 0, 8) };
        var source = Source(bars);
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 1));
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
            var result = PriceTrendGeometry.Project(line, test.Source.Query());
            AssertSuppressed(test.Status, result);
            Assert.Same(line, result.Line);
            Assert.Equal(2, line.First.Value);
            Assert.Equal(bars[0], line.First.Hit.Bar);
        }
        var otherFieldsChanged = Source([new PriceBar(0, Epoch, 5, 20, -1, 2, 99), bars[1]], sourceVersion: 7);
        Assert.Equal(PriceTrendProjectionStatus.Visible, PriceTrendGeometry.Project(line, otherFieldsChanged.Query()).Status);
    }

    [Fact]
    public void SignedZeroBitChangeIsDetectedAlthoughNumericValuesCompareEqual()
    {
        var negativeZero = BitConverter.Int64BitsToDouble(long.MinValue);
        var source = Source([new PriceBar(0, Epoch, 0, 0, negativeZero, negativeZero), Bar(1, 1)]);
        var raw = source.Query();
        var first = Capture(raw, 0);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(first.Value));
        var line = new PriceTrendLine(1, first, Capture(raw, 1));
        var changed = Source([Bar(0, 0), Bar(1, 1)], sourceVersion: 1);
        var result = PriceTrendGeometry.Project(line, changed.Query());
        AssertSuppressed(PriceTrendProjectionStatus.ChangedAnchorValue, result);
        Assert.Equal(0, BitConverter.DoubleToInt64Bits(result.First!.Value.Hit.Bar.Close!.Value));
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(line.First.Value));
    }

    [Fact]
    public void EmptyNoDomainAndBudgetRefusedFramesSuppressWhileRetainingResolvableProvenance()
    {
        var source = Source([Bar(0, 2), new PriceBar(1, Epoch.AddMinutes(1), null, null, null, null), Bar(2, 8)]);
        var raw = source.Query();
        var line = new PriceTrendLine(1, Capture(raw, 0), Capture(raw, 2));
        var cases = new (PriceChartSnapshot Snapshot, PriceTrendProjectionStatus Status)[]
        {
            (source.Query(new(barCount: 0)), PriceTrendProjectionStatus.EmptyViewport),
            (source.Query(new(1, 1)), PriceTrendProjectionStatus.NoDomain),
            (source.Query(new(geometryBudget: 0)), PriceTrendProjectionStatus.GeometryBudgetExceeded)
        };
        foreach (var test in cases)
        {
            var result = PriceTrendGeometry.Project(line, test.Snapshot);
            AssertSuppressed(test.Status, result);
            Assert.Equal(line.First.Hit, result.First!.Value.Hit);
            Assert.Equal(line.Second.Hit, result.Second!.Value.Hit);
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

    private static PriceTrendLine Add(PriceTrendLines store, PriceTrendAnchor first, PriceTrendAnchor second)
    {
        var draft = Assert.IsType<PriceTrendLineDraft>(store.BeginNew());
        Assert.True(store.Commit(draft, first, second));
        return store.Items[^1];
    }

    private static void AssertSuppressed(PriceTrendProjectionStatus expected, PriceTrendProjection result)
    {
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Segment);
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
