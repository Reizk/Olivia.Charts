using System.Collections.Immutable;

namespace Olivia.Core.Charts;

/// <summary>Stable ordinal identity with a display-only label. Equal labels are valid.</summary>
public sealed record HeatmapAxisItem
{
    public HeatmapAxisItem(string key, string label)
    {
        BarChartSource.ValidateText(key, nameof(key), identity: true);
        BarChartSource.ValidateText(label, nameof(label));
        Key = key; Label = label;
    }
    public string Key { get; }
    public string Label { get; }
}

/// <summary>Original nullable matrix entry. Row and column keys together are its identity.
/// Null means missing; NaN and infinities are explicitly rejected rather than treated as missing.</summary>
public sealed record HeatmapCell
{
    public HeatmapCell(string rowKey, string columnKey, double? value)
    {
        BarChartSource.ValidateText(rowKey, nameof(rowKey), identity: true);
        BarChartSource.ValidateText(columnKey, nameof(columnKey), identity: true);
        if (value.HasValue && !double.IsFinite(value.Value))
            throw new ArgumentOutOfRangeException(nameof(value), "A heatmap value must be finite or null.");
        RowKey = rowKey; ColumnKey = columnKey; Value = value;
    }
    public string RowKey { get; }
    public string ColumnKey { get; }
    public double? Value { get; }
}

public sealed class HeatmapChartOptions
{
    public HeatmapChartOptions(int geometryBudget = HeatmapChartSource.MaximumCells,
        int workBudget = HeatmapChartSource.MaximumWork)
    {
        if (geometryBudget < 0 || geometryBudget > HeatmapChartSource.MaximumCells)
            throw new ArgumentOutOfRangeException(nameof(geometryBudget));
        if (workBudget < 0 || workBudget > HeatmapChartSource.MaximumWork)
            throw new ArgumentOutOfRangeException(nameof(workBudget));
        GeometryBudget = geometryBudget; WorkBudget = workBudget;
    }
    public static HeatmapChartOptions Default { get; } = new();
    public int GeometryBudget { get; }
    /// <summary>Maximum cell visits during Query: one domain pass and one geometry pass.
    /// Admission and HitTest have their own fixed caps. Constant-size legend work is independent.</summary>
    public int WorkBudget { get; }
}

public enum HeatmapCellStatus { Value, Missing }
public readonly record struct HeatmapColor(byte R, byte G, byte B);

/// <summary>Original continuous blue-to-orange RGB scale. All callers, including the legend,
/// use the same rounded mapping. Missing entries use a separate neutral color and status.</summary>
public static class HeatmapChartPalette
{
    public static HeatmapColor MissingColor { get; } = new(214, 219, 226);
    public static HeatmapColor ColorAt(double position)
    {
        if (!double.IsFinite(position) || position < 0 || position > 1)
            throw new ArgumentOutOfRangeException(nameof(position));
        return new(Channel(34, 230), Channel(87, 118), Channel(166, 38));
        byte Channel(byte first, byte last) => (byte)Math.Round(first + (last - first) * position,
            MidpointRounding.AwayFromZero);
    }
}

public readonly record struct HeatmapChartHit(long DatasetVersion, HeatmapAxisItem Row,
    HeatmapAxisItem Column, HeatmapCell Cell)
{
    public string RowKey => Row.Key;
    public string ColumnKey => Column.Key;
    public double? Value => Cell.Value;
}

/// <summary>Finite clipped normalized rectangle, X rightward and Y downward. Both axes are
/// half-open [start,end); shared endpoints have one owner. A missing cell retains real geometry.</summary>
public readonly record struct HeatmapChartCell(HeatmapChartHit Hit, int RowIndex, int ColumnIndex,
    double Left, double Top, double Right, double Bottom, HeatmapCellStatus Status,
    double? Position, HeatmapColor Color)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;
}

/// <summary>An exact finite legend value and its actual position/color under the cell mapping.</summary>
public readonly record struct HeatmapLegendStop(double Value, double Position, HeatmapColor Color);

public sealed class HeatmapChartSnapshot
{
    internal HeatmapChartSnapshot(HeatmapChartSource source, HeatmapChartOptions options,
        HistoryValueDomain? domain, ImmutableArray<HeatmapChartCell> cells,
        ImmutableArray<HeatmapLegendStop> legend, ChartBudgetStatus budgetStatus)
    {
        Source = source; Options = options; Domain = domain; Cells = cells; Legend = legend;
        BudgetStatus = budgetStatus;
    }
    public HeatmapChartSource Source { get; }
    public long DatasetVersion => Source.DatasetVersion;
    public HeatmapChartOptions Options { get; }
    /// <summary>Min/max of present raw values, without zero-padding. Null for no present values
    /// or an explicitly refused frame. Constant domains place every present value at 0.5.</summary>
    public HistoryValueDomain? Domain { get; }
    public ImmutableArray<HeatmapChartCell> Cells { get; }
    public ImmutableArray<HeatmapLegendStop> Legend { get; }
    public int RequiredCellCount => Source.Cells.Length;
    public int RequiredWorkCount => RequiredCellCount * 2;
    public ChartBudgetStatus BudgetStatus { get; }
    public bool IsEmpty => Source.Cells.IsEmpty;
    public bool HasValues => Source.PresentCellCount > 0;
}

/// <summary>Immutable complete numeric matrix: at most 64 rows, 64 columns and 4096 cells,
/// with at most 256 UTF-16 code units per key/label. Duplicate keys on an axis, unknown pairs,
/// repeated pairs and omitted pairs are rejected. Null is an explicit missing entry.
/// Input enumerators are disposed and advance at most their limit + 1 times. Rows/columns keep
/// caller order; cells are copied into row-major order without aggregation, interpolation or sampling.</summary>
public sealed class HeatmapChartSource
{
    public const int MaximumRows = 64;
    public const int MaximumColumns = 64;
    public const int MaximumCells = MaximumRows * MaximumColumns;
    public const int MaximumWork = MaximumCells * 2;
    public const int MaximumTextLength = 256;

