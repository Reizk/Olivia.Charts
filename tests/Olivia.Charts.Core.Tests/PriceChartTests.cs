using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class PriceChartTests
{
    private static DateTimeOffset Time(int index) => DateTimeOffset.UnixEpoch.AddDays(index);
    private static PriceBar Bar(int key, double open = 2, double high = 4, double low = 0, double close = 3)
        => new(key, Time(key), open, high, low, close);
    private static PriceChartSource Source(params PriceBar[] bars) => new("price", "Price", bars, 17, 29);

    [Fact]
    public void CopiesInputAndPreservesOriginalIdentityIncludingOffsetAndSignedZero()
    {
        var time = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.FromHours(3));
        var original = new PriceBar(long.MinValue, time, -0d, 1, -1, +0d);
        var input = new[] { original };
        var source = Source(input);
        input[0] = Bar(1);
        var snapshot = source.Query();
        var hit = Assert.Single(snapshot.Candles).Hit;
        Assert.Equal(original, hit.Bar);
        Assert.Equal(time.Offset, hit.Timestamp.Offset);
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(hit.Bar.Open!.Value));
        Assert.Equal(long.MinValue, hit.Key);
        Assert.Equal(17, hit.DatasetVersion);
        Assert.Equal(29, hit.SourceVersion);
        Assert.Equal("price", hit.SeriesId);
        Assert.Same(source, snapshot.Source);
    }

    [Fact]
    public void RejectsEveryPartialGapAndEveryNonfiniteComponent()
    {
        for (var mask = 1; mask < 15; mask++)
            Assert.Throws<ArgumentException>(() => new PriceBar(0, Time(0),
                (mask & 1) == 0 ? null : 0, (mask & 2) == 0 ? null : 0,
                (mask & 4) == 0 ? null : 0, (mask & 8) == 0 ? null : 0));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            for (var component = 0; component < 4; component++)
            {
                var values = new[] { 0d, 0d, 0d, 0d }; values[component] = value;
                Assert.Throws<ArgumentException>(() => new PriceBar(0, Time(0), values[0], values[1], values[2], values[3]));
            }
        Assert.True(new PriceBar(0, Time(0), null, null, null, null).IsGap);
        Assert.True(default(PriceBar).IsGap);
    }

    [Fact]
    public void RejectsInvalidOhlcOrderAndNonIncreasingUtcInstantsAndDuplicateKeys()
    {
        Assert.Throws<ArgumentException>(() => Bar(0, 2, 1, 0, 0));
        Assert.Throws<ArgumentException>(() => Bar(0, 0, 1, 0, 2));
        Assert.Throws<ArgumentException>(() => Bar(0, -1, 1, 0, 0));
        Assert.Throws<ArgumentException>(() => Bar(0, 0, 1, 0, -1));
        Assert.Throws<ArgumentException>(() => Bar(0, 0, -1, 1, 0));
        Assert.Throws<ArgumentException>(() => Source(Bar(2), Bar(1)));
        Assert.Throws<ArgumentException>(() => Source(Bar(1), new(2, Time(1).ToOffset(TimeSpan.FromHours(5)), 0, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => Source(Bar(1), new(1, Time(2), 0, 0, 0, 0)));
    }

    [Fact]
    public void MapsUpDownDojiAndWickWithoutAnchoringDomainToZero()
    {
        var snapshot = Source(Bar(0, 11, 15, 10, 14), Bar(1, 14, 15, 10, 11), Bar(2, 12, 15, 10, 12)).Query();
        Assert.Equal(new HistoryValueDomain(10, 15), snapshot.Domain);
        Assert.Equal(3, snapshot.Candles.Length);
        var up = snapshot.Candles[0]; var down = snapshot.Candles[1]; var doji = snapshot.Candles[2];
        Assert.Equal(.2, up.Body!.Value.Top, 14);
        Assert.Equal(.6, up.Body.Value.Height, 14);
        Assert.Equal(up.Body.Value.Top, down.Body!.Value.Top);
        Assert.Equal(up.Body.Value.Height, down.Body.Value.Height);
        Assert.Equal(0, doji.Body!.Value.Height);
        Assert.Equal(0, up.Wick!.Value.Start.Y);
        Assert.Equal(1, up.Wick.Value.End.Y);
        Assert.Equal(up.Hit, PriceChartGeometry.HitTest(snapshot, up.CenterX, .05));
        Assert.Null(PriceChartGeometry.HitTest(snapshot, up.CenterX + .01, .05));
        Assert.Equal(doji.Hit, PriceChartGeometry.HitTest(snapshot, doji.Body.Value.Left, doji.Body.Value.Top));
        Assert.Null(PriceChartGeometry.HitTest(snapshot, doji.Body.Value.Left, doji.Body.Value.Top + .01));
    }

    [Fact]
    public void GapsOccupySlotsButMissingSessionsDoNotInventThem()
    {
        var source = Source(Bar(0), new(1, Time(1), null, null, null, null), Bar(100));
        var snapshot = source.Query();
        Assert.Equal(2, snapshot.Candles.Length);
        Assert.Single(snapshot.Gaps);
        Assert.Equal(1d / 6, snapshot.Candles[0].CenterX);
        Assert.Equal(5d / 6, snapshot.Candles[1].CenterX);
        var view = source.Query(new(barStart: 1, barCount: 2));
        Assert.Equal(.75, Assert.Single(view.Candles).CenterX);
        Assert.Equal(1, Assert.Single(view.Gaps).Key);
        Assert.Equal(2, view.Candles[0].BarIndex);
        Assert.Null(PriceChartGeometry.HitTest(snapshot, .5, .5));
    }

    [Fact]
    public void ClipsWickAndBodySeparatelyBeforeMappingAndPreservesBoundaryContact()
    {
        var source = Source(Bar(0, 8, 9, 7, 8), Bar(1, 7, 9, 0, 8), Bar(2, 1, 5, 0, 4), Bar(3, 2, 2, 2, 2));
        var snapshot = source.Query(new(fixedDomain: new(2, 4)));
        Assert.Equal(new long[] { 1, 2, 3 }, snapshot.Candles.Select(c => c.Hit.Key));
        Assert.Null(snapshot.Candles[0].Body);
        Assert.Equal(0, snapshot.Candles[0].Wick!.Value.Start.Y);
        Assert.Equal(1, snapshot.Candles[0].Wick!.Value.End.Y);
        Assert.Equal(1, snapshot.Candles[1].Body!.Value.Height);
        Assert.Equal(1, snapshot.Candles[2].Body!.Value.Top);
        Assert.Equal(0, snapshot.Candles[2].Body!.Value.Height);
        Assert.Null(PriceChartGeometry.HitTest(snapshot, .125, 0));
        Assert.Null(PriceChartGeometry.HitTest(snapshot, .375 + .01, 0));
        Assert.Equal(1, PriceChartGeometry.HitTest(snapshot, .375, 0)!.Value.Key);
    }

    [Fact]
    public void EmptyAllGapConstantAndBudgetStatesAreExplicit()
    {
        Assert.Null(Source().Query().Domain);
        var gaps = Source(new PriceBar(0, Time(0), null, null, null, null)).Query();
        Assert.Null(gaps.Domain); Assert.Empty(gaps.Candles); Assert.Empty(gaps.Ticks); Assert.Single(gaps.Gaps);
        var source = Source(Bar(0, 3, 3, 3, 3), Bar(1, 3, 3, 3, 3));
        var constant = source.Query();
        Assert.Single(constant.Ticks);
        Assert.All(constant.Candles, c => { Assert.Equal(.5, c.Body!.Value.Top); Assert.Equal(0, c.Body.Value.Height); });
        var insufficient = source.Query(new(geometryBudget: 1));
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, insufficient.BudgetStatus);
        Assert.Equal(2, insufficient.RequiredCandleCount); Assert.Empty(insufficient.Candles);
        Assert.Null(PriceChartGeometry.GetHit(insufficient, 0));
        foreach (var options in new[] { new PriceChartOptions(barCount: 0), new PriceChartOptions(barStart: int.MaxValue) })
        {
            var empty = source.Query(options); Assert.Null(empty.Domain); Assert.Empty(empty.Candles); Assert.Equal(0, empty.BarCount);
        }
        var fixedConstant = Source(Bar(0)).Query(new(fixedDomain: new(1, 1)));
        Assert.Null(Assert.Single(fixedConstant.Candles).Body);
        Assert.Equal(.5, fixedConstant.Candles[0].Wick!.Value.Start.Y);
        Assert.Equal(.5, fixedConstant.Candles[0].Wick!.Value.End.Y);
    }

    [Fact]
    public void ExtremeSubnormalAndAdjacentValuesRemainFiniteWithoutMinimumWicks()
    {
        foreach (var pair in new[] { (-double.MaxValue, double.MaxValue), (0d, double.Epsilon),
            (double.MaxValue / 2, double.MaxValue), (1d, Math.BitIncrement(1d)) })
        {
            var snapshot = Source(Bar(0, pair.Item1, pair.Item2, pair.Item1, pair.Item2)).Query();
            var candle = Assert.Single(snapshot.Candles);
            Assert.Equal(0, candle.Wick!.Value.Start.Y); Assert.Equal(1, candle.Wick.Value.End.Y);
            Assert.Equal(1, candle.Body!.Value.Height);
            Assert.All(snapshot.Ticks, tick => Assert.True(double.IsFinite(tick.Value) && double.IsFinite(tick.Position)));
        }
        var nearTop = Source(Bar(0, -1e-300, 0, -1, -1e-300)).Query();
        Assert.Equal(1e-300, nearTop.Candles[0].Body!.Value.Top);
        var tiny = Source(Bar(0, 0, double.Epsilon, 0, 0)).Query(new(fixedDomain: new(-double.MaxValue, double.MaxValue)));
        Assert.Equal(tiny.Candles[0].Wick!.Value.Start.Y, tiny.Candles[0].Wick!.Value.End.Y);
        Assert.Equal(0, tiny.Candles[0].Body!.Value.Height);
    }

    [Fact]
    public void HitToleranceOverlapAndKeyboardUseDeterministicRawIdentity()
    {
        var snapshot = Source(Bar(0), Bar(1)).Query();
        Assert.Equal(1, PriceChartGeometry.HitTest(snapshot, .5, .5, .5)!.Value.Key);
        Assert.Null(PriceChartGeometry.HitTest(snapshot, -.001, .5, 1));
        Assert.Null(PriceChartGeometry.GetHit(snapshot, -1)); Assert.Null(PriceChartGeometry.GetHit(snapshot, 2));
        Assert.Equal(0, PriceChartGeometry.GetHit(snapshot, 0)!.Value.Key);
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceChartGeometry.HitTest(snapshot, double.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceChartGeometry.HitTest(snapshot, 0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceChartGeometry.HitTest(snapshot, 0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriceChartGeometry.HitTest(snapshot, 0, 0, 0, 1.1));
    }

    [Fact]
    public void AdmissionAndOptionsAreBoundedAndOverlimitIteratorIsDisposed()
    {
        var advances = 0; var disposed = false;
        IEnumerable<PriceBar> Infinite()
        {
            try { while (true) { advances++; yield return Bar(0); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new PriceChartSource("s", "", Infinite()));
        Assert.Equal(PriceChartSource.MaximumBars + 1, advances); Assert.True(disposed);
        Assert.Equal(PriceChartSource.MaximumBars, new PriceChartSource("s", "", Enumerable.Range(0, PriceChartSource.MaximumBars).Select(i => Bar(i))).Query().Candles.Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceChartOptions(barStart: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceChartOptions(barCount: PriceChartSource.MaximumBars + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceChartOptions(geometryBudget: PriceChartSource.MaximumBars + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceChartOptions(bodyWidthFraction: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceChartOptions(bodyWidthFraction: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceChartOptions(bodyWidthFraction: 1.01));
        Assert.Throws<ArgumentException>(() => new PriceChartSource(" ", "", []));
        Assert.Throws<ArgumentException>(() => new PriceChartSource("s", new string('a', 257), []));
        Assert.Throws<ArgumentException>(() => new PriceChartSource("s", "\ud800", []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceChartSource("s", "", [], datasetVersion: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PriceChartSource("s", "", [], sourceVersion: -1));
    }
}
