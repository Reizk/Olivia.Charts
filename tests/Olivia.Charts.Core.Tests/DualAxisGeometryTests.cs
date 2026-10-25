using System.Numerics;
using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class DualAxisGeometryTests
{
    [Fact]
    public void NormalizationAndTicksCoverEmptyConstantSubnormalAndExtremeDomains()
    {
        Assert.Empty(DualAxisChartGeometry.CreateTicks(null));
        foreach (var value in new[] { 0d, -0d, double.Epsilon, -double.Epsilon, double.MaxValue, -double.MaxValue })
        {
            var domain = new HistoryValueDomain(value, value);
            Assert.Equal(0.5, DualAxisChartGeometry.NormalizeY(value, domain));
            Assert.Equal(new HistoryAxisTick(value, 0.5), Assert.Single(DualAxisChartGeometry.CreateTicks(domain)));
        }
        var extremes = new HistoryValueDomain(-double.MaxValue, double.MaxValue);
        Assert.Equal(0, DualAxisChartGeometry.NormalizeY(-double.MaxValue, extremes));
        Assert.Equal(0.5, DualAxisChartGeometry.NormalizeY(0, extremes));
        Assert.Equal(1, DualAxisChartGeometry.NormalizeY(double.MaxValue, extremes));
        Assert.Equal(0.25, DualAxisChartGeometry.NormalizeY(-double.MaxValue / 2, extremes));
        var subnormal = new HistoryValueDomain(-double.Epsilon, double.Epsilon);
        Assert.Equal(0.5, DualAxisChartGeometry.NormalizeY(0, subnormal));
        var adjacent = new HistoryValueDomain(Math.BitDecrement(double.MaxValue), double.MaxValue);
        var ticks = DualAxisChartGeometry.CreateTicks(adjacent, 256);
        Assert.Equal(2, ticks.Length);
        Assert.Equal(adjacent.Minimum, ticks[0].Value);
        Assert.Equal(adjacent.Maximum, ticks[1].Value);
        Assert.Equal(0, ticks[0].Position);
        Assert.Equal(1, ticks[1].Position);
    }

    [Fact]
    public void SeededFiniteNormalizationMatchesIndependentExactIeee754IntegerOracle()
    {
        var random = new Random(57041);
        var fixtures = new List<(double Minimum, double Maximum)>
        {
            (-double.MaxValue, double.MaxValue), (-double.MaxValue, double.MaxValue / 4),
            (Math.BitDecrement(double.MaxValue), double.MaxValue),
            (-double.MaxValue, Math.BitIncrement(-double.MaxValue)),
            (-double.Epsilon, double.Epsilon), (double.Epsilon, double.Epsilon * 100),
            (-10, 20), (0, 1), (-1, 0)
        };
        for (var trial = 0; trial < 500; trial++)
        {
            var first = Finite(random);
            var second = Finite(random);
            fixtures.Add((Math.Min(first, second), Math.Max(first, second)));
        }
        foreach (var (minimum, maximum) in fixtures)
        {
            var domain = new HistoryValueDomain(minimum, maximum);
            var values = new[] { minimum, maximum, minimum / 2 + maximum / 2, 0, Finite(random),
                Math.BitIncrement(minimum), Math.BitDecrement(maximum) }.Where(double.IsFinite);
            foreach (var value in values)
            {
                var expected = ExactRatioOracle(value, minimum, maximum);
                var actual = DualAxisChartGeometry.NormalizeY(value, domain);
                Assert.True(double.IsFinite(actual));
                Assert.InRange(actual, 0, 1);
                Assert.InRange(Math.Abs(actual - expected), 0, 4e-15);
            }
            var ticks = DualAxisChartGeometry.CreateTicks(domain, 17);
            Assert.InRange(ticks.Length, 1, 17);
            Assert.Equal(minimum, ticks[0].Value);
            Assert.Equal(maximum, ticks[^1].Value);
            Assert.All(ticks, tick =>
            {
                Assert.True(double.IsFinite(tick.Value));
                Assert.InRange(tick.Value, minimum, maximum);
                Assert.InRange(Math.Abs(tick.Position - ExactRatioOracle(tick.Value, minimum, maximum)), 0, 4e-15);
            });
            Assert.Equal(ticks.Length, ticks.Select(tick => tick.Value).Distinct().Count());
            for (var index = 1; index < ticks.Length; index++)
            {
                Assert.True(ticks[index].Value > ticks[index - 1].Value);
                Assert.True(ticks[index].Position >= ticks[index - 1].Position);
            }
        }
    }

    [Fact]
    public void SharedUtcProjectionKeepsTickPrecisionAndScalesUnitsIndependently()
    {
        var window = new TimeWindow(TimeWindow.MaximumExclusiveTicks - 100, TimeWindow.MaximumExclusiveTicks);
        Assert.Equal(0.01, DualAxisChartGeometry.NormalizeX(window.StartTicks + 1, window));
        Assert.Equal(0.99, DualAxisChartGeometry.NormalizeX(window.EndTicks - 1, window));
        Assert.Equal(1, DualAxisChartGeometry.NormalizeX(window.EndTicks, window));
        Assert.Equal(0, DualAxisChartGeometry.NormalizeX(0, window));
        Assert.Equal(0.5, DualAxisChartGeometry.NormalizeX(0, default));
        var temperature = DualAxisChartGeometry.Project(window.StartTicks + 50, 50, window,
            new HistoryValueDomain(0, 100), 10, 20, 600, 400);
        var pressure = DualAxisChartGeometry.Project(window.StartTicks + 50, 1500, window,
            new HistoryValueDomain(1000, 2000), 10, 20, 600, 400);
        Assert.Equal(new HistoryChartPoint(310, 220), temperature);
        Assert.Equal(temperature, pressure);
        var extremes = DualAxisChartGeometry.Project(window.StartTicks, double.MaxValue, window,
            new HistoryValueDomain(-double.MaxValue, double.MaxValue), 10, 20, 600, 400);
        Assert.Equal(new HistoryChartPoint(10, 20), extremes);
        Assert.Equal(0, DualAxisChartGeometry.NormalizeY(-double.MaxValue, new HistoryValueDomain(10, 20)));
        Assert.Equal(1, DualAxisChartGeometry.NormalizeY(double.MaxValue, new HistoryValueDomain(10, 20)));
    }

    [Fact]
    public void ClipSegmentPreservesTrueCrossingsRatherThanClampingOriginalVertices()
    {
        var line = Clip(new HistorySample(1, 0, -100), new HistorySample(2, 100, 100), new HistoryValueDomain(0, 50));
        Assert.NotNull(line);
        Assert.Equal(new HistoryChartPoint(50, 100), line.Value.Start);
        Assert.Equal(new HistoryChartPoint(75, 0), line.Value.End);
        Assert.Null(Clip(new HistorySample(1, 0, 200), new HistorySample(2, 100, 100), new HistoryValueDomain(0, 50)));
        Assert.Null(Clip(new HistorySample(1, 0, null), new HistorySample(2, 100, 10), new HistoryValueDomain(0, 50)));
        Assert.Null(Clip(new HistorySample(1, 0, double.NaN), new HistorySample(2, 100, 10), new HistoryValueDomain(0, 50)));
        var constant = Clip(new HistorySample(1, 0, -100), new HistorySample(2, 100, 100), new HistoryValueDomain(0, 0));
        Assert.Equal(new HistoryChartPoint(50, 50), constant!.Value.Start);
        Assert.Equal(constant.Value.Start, constant.Value.End);
        Assert.Null(Clip(new HistorySample(1, 0, 20), new HistorySample(2, 100, 10), new HistoryValueDomain(0, 0)));
    }

    [Fact]
    public void ClipExtremeCrossingRetainsBothYBoundariesEvenWhenTimeFractionsCoincide()
    {
        var domain = new HistoryValueDomain(-1, 1);
        var first = new HistorySample(1, 0, -double.MaxValue);
        var second = new HistorySample(2, 100, double.MaxValue);
        var line = Clip(first, second, domain)!.Value;
        Assert.Equal(new HistoryChartPoint(50, 100), line.Start);
        Assert.Equal(new HistoryChartPoint(50, 0), line.End);
        var reverse = Clip(second, first, domain)!.Value;
        Assert.Equal(line.End, reverse.Start);
        Assert.Equal(line.Start, reverse.End);
        var full = Clip(first, second, new HistoryValueDomain(-double.MaxValue, double.MaxValue))!.Value;
        Assert.Equal(new HistoryChartPoint(0, 100), full.Start);
        Assert.Equal(new HistoryChartPoint(100, 0), full.End);
    }

    [Fact]
    public void SeededLineClippingMatchesIndependentDecimalBoundaryIntersectionOracle()
    {
        var random = new Random(34381);
        var window = new TimeWindow(100, 900);
        var domain = new HistoryValueDomain(-50, 80);
        for (var trial = 0; trial < 1200; trial++)
        {
            var first = new HistorySample(1, random.Next(100, 900), random.Next(-1000, 1001));
            var second = new HistorySample(2, random.Next(100, 900), random.Next(-1000, 1001));
            var expected = DecimalClipOracle(first, second, window, domain);
            var actual = DualAxisChartGeometry.ClipSegment(first, second, window, domain, 10, 20, 600, 400);
            Assert.Equal(expected.HasValue, actual.HasValue);
            if (actual.HasValue)
            {
                Assert.InRange(Math.Abs(actual.Value.Start.X - expected!.Value.Start.X), 0, 1e-9);
                Assert.InRange(Math.Abs(actual.Value.Start.Y - expected.Value.Start.Y), 0, 1e-9);
                Assert.InRange(Math.Abs(actual.Value.End.X - expected.Value.End.X), 0, 1e-9);
                Assert.InRange(Math.Abs(actual.Value.End.Y - expected.Value.End.Y), 0, 1e-9);
                foreach (var point in new[] { actual.Value.Start, actual.Value.End })
                {
                    Assert.InRange(point.X, 10, 610);
                    Assert.InRange(point.Y, 20, 420);
                }
            }
        }
    }

    [Fact]
    public void ClipRejectsOutsideXNeighborsIncludingRoundedExtremeBoundaryCounterexample()
    {
        var first = new HistorySample(1, 0, -double.MaxValue);
        var second = new HistorySample(2, 1000000000000000000, double.MaxValue);
        var window = new TimeWindow(500000000000000001, 1000000000000000001);
        Assert.Null(DualAxisChartGeometry.ClipSegment(first, second, window,
            new HistoryValueDomain(-1, 1), 0, 0, 100, 100));
        Assert.Null(DualAxisChartGeometry.ClipSegment(new HistorySample(1, 0, 0), new HistorySample(2, 10, 1),
            new TimeWindow(0, 10), new HistoryValueDomain(0, 1), 0, 0, 100, 100));
    }

    [Fact]
    public void GeometryRejectsInvalidFiniteInputsAndBoundsItsTickWork()
    {
        var domain = new HistoryValueDomain(0, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.NormalizeY(double.NaN, domain));
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.NormalizeY(double.PositiveInfinity, domain));
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.NormalizeX(-1, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.NormalizeX(long.MaxValue, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.CreateTicks(domain, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.CreateTicks(domain, 257));
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.Project(0, 0, default, domain, 0, 0, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.Project(0, 0, default, domain, 0, 0, 1, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => DualAxisChartGeometry.Project(0, 0, default, domain, double.MaxValue, 0, double.MaxValue, 1));
        Assert.Null(DualAxisChartGeometry.ClipSegment(new HistorySample(0, 0, 0), new HistorySample(1, 1, 1),
            default, domain, 0, 0, 100, 100));
    }

    private static HistoryChartLine? Clip(HistorySample first, HistorySample second, HistoryValueDomain domain) =>
        DualAxisChartGeometry.ClipSegment(first, second, new TimeWindow(0, 200), domain, 0, 0, 200, 100);

    private static double Finite(Random random)
    {
        while (true)
        {
            var bits = random.NextInt64();
            if (random.Next(2) == 0) bits |= long.MinValue;
            var value = BitConverter.Int64BitsToDouble(bits);
            if (double.IsFinite(value)) return value;
        }
    }

    // Exact binary integer subtraction and a 28-decimal-digit rational quotient are independent
    // of production floating-point subtraction/scaling and expose overflow/underflow errors.
    private static double ExactRatioOracle(double value, double minimum, double maximum)
    {
        if (minimum == maximum) return 0.5;
        if (value <= minimum) return 0;
        if (value >= maximum) return 1;
        static (BigInteger Integer, int Exponent) Decode(double number)
        {
            var bits = BitConverter.DoubleToInt64Bits(number);
            var exponent = (int)((bits >> 52) & 0x7ff);
            var coefficient = new BigInteger(bits & 0xfffffffffffffL);
            if (exponent != 0) coefficient += BigInteger.One << 52;
            if (bits < 0) coefficient = -coefficient;
            return (coefficient, exponent == 0 ? -1074 : exponent - 1075);
        }
        var a = Decode(minimum);
        var b = Decode(maximum);
        var x = Decode(value);
        var common = Math.Min(a.Exponent, Math.Min(b.Exponent, x.Exponent));
        var ai = a.Integer << (a.Exponent - common);
        var bi = b.Integer << (b.Exponent - common);
        var xi = x.Integer << (x.Exponent - common);
        var scaled = (xi - ai) * BigInteger.Pow(10, 28) / (bi - ai);
        return (double)((decimal)scaled / 10000000000000000000000000000m);
    }

    private static HistoryChartLine? DecimalClipOracle(HistorySample first, HistorySample second,
        TimeWindow window, HistoryValueDomain domain)
    {
        var x0 = (decimal)first.UtcTicks;
        var x1 = (decimal)second.UtcTicks;
        var y0 = (decimal)first.Value!.Value;
        var y1 = (decimal)second.Value!.Value;
        var candidates = new List<decimal> { 0, 1 };
        if (x0 != x1)
        {
            candidates.Add((window.StartTicks - x0) / (x1 - x0));
            candidates.Add((window.EndTicks - x0) / (x1 - x0));
        }
        if (y0 != y1)
        {
            candidates.Add(((decimal)domain.Minimum - y0) / (y1 - y0));
            candidates.Add(((decimal)domain.Maximum - y0) / (y1 - y0));
        }
        const decimal epsilon = 0.00000000000000000001m;
        var inside = candidates.Where(t => t >= 0 && t <= 1).Where(t =>
        {
            var x = x0 + t * (x1 - x0);
            var y = y0 + t * (y1 - y0);
            return x >= window.StartTicks - epsilon && x <= window.EndTicks + epsilon &&
                y >= (decimal)domain.Minimum - epsilon && y <= (decimal)domain.Maximum + epsilon;
        }).Order().ToArray();
        if (inside.Length == 0) return null;
        HistoryChartPoint At(decimal t) => new(
            10 + (double)((x0 + t * (x1 - x0) - window.StartTicks) / window.DurationTicks) * 600,
            20 + (1 - (double)((y0 + t * (y1 - y0) - (decimal)domain.Minimum) /
                ((decimal)domain.Maximum - (decimal)domain.Minimum))) * 400);
        return new HistoryChartLine(At(inside[0]), At(inside[^1]));
    }
}
