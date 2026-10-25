using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace Olivia.Core.Charts;

public enum ChartBindingDimension { Company, Product, Month }
public enum ChartBindingValue { Income, Revenue }
public enum ChartBindingFunction { Sum, Average, Min, Max }
public sealed record ChartBindingMember(string Id, string Label);
public sealed record ChartBindingRow(long RowId, ChartBindingMember Company, ChartBindingMember Product,
    int Month, decimal? Income, decimal? Revenue);
public sealed record ChartBindingRequest(ChartBindingDimension Series, ChartBindingDimension Argument,
    ChartBindingValue Value, ChartBindingFunction Function)
{
    public static ChartBindingRequest Default { get; } = new(ChartBindingDimension.Company,
        ChartBindingDimension.Product, ChartBindingValue.Revenue, ChartBindingFunction.Sum);
    public void Validate()
    {
        if (!Enum.IsDefined(Series) || !Enum.IsDefined(Argument) || !Enum.IsDefined(Value) || !Enum.IsDefined(Function))
            throw new ArgumentOutOfRangeException(nameof(Series), "Unknown typed binding choice.");
        if (Series == Argument) throw new ArgumentException("Series and argument must use different fields.");
    }
}

/// <summary>Owns a copied, bounded typed row snapshot. Amounts have at most four fractional digits,
/// absolute magnitude at most one million; sum of 4096 rows is exactly representable in decimal.</summary>
public sealed class TypedChartBindingSource
{
    public const int MaximumRows = 4096;
    public const decimal MaximumAmount = 1_000_000m;
    public TypedChartBindingSource(IEnumerable<ChartBindingRow> rows, long version = 1)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        var copy = rows.Take(MaximumRows + 1).ToImmutableArray();
        if (copy.Length > MaximumRows) throw new ArgumentException("Row limit is 4096; no truncation.", nameof(rows));
        var ids = new HashSet<long>();
        var companies = new Dictionary<string, string>(StringComparer.Ordinal);
        var products = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in copy)
        {
            ArgumentNullException.ThrowIfNull(row);
            if (!ids.Add(row.RowId)) throw new ArgumentException("Duplicate row identity.", nameof(rows));
            ValidateMember(row.Company, companies); ValidateMember(row.Product, products);
            if (row.Month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(rows), "Month must be 1 through 12.");
            ValidateAmount(row.Income); ValidateAmount(row.Revenue);
        }
        Rows = copy; Version = version;
    }
    public ImmutableArray<ChartBindingRow> Rows { get; }
    public long Version { get; }
    private static void ValidateMember(ChartBindingMember member, Dictionary<string, string> labels)
    {
        ArgumentNullException.ThrowIfNull(member);
        BarChartSource.ValidateText(member.Id, nameof(member), identity: true);
        BarChartSource.ValidateText(member.Label, nameof(member));
        if (member.Id.Length > 64 || member.Label.Length > 128) throw new ArgumentException("Member ID/label exceeds 64/128 UTF16 units.");
        if (labels.TryGetValue(member.Id, out var label) && !StringComparer.Ordinal.Equals(label, member.Label))
            throw new ArgumentException("One member ID must have one label in a source.");
        labels[member.Id] = member.Label;
    }
    private static void ValidateAmount(decimal? value)
    {
        if (value is { } amount && (amount < -MaximumAmount || amount > MaximumAmount
            || decimal.Round(amount, 4, MidpointRounding.ToEven) != amount))
            throw new ArgumentOutOfRangeException(nameof(value), "Amount must be within +/-1,000,000 with at most four fractional digits.");
    }
}

/// <summary>Bucket key is local to its publication, never a raw row ID. Sum is exact for numeric rows;
/// all-null buckets have null Sum/Value. Average has an explicit six-place ToEven policy.
/// DisplayValue is binary64 geometry only; Value remains the authoritative decimal.</summary>
public sealed record ChartBindingBucket(long Key, string SeriesId, string SeriesLabel,
    string CategoryKey, string CategoryLabel, ImmutableArray<long> RowIds, int NumericCount,
    decimal? Sum, decimal? Value, bool AverageRounded, double? DisplayValue, bool DisplayRounded)
{
    public int RowCount => RowIds.Length;
}
public sealed record TypedChartBindingResult(TypedChartBindingSource Source, ChartBindingRequest Request,
    ImmutableArray<ChartBindingBucket> Buckets, BarChartSource Bars);

