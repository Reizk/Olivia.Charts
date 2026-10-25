using Olivia.Core.Charts;
using Olivia.Core.Workspaces;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class TypedChartBindingTests
{
    private static ChartBindingRow Row(long id, decimal? income, decimal? revenue = null, string company = "c", string product = "p", int month = 1) =>
        new(id, new(company, company), new(product, product), month, income, revenue);
    private static ChartBindingRequest Request(ChartBindingFunction function = ChartBindingFunction.Sum) =>
        new(ChartBindingDimension.Company, ChartBindingDimension.Product, ChartBindingValue.Income, function);

    [Fact]
    public void Four_functions_nulls_zero_and_binary64_display_keep_exact_decimal_provenance()
    {
        var source = new TypedChartBindingSource([Row(3, null), Row(2, 20m), Row(1, 10m), Row(4, null, product: "all-null")]);
        foreach (var (function, value) in new[] { (ChartBindingFunction.Sum, 30m), (ChartBindingFunction.Average, 15m), (ChartBindingFunction.Min, 10m), (ChartBindingFunction.Max, 20m) })
        {
            var result = TypedChartBinding.Build(source, Request(function));
            var bucket = result.Buckets.Single(b => b.CategoryKey == "Product:p");
            Assert.Equal(value, bucket.Value); Assert.Equal(30m, bucket.Sum);
            Assert.Equal(new long[] { 1, 2, 3 }, bucket.RowIds); Assert.Equal(3, bucket.RowCount); Assert.Equal(2, bucket.NumericCount);
            Assert.False(bucket.DisplayRounded); Assert.False(bucket.AverageRounded);
            var gap = result.Buckets.Single(b => b.CategoryKey == "Product:all-null");
            Assert.Null(gap.Sum); Assert.Null(gap.Value); Assert.Null(gap.DisplayValue); Assert.Equal(0, gap.NumericCount);
            Assert.Single(result.Bars.Query().Gaps); Assert.Single(result.Bars.Query().Bars);
        }
        var decimalResult = TypedChartBinding.Build(new([Row(1, .1m), Row(2, .2m)]), Request());
        Assert.Equal(.3m, decimalResult.Buckets[0].Value); Assert.True(decimalResult.Buckets[0].DisplayRounded);
        var zero = TypedChartBinding.Build(new([Row(1, 0m), Row(2, null)]), Request(ChartBindingFunction.Average));
        Assert.Equal(1, zero.Buckets[0].NumericCount); Assert.Equal(0m, zero.Buckets[0].Value);
        Assert.Single(zero.Bars.Query().Bars); Assert.Equal(0d, zero.Bars.Query().Bars[0].Height);
    }

    [Theory]
    [InlineData(1, 8, "0.000012")]
    [InlineData(3, 8, "0.000038")]
    [InlineData(-1, 8, "-0.000012")]
    [InlineData(-3, 8, "-0.000038")]
    [InlineData(1, 3, "0.000033")]
    public void Average_has_explicit_six_place_signed_ToEven_policy(int units, int count, string expected)
    {
        var rows = Enumerable.Range(0, count).Select(i => Row(i, i == 0 ? units / 10000m : 0m));
        var b = TypedChartBinding.Build(new(rows), Request(ChartBindingFunction.Average)).Buckets[0];
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), b.Value);
        Assert.True(b.AverageRounded); Assert.Equal(units / 10000m, b.Sum); Assert.Equal(count, b.NumericCount);
    }

    [Fact]
    public void Fields_change_grouping_and_measure_without_cross_product_zeros_or_label_identity_collisions()
    {
        var rows = new[] { Row(1, 10, 100, "a", "x", 10), Row(2, 20, 200, "b", "y", 2), Row(3, null, 300, "a", "x", 1) }
            .Select(r => r with { Company = new(r.Company.Id, "Same"), Product = new(r.Product.Id, "Same") }).ToArray();
        var source = new TypedChartBindingSource(rows);
        var first = TypedChartBinding.Build(source, Request());
        Assert.Equal(2, first.Bars.Series.Length); Assert.Equal(2, first.Bars.Categories.Length); Assert.Equal(2, first.Buckets.Length);
        Assert.Equal(2, first.Bars.Query().Bars.Length);
        var request = new ChartBindingRequest(ChartBindingDimension.Product, ChartBindingDimension.Month, ChartBindingValue.Revenue, ChartBindingFunction.Sum);
        var second = TypedChartBinding.Build(source, request);
        Assert.Equal(new[] { "Month:01", "Month:02", "Month:10" }, second.Bars.Categories.Select(c => c.Key));
        Assert.Equal(3, second.Buckets.Length); Assert.Equal(600m, second.Buckets.Sum(b => b.Value));
        var reversed = TypedChartBinding.Build(new(rows.Reverse()), request);
        Assert.Equal(second.Buckets.Select(b => (b.SeriesId, b.CategoryKey, b.Value)), reversed.Buckets.Select(b => (b.SeriesId, b.CategoryKey, b.Value)));
        Assert.Equal(second.Buckets.Select(b => string.Join(",", b.RowIds)), reversed.Buckets.Select(b => string.Join(",", b.RowIds)));
    }

    [Fact]
    public void Admission_copies_rows_and_rejects_invalid_identity_amounts_and_configuration()
    {
        var rows = new List<ChartBindingRow> { Row(long.MinValue, -1_000_000m), Row(long.MaxValue, 1_000_000m) };
        var source = new TypedChartBindingSource(rows, long.MaxValue); rows.Clear();
        Assert.Equal(2, source.Rows.Length); Assert.Equal(0m, TypedChartBinding.Build(source, Request()).Buckets[0].Sum);
        Assert.Throws<ArgumentNullException>(() => new TypedChartBindingSource(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypedChartBindingSource([], -1));
        Assert.Throws<ArgumentNullException>(() => new TypedChartBindingSource([null!]));
        Assert.Throws<ArgumentException>(() => new TypedChartBindingSource([Row(1, 1), Row(1, 2)]));
        Assert.Throws<ArgumentException>(() => new TypedChartBindingSource([Row(1, 1), Row(2, 1) with { Company = new("c", "other") }]));
        Assert.ThrowsAny<ArgumentException>(() => new TypedChartBindingSource([Row(1, 1, company: " ")]));
        Assert.ThrowsAny<ArgumentException>(() => new TypedChartBindingSource([Row(1, 1) with { Product = new("x", "\ud800") }]));
        Assert.ThrowsAny<ArgumentException>(() => new TypedChartBindingSource([Row(1, 1) with { Company = null! }]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypedChartBindingSource([Row(1, 1.00001m)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypedChartBindingSource([Row(1, 1_000_000.0001m)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypedChartBindingSource([Row(1, decimal.MinValue)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TypedChartBindingSource([Row(1, 1, month: 13)]));
        Assert.Throws<ArgumentException>(() => TypedChartBinding.Build(source, Request() with { Argument = ChartBindingDimension.Company }));
        Assert.Throws<ArgumentOutOfRangeException>(() => TypedChartBinding.Build(source, Request() with { Function = (ChartBindingFunction)99 }));
    }

    [Fact]
    public void All_budgets_are_bounded_and_maximum_exact_sum_is_not_multiseries_magnitude_limited()
    {
        var full = new TypedChartBindingSource(Enumerable.Range(0, 4096).Select(i => Row(i, 1_000_000m)));
        Assert.Equal(4_096_000_000m, TypedChartBinding.Build(full, Request()).Buckets[0].Value);
        var moved = 0;
        IEnumerable<ChartBindingRow> Endless() { while (true) { moved++; yield return Row(moved, 1); } }
        Assert.Throws<ArgumentException>(() => new TypedChartBindingSource(Endless())); Assert.Equal(4097, moved);
        var maximum = new TypedChartBindingSource(Enumerable.Range(0, 4096).Select(i => Row(i, 1, company: (i % 8).ToString(), product: (i / 8 % 256).ToString())));
        var result = TypedChartBinding.Build(maximum, Request());
        Assert.Equal(8, result.Bars.Series.Length); Assert.Equal(256, result.Bars.Categories.Length); Assert.Equal(2048, result.Buckets.Length);
        Assert.Equal(2048, result.Bars.Query().Bars.Length);
        Assert.Throws<ArgumentException>(() => TypedChartBinding.Build(new(Enumerable.Range(0, 9).Select(i => Row(i, 1, company: i.ToString()))), Request()));
        Assert.Throws<ArgumentException>(() => TypedChartBinding.Build(new(Enumerable.Range(0, 257).Select(i => Row(i, 1, product: i.ToString()))), Request()));
        Assert.Throws<ArgumentException>(() => TypedChartBinding.Build(new(Enumerable.Range(1, 12).Select(i => Row(i, 1, month: i))), Request() with { Series = ChartBindingDimension.Month }));
        var empty = TypedChartBinding.Build(new([]), Request());
        Assert.Empty(empty.Buckets); Assert.Empty(empty.Bars.Series); Assert.Empty(empty.Bars.Categories); Assert.Null(empty.Bars.Query().Domain);
    }

    [Fact]
    public void Session_atomic_failure_noop_same_version_replacement_and_stale_selection_are_explicit()
    {
        var source = new TypedChartBindingSource([Row(1, 10), Row(2, null, product: "null")], 7);
        using var session = new TypedChartBindingSession(source, Request());
        var p = session.Publication; var events = 0; session.Changed += (_, _) => events++;
        Assert.True(session.Select(0, p)); Assert.NotNull(session.SelectedBucket);
        Assert.False(session.Apply(Request())); Assert.False(session.ReplaceSource(source)); Assert.Equal(1, events); Assert.Same(p, session.Publication);
        Assert.Throws<ArgumentException>(() => session.Apply(Request() with { Series = ChartBindingDimension.Product }));
        Assert.Throws<ArgumentException>(() => session.ReplaceSource(new(Enumerable.Range(0, 9).Select(i => Row(i, 1, company: i.ToString())))));
        Assert.Same(p, session.Publication); Assert.NotNull(session.SelectedBucket); Assert.Equal(1, events);
        Assert.True(session.ReplaceSource(new([Row(1, 20)], 7))); Assert.Null(session.SelectedBucket);
        Assert.Equal(7, session.Publication.Result.Source.Version); Assert.False(session.Select(0, p));
        Assert.Equal(20m, session.Publication.Result.Buckets[0].Value);
        using var other = new TypedChartBindingSession(source, Request());
        Assert.False(session.Select(0, other.Publication));
        bool? nestedResult = null;
        session.Changed += (_, _) => { nestedResult = session.Apply(Request(ChartBindingFunction.Min)); throw new InvalidOperationException("Observer failure"); };
        Assert.True(session.Apply(Request(ChartBindingFunction.Max))); Assert.Equal(false, nestedResult);
        Assert.Equal(ChartBindingFunction.Max, session.Publication.Result.Request.Function);
        Assert.Equal(1, session.CallbackFailureCount);
        session.Dispose(); Assert.False(session.Apply(Request())); Assert.False(session.Select(0, session.Publication));
    }
}
