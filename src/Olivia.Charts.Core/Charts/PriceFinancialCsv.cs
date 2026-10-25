using System.Globalization;

namespace Olivia.Core.Charts;

/// <summary>Reads the first 18,000 rows used by the official RealTimeFinancialDataGenerator.
/// Rows in that file are newest-first, without timestamps. Reconstructs its backward-minute
/// and Sunday-minus-two-days rule in the caller's fixed offset, preserving subminute ticks.
/// This is demo history, not a market feed or a time-zone/DST trading-calendar service.</summary>
public static class PriceFinancialCsv
{
    public const int InitialBars = 18000;

    public static PriceChartSource ReadInitial(TextReader reader, DateTimeOffset endTime)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var bars = new PriceBar[InitialBars];
        var timestamp = endTime;
        for (var row = 0; row < InitialBars; row++)
        {
            var line = reader.ReadLine() ?? throw new FormatException($"Financial CSV ended before row {row + 1}.");
            var fields = line.Split(',');
            if (fields.Length != 5) throw new FormatException($"Financial CSV row {row + 1} requires five values.");
            timestamp = timestamp.AddMinutes(-1);
            if (timestamp.DayOfWeek == DayOfWeek.Sunday) timestamp = timestamp.AddDays(-2);
            var index = InitialBars - row - 1;
            try
            {
                double Value(int column) => double.Parse(fields[column], NumberStyles.Float, CultureInfo.InvariantCulture);
                bars[index] = new(index + 1L, timestamp, Value(0), Value(1), Value(2), Value(3), Value(4));
            }
            catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
            {
                throw new FormatException($"Invalid Financial CSV row {row + 1}.", error);
            }
        }
        return new("official-financial-csv", "Official Financial demo CSV history", bars);
    }
}