/// <summary>Original finite field binding. No reflection, vendor types, expressions or viewport aggregation.
/// Present buckets only; absent cross-products never become zero. Ordering uses ordinal member IDs
/// (months have two-digit IDs), independently of display labels and incoming row order.</summary>
public static class TypedChartBinding
{
    public static TypedChartBindingResult Build(TypedChartBindingSource source, ChartBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(request); request.Validate();
        var series = new SortedDictionary<string, ChartBindingMember>(StringComparer.Ordinal);
        var categories = new SortedDictionary<string, ChartBindingMember>(StringComparer.Ordinal);
        var groups = new Dictionary<(string Series, string Category), List<ChartBindingRow>>();
        foreach (var row in source.Rows)
        {
            var s = Member(row, request.Series); var c = Member(row, request.Argument);
            var sid = Identity(request.Series, s.Id); var cid = Identity(request.Argument, c.Id);
            series[sid] = s; categories[cid] = c;
            if (series.Count > BarChartSource.MaximumSeries || categories.Count > BarChartSource.MaximumCategories)
                throw new ArgumentException("Binding exceeds 8 series or 256 categories; no truncation.");
            if (!groups.TryGetValue((sid, cid), out var group))
            {
                if (groups.Count == BarChartSource.MaximumRawEntries) throw new ArgumentException("Binding exceeds 2048 buckets.");
                groups.Add((sid, cid), group = []);
            }
            group.Add(row);
        }
        var buckets = ImmutableArray.CreateBuilder<ChartBindingBucket>();
        foreach (var (sid, s) in series)
        foreach (var (cid, c) in categories)
        {
            if (!groups.TryGetValue((sid, cid), out var rows)) continue;
            var values = rows.Select(r => request.Value == ChartBindingValue.Income ? r.Income : r.Revenue)
                .Where(v => v.HasValue).Select(v => v!.Value).ToArray();
            decimal? sum = values.Length == 0 ? null : values.Sum();
            decimal? value = values.Length == 0 ? null : request.Function switch
            {
                ChartBindingFunction.Sum => sum,
                ChartBindingFunction.Average => decimal.Round(sum!.Value / values.Length, 6, MidpointRounding.ToEven),
                ChartBindingFunction.Min => values.Min(),
                ChartBindingFunction.Max => values.Max(),
                _ => throw new InvalidOperationException("Validated function expected.")
            };
            var averageRounded = request.Function == ChartBindingFunction.Average && value.HasValue
                && value.Value * values.Length != sum!.Value;
            var display = value is { } amount ? (double?)amount : null;
            buckets.Add(new(buckets.Count, sid, s.Label, cid, c.Label, rows.Select(r => r.RowId).Order().ToImmutableArray(),
                values.Length, sum, value, averageRounded, display,
                value is { } exact && !IsExactBinary64(exact, display!.Value)));
        }
        var frozen = buckets.ToImmutable();
        var bars = new BarChartSource(categories.Select(c => new BarCategory(c.Key, c.Value.Label)),
            series.Select(s => new BarSeries(s.Key, s.Value.Label,
                frozen.Where(b => b.SeriesId == s.Key).Select(b => new BarSample(b.Key, b.CategoryKey, b.DisplayValue)), source.Version)), source.Version);
        return new(source, request, frozen, bars);
    }
    private static ChartBindingMember Member(ChartBindingRow row, ChartBindingDimension dimension) => dimension switch
    {
        ChartBindingDimension.Company => row.Company,
        ChartBindingDimension.Product => row.Product,
        ChartBindingDimension.Month => new(row.Month.ToString("D2", CultureInfo.InvariantCulture), $"Month {row.Month.ToString("D2", CultureInfo.InvariantCulture)}"),
        _ => throw new ArgumentOutOfRangeException(nameof(dimension))
    };
    // The dimension prefix and full ID are sufficient: each identity has exactly one member.
    private static string Identity(ChartBindingDimension dimension, string id) => $"{dimension}:{id}";
    private static bool IsExactBinary64(decimal value, double display)
    {
        var d = decimal.GetBits(value);
        var coefficient = new BigInteger((uint)d[0]) + (new BigInteger((uint)d[1]) << 32) + (new BigInteger((uint)d[2]) << 64);
        if (d[3] < 0) coefficient = -coefficient;
        var denominator = BigInteger.Pow(10, (d[3] >> 16) & 255);
        var bits = BitConverter.DoubleToInt64Bits(display);
        var exponentBits = (int)((bits >> 52) & 2047);
        BigInteger binary = bits & 0x000fffffffffffffL;
        var exponent = exponentBits == 0 ? -1074 : exponentBits - 1023 - 52;
        if (exponentBits != 0) binary += BigInteger.One << 52;
        if (bits < 0) binary = -binary;
        return exponent >= 0 ? coefficient == (binary << exponent) * denominator
            : (coefficient << -exponent) == binary * denominator;
    }
}