    public HeatmapChartSource(IEnumerable<HeatmapAxisItem> rows, IEnumerable<HeatmapAxisItem> columns,
        IEnumerable<HeatmapCell> cells, long datasetVersion = 0)
    {
        if (datasetVersion < 0) throw new ArgumentOutOfRangeException(nameof(datasetVersion));
        Rows = BarChartSource.CopyBounded(rows, MaximumRows, nameof(rows));
        Columns = BarChartSource.CopyBounded(columns, MaximumColumns, nameof(columns));
        var rowIndices = Indices(Rows, nameof(rows));
        var columnIndices = Indices(Columns, nameof(columns));
        var expected = Rows.Length * Columns.Length;
        var input = BarChartSource.CopyBounded(cells, expected, nameof(cells));
        if (input.Length != expected)
            throw new ArgumentException("Every row/column pair must have an explicit cell, including missing cells.", nameof(cells));
        var ordered = new HeatmapCell[expected];
        var present = 0;
        foreach (var cell in input)
        {
            ArgumentNullException.ThrowIfNull(cell);
            if (!rowIndices.TryGetValue(cell.RowKey, out var row) || !columnIndices.TryGetValue(cell.ColumnKey, out var column))
                throw new ArgumentException("Unknown row or column key.", nameof(cells));
            var index = row * Columns.Length + column;
            if (ordered[index] is not null) throw new ArgumentException("Duplicate row/column pair.", nameof(cells));
            ordered[index] = cell;
            if (cell.Value.HasValue) present++;
        }
        Cells = ImmutableArray.CreateRange(ordered);
        DatasetVersion = datasetVersion; PresentCellCount = present;
    }
    public ImmutableArray<HeatmapAxisItem> Rows { get; }
    public ImmutableArray<HeatmapAxisItem> Columns { get; }
    public ImmutableArray<HeatmapCell> Cells { get; }
    public long DatasetVersion { get; }
    public int PresentCellCount { get; }

    public HeatmapChartSnapshot Query(HeatmapChartOptions? options = null)
    {
        options ??= HeatmapChartOptions.Default;
        // Refuse before either scan; never publish a partial matrix or a misleading partial domain.
        if (Cells.Length > options.GeometryBudget || Cells.Length * 2 > options.WorkBudget)
            return new(this, options, null, [], [], ChartBudgetStatus.InsufficientBudget);
        var minimum = double.PositiveInfinity; var maximum = double.NegativeInfinity;
        foreach (var cell in Cells)
            if (cell.Value.HasValue)
            {
                minimum = Math.Min(minimum, cell.Value.Value);
                maximum = Math.Max(maximum, cell.Value.Value);
            }
        HistoryValueDomain? domain = PresentCellCount == 0 ? null : new HistoryValueDomain(minimum, maximum);
        var geometry = ImmutableArray.CreateBuilder<HeatmapChartCell>(Cells.Length);
        for (var index = 0; index < Cells.Length; index++)
        {
            var row = index / Columns.Length; var column = index % Columns.Length;
            var cell = Cells[index];
            var status = cell.Value.HasValue ? HeatmapCellStatus.Value : HeatmapCellStatus.Missing;
            double? position = cell.Value.HasValue ? DualAxisChartGeometry.NormalizeY(cell.Value.Value, domain!.Value) : null;
            var color = position.HasValue ? HeatmapChartPalette.ColorAt(position.Value) : HeatmapChartPalette.MissingColor;
            geometry.Add(new(new(DatasetVersion, Rows[row], Columns[column], cell), row, column,
                (double)column / Columns.Length, (double)row / Rows.Length,
                (double)(column + 1) / Columns.Length, (double)(row + 1) / Rows.Length,
                status, position, color));
        }
        var legend = DualAxisChartGeometry.CreateTicks(domain)
            .Select(tick => new HeatmapLegendStop(tick.Value, tick.Position, HeatmapChartPalette.ColorAt(tick.Position)))
            .ToImmutableArray();
        return new(this, options, domain, geometry.MoveToImmutable(), legend, ChartBudgetStatus.Satisfied);
    }

    private static Dictionary<string, int> Indices(ImmutableArray<HeatmapAxisItem> items, string parameter)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < items.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(items[index]);
            if (!result.TryAdd(items[index].Key, index)) throw new ArgumentException("Duplicate axis key.", parameter);
        }
        return result;
    }
}

public static class HeatmapChartGeometry
{
    /// <summary>Hits exact raw identity/value/version, including missing cells. Outside the clipped
    /// half-open unit grid is a miss. Comparing stored edges rather than multiplying by the row or
    /// column count keeps rational boundaries deterministic at floating-point rounding seams.
    /// At most 4096 cells are inspected; an insufficient-budget frame exposes no hits.</summary>
    public static HeatmapChartHit? HitTest(HeatmapChartSnapshot snapshot, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (x < 0 || x >= 1 || y < 0 || y >= 1) return null;
        foreach (var cell in snapshot.Cells)
            if (x >= cell.Left && x < cell.Right && y >= cell.Top && y < cell.Bottom) return cell.Hit;
        return null;
    }

    /// <summary>Stable row-major keyboard access includes missing entries, but no refused frame.</summary>
    public static HeatmapChartHit? GetHit(HeatmapChartSnapshot snapshot, int index)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return index >= 0 && index < snapshot.Cells.Length ? snapshot.Cells[index].Hit : null;
    }
}
