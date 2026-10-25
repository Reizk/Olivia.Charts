using System.Globalization;
using System.Numerics;

namespace Olivia.Core.Editing;
public enum SparklineMode { Line, Area, Bar, WinLoss }
public readonly record struct SparklinePoint(double X, double Y);
public sealed record SparklineGeometry(IReadOnlyList<SparklinePoint> Points, double Baseline, double BarWidth);

/// <summary>Bounded original data; exact sum and immutable input, separate approximate render coordinates.</summary>
public sealed class SparklineSnapshot
{
    public IReadOnlyList<decimal> Values { get; }
    public int Count => Values.Count;
    public decimal? Minimum { get; }
    public decimal? Maximum { get; }
    public string SumExact { get; }
    public int Positive { get; }
    public int Negative { get; }
    public int Zero => Count - Positive - Negative;
    public SparklineSnapshot(IEnumerable<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new List<decimal>(); BigInteger sum = 0;
        foreach (var value in values)
        {
            if (copy.Count == 32 || value < -1000000 || value > 1000000) throw new ArgumentException("Require at most32 values in [-1000000,1000000].", nameof(values));
            copy.Add(value); var bits = decimal.GetBits(value);
            var coefficient = (BigInteger)(uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
            sum += ((bits[3] & int.MinValue) == 0 ? coefficient : -coefficient) * BigInteger.Pow(10, 28 - ((bits[3] >> 16) & 255));
        }
        Values = copy.AsReadOnly(); Minimum = Count == 0 ? null : copy.Min(); Maximum = Count == 0 ? null : copy.Max();
        Positive = copy.Count(x => x > 0); Negative = copy.Count(x => x < 0);
        var digits = BigInteger.Abs(sum).ToString(CultureInfo.InvariantCulture).PadLeft(29, '0');
        SumExact = (sum.Sign < 0 ? "-" : "") + (digits[..^28] + "." + digits[^28..]).TrimEnd('0').TrimEnd('.');
    }
    public SparklineGeometry Geometry(SparklineMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var low = (double)Math.Min(0, Minimum ?? 0); var high = (double)Math.Max(0, Maximum ?? 0);
        var baseline = mode == SparklineMode.WinLoss || high == low ? .5 : high / (high - low);
        var points = Values.Select((v, i) => new SparklinePoint(
            mode is SparklineMode.Bar or SparklineMode.WinLoss ? (i + .5) / Count : Count == 1 ? .5 : (double)i / (Count - 1),
            mode == SparklineMode.WinLoss ? v > 0 ? 0 : v < 0 ? 1 : .5 : high == low ? .5 : (high - (double)v) / (high - low))).ToArray();
        return new(Array.AsReadOnly(points), baseline, Count == 0 ? 0 : .7 / Count);
    }
    public bool SameContent(SparklineSnapshot other) => other is not null && Values.SequenceEqual(other.Values);
}
