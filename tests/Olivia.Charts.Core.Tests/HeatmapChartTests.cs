using Olivia.Core.Charts;
using Xunit;

namespace Olivia.Core.Tests;

public sealed class HeatmapChartTests
{
    [Fact]
    public void CopiesAxesAndCellsInRowMajorOrderWithExactIdentityValueAndVersion()
    {
        var rows = new[] { new HeatmapAxisItem("row-b", "Same"), new HeatmapAxisItem("row-a", "Same") };
        var columns = new[] { new HeatmapAxisItem("col-b", "Same"), new HeatmapAxisItem("col-a", "Same") };
        var cells = new[]
        {
            new HeatmapCell("row-a", "col-a", -0d), new HeatmapCell("row-b", "col-a", null),
            new HeatmapCell("row-a", "col-b", -8), new HeatmapCell("row-b", "col-b", 3)
        };
        var source = new HeatmapChartSource(rows, columns, cells, 91);
        var snapshot = source.Query();
        rows[0] = new("changed", "Changed"); columns[0] = new("changed", "Changed");
        cells[3] = new("changed", "changed", 99);
        Assert.Equal(new[] { "row-b", "row-a" }, source.Rows.Select(r => r.Key));
        Assert.Equal(new[] { "col-b", "col-a" }, source.Columns.Select(c => c.Key));
        Assert.All(source.Rows.Concat(source.Columns), axis => Assert.Equal("Same", axis.Label));
        Assert.Equal(new double?[] { 3, null, -8, -0d }, source.Cells.Select(c => c.Value));
        Assert.Equal(91, snapshot.DatasetVersion); Assert.Same(source, snapshot.Source);
        Assert.Equal(3, source.PresentCellCount); Assert.True(snapshot.HasValues); Assert.False(snapshot.IsEmpty);
        Assert.Equal(4, snapshot.RequiredCellCount); Assert.Equal(8, snapshot.RequiredWorkCount);
        for (var index = 0; index < 4; index++)
        {
            var cell = snapshot.Cells[index]; var hit = HeatmapChartGeometry.GetHit(snapshot, index)!.Value;
            Assert.Equal(91, hit.DatasetVersion); Assert.Same(source.Cells[index], hit.Cell);
            Assert.Same(source.Rows[index / 2], hit.Row); Assert.Same(source.Columns[index % 2], hit.Column);
            Assert.Equal(hit.Cell.RowKey, hit.RowKey); Assert.Equal(hit.Cell.ColumnKey, hit.ColumnKey);
            Assert.Equal(index / 2, cell.RowIndex); Assert.Equal(index % 2, cell.ColumnIndex);
            Assert.Equal(hit, HeatmapChartGeometry.HitTest(snapshot, cell.Left + cell.Width / 2, cell.Top + cell.Height / 2));
        }
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(snapshot.Cells[3].Hit.Value!.Value));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteValuesAreRejectedRatherThanSilentlyMadeMissing(double value)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new HeatmapCell("r", "c", value));

    [Fact]
    public void NegativeValuesUseTheirOwnFiniteRangeWithoutZeroPadding()
    {
        var snapshot = Matrix(1, 3, [-10, -7, -4]).Query();
        Assert.Equal(-10, snapshot.Domain!.Value.Minimum); Assert.Equal(-4, snapshot.Domain.Value.Maximum);
        Assert.Equal(new double?[] { 0, .5, 1 }, snapshot.Cells.Select(c => c.Position));
        Assert.Equal(new HeatmapColor(34, 87, 166), snapshot.Cells[0].Color);
        Assert.Equal(new HeatmapColor(132, 103, 102), snapshot.Cells[1].Color);
        Assert.Equal(new HeatmapColor(230, 118, 38), snapshot.Cells[2].Color);
        AssertLegend(snapshot);
    }

    [Fact]
    public void MissingIsDistinctFromZeroAndRetainsGeometryAndRawAccess()
    {
        var snapshot = Matrix(1, 3, [null, 0, 1]).Query();
        var missing = snapshot.Cells[0];
        Assert.Equal(HeatmapCellStatus.Missing, missing.Status); Assert.Null(missing.Position);
        Assert.Equal(HeatmapChartPalette.MissingColor, missing.Color);
        Assert.Equal(HeatmapCellStatus.Value, snapshot.Cells[1].Status); Assert.Equal(0, snapshot.Cells[1].Position);
        Assert.NotEqual(missing.Color, snapshot.Cells[1].Color);
        var hit = HeatmapChartGeometry.HitTest(snapshot, 0, 0);
        Assert.NotNull(hit); Assert.Null(hit.Value.Value); Assert.Equal("r0", hit.Value.RowKey);
        Assert.Equal("c0", hit.Value.ColumnKey);
        Assert.Equal(0, snapshot.Domain!.Value.Minimum); Assert.Equal(1, snapshot.Domain.Value.Maximum);
    }

    [Fact]
    public void EmptyAxesAndAllMissingAreUnambiguousAndSafe()
    {
        foreach (var (rows, columns) in new[] { (0, 0), (0, 3), (3, 0) })
        {
            var empty = Matrix(rows, columns, []).Query(new(0, 0));
            Assert.True(empty.IsEmpty); Assert.False(empty.HasValues); Assert.Null(empty.Domain);
            Assert.Empty(empty.Cells); Assert.Empty(empty.Legend); Assert.Equal(0, empty.RequiredWorkCount);
            Assert.Equal(ChartBudgetStatus.Satisfied, empty.BudgetStatus);
            Assert.Null(HeatmapChartGeometry.HitTest(empty, .5, .5));
        }
        var missing = Matrix(2, 2, [null, null, null, null]).Query();
        Assert.False(missing.IsEmpty); Assert.False(missing.HasValues); Assert.Null(missing.Domain);
        Assert.Empty(missing.Legend); Assert.Equal(4, missing.Cells.Length);
        Assert.All(missing.Cells, c =>
        {
            Assert.Equal(HeatmapCellStatus.Missing, c.Status); Assert.Null(c.Position);
            Assert.Equal(HeatmapChartPalette.MissingColor, c.Color);
        });
        Assert.NotNull(HeatmapChartGeometry.GetHit(missing, 3));
        Assert.Equal("r1", HeatmapChartGeometry.HitTest(missing, .75, .75)!.Value.RowKey);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-12d)]
    [InlineData(double.Epsilon)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void ConstantFiniteValuesHaveOneExactCenteredLegendEntry(double value)
    {
        var snapshot = Matrix(1, 3, [value, null, value]).Query();
        Assert.True(snapshot.Domain!.Value.IsConstant);
        Assert.Equal(value, snapshot.Domain.Value.Minimum); Assert.Equal(value, snapshot.Domain.Value.Maximum);
        Assert.Equal(.5, snapshot.Cells[0].Position); Assert.Equal(.5, snapshot.Cells[2].Position);
        var stop = Assert.Single(snapshot.Legend);
        Assert.Equal(value, stop.Value); Assert.Equal(.5, stop.Position);
        Assert.Equal(snapshot.Cells[0].Color, stop.Color); AssertLegend(snapshot);
    }

    [Fact]
    public void ExtremeCrossZeroSubnormalAndAdjacentRangesStayFiniteAndAccurate()
    {
        var huge = Matrix(1, 5, [-double.MaxValue, -double.MaxValue / 2, 0, double.MaxValue / 2, double.MaxValue]).Query();
        Assert.Equal(new double?[] { 0, .25, .5, .75, 1 }, huge.Cells.Select(c => c.Position));
        AssertLegend(huge);
        var tiny = Matrix(1, 5, [-2 * double.Epsilon, -double.Epsilon, 0, double.Epsilon, 2 * double.Epsilon]).Query();
        Assert.Equal(new double?[] { 0, .25, .5, .75, 1 }, tiny.Cells.Select(c => c.Position));
        AssertLegend(tiny);
        foreach (var lower in new[] { -double.MaxValue, -1d, 0, double.Epsilon, 1d, Math.BitDecrement(double.MaxValue) })
        {
            var upper = Math.BitIncrement(lower);
            var adjacent = Matrix(1, 2, [lower, upper]).Query();
            Assert.Equal(0, adjacent.Cells[0].Position); Assert.Equal(1, adjacent.Cells[1].Position);
            Assert.Equal(2, adjacent.Legend.Length); AssertLegend(adjacent);
        }
    }

    [Fact]
    public void LegendUsesActualRoundedValuePositionsIncludingSubnormalInteriorStops()
    {
        var snapshot = Matrix(1, 4, [0, double.Epsilon, 2 * double.Epsilon, 3 * double.Epsilon]).Query();
        Assert.Equal(new[] { 0, double.Epsilon, 2 * double.Epsilon, 3 * double.Epsilon }, snapshot.Legend.Select(s => s.Value));
        Assert.Equal(new[] { 0, 1d / 3, 2d / 3, 1 }, snapshot.Legend.Select(s => s.Position));
        for (var i = 0; i < 4; i++) Assert.Equal(snapshot.Cells[i].Color, snapshot.Legend[i].Color);
        AssertLegend(snapshot);
    }

    [Fact]
    public void BothBudgetsRefuseWholeFrameBeforePublishingAnyDomainGeometryLegendOrHits()
    {
        var source = Matrix(2, 2, [0, 1, null, -1]);
        foreach (var options in new[] { new HeatmapChartOptions(3), new HeatmapChartOptions(4, 7), new HeatmapChartOptions(0, 0) })
        {
            var refused = source.Query(options);
            Assert.Same(options, refused.Options); Assert.Equal(ChartBudgetStatus.InsufficientBudget, refused.BudgetStatus);
            Assert.Equal(4, refused.RequiredCellCount); Assert.Equal(8, refused.RequiredWorkCount);
            Assert.Empty(refused.Cells); Assert.Empty(refused.Legend); Assert.Null(refused.Domain);
            Assert.False(refused.IsEmpty); Assert.True(refused.HasValues);
            Assert.Null(HeatmapChartGeometry.HitTest(refused, 0, 0)); Assert.Null(HeatmapChartGeometry.GetHit(refused, 0));
        }
        var admitted = source.Query(new(4, 8));
        Assert.Equal(ChartBudgetStatus.Satisfied, admitted.BudgetStatus); Assert.Equal(4, admitted.Cells.Length);
        var allMissing = Matrix(1, 2, [null, null]).Query(new(2, 3));
        Assert.Equal(ChartBudgetStatus.InsufficientBudget, allMissing.BudgetStatus);
        Assert.Equal(4, allMissing.RequiredWorkCount); Assert.Empty(allMissing.Cells);
    }

    [Fact]
    public void ExactRectanglesCoverGridAndEveryHalfOpenSeamHasOneOwner()
    {
        var snapshot = Matrix(7, 49, Enumerable.Repeat<double?>(1, 7 * 49).ToArray()).Query();
        foreach (var cell in snapshot.Cells)
        {
            Assert.InRange(cell.Left, 0, 1); Assert.InRange(cell.Top, 0, 1);
            Assert.InRange(cell.Right, cell.Left, 1); Assert.InRange(cell.Bottom, cell.Top, 1);
            Assert.True(double.IsFinite(cell.Width)); Assert.True(double.IsFinite(cell.Height));
            Assert.True(cell.Width > 0); Assert.True(cell.Height > 0);
            Assert.Equal(cell.Hit, HeatmapChartGeometry.HitTest(snapshot, cell.Left, cell.Top));
            var insideRight = Math.BitDecrement(cell.Right); var insideBottom = Math.BitDecrement(cell.Bottom);
            Assert.Equal(cell.Hit, HeatmapChartGeometry.HitTest(snapshot, insideRight, insideBottom));
            if (cell.ColumnIndex > 0)
                Assert.Equal($"c{cell.ColumnIndex - 1}", HeatmapChartGeometry.HitTest(snapshot, Math.BitDecrement(cell.Left), cell.Top)!.Value.ColumnKey);
            if (cell.RowIndex > 0)
                Assert.Equal($"r{cell.RowIndex - 1}", HeatmapChartGeometry.HitTest(snapshot, cell.Left, Math.BitDecrement(cell.Top))!.Value.RowKey);
        }
        Assert.Equal("c48", HeatmapChartGeometry.HitTest(snapshot, Math.BitDecrement(1d), 0)!.Value.ColumnKey);
        Assert.Null(HeatmapChartGeometry.HitTest(snapshot, 1, .5)); Assert.Null(HeatmapChartGeometry.HitTest(snapshot, .5, 1));
        Assert.Null(HeatmapChartGeometry.HitTest(snapshot, -double.Epsilon, 0)); Assert.Null(HeatmapChartGeometry.HitTest(snapshot, 0, -double.Epsilon));
        Assert.Null(HeatmapChartGeometry.HitTest(snapshot, double.MaxValue, 0));
        Assert.Null(HeatmapChartGeometry.GetHit(snapshot, -1)); Assert.Null(HeatmapChartGeometry.GetHit(snapshot, snapshot.Cells.Length));
    }

    [Fact]
    public void MaximumMatrixIsAdmittedWithoutSamplingAndMatchesRawCells()
    {
        var values = Enumerable.Range(0, HeatmapChartSource.MaximumCells).Select(i => i % 3 == 0 ? null : (double?)i).ToArray();
        var source = Matrix(64, 64, values); var snapshot = source.Query();
        Assert.Equal(4096, source.Cells.Length); Assert.Equal(4096, snapshot.Cells.Length);
        Assert.Equal(8192, snapshot.RequiredWorkCount); Assert.Equal(ChartBudgetStatus.Satisfied, snapshot.BudgetStatus);
        Assert.Equal(values, snapshot.Cells.Select(c => c.Hit.Value));
        Assert.Equal("r63", snapshot.Cells[^1].Hit.RowKey); Assert.Equal("c63", snapshot.Cells[^1].Hit.ColumnKey);
        Assert.Equal(1, snapshot.Cells[^1].Right); Assert.Equal(1, snapshot.Cells[^1].Bottom);
        AssertLegend(snapshot);
    }

    [Fact]
    public void AdmissionRejectsDuplicateAxisIdsDuplicatePairsUnknownPairsAndIncompleteMatrices()
    {
        var rows = new[] { new HeatmapAxisItem("r", "Same") };
        var columns = new[] { new HeatmapAxisItem("c", "Same"), new HeatmapAxisItem("d", "Same") };
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource([new("r", "A"), new("r", "B")], [], []));
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource([], [new("c", "A"), new("c", "B")], []));
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource(rows, columns, [new("r", "c", 1), new("r", "c", 2)]));
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource(rows, columns, [new("r", "c", 1), new("unknown", "d", 2)]));
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource(rows, columns, [new("r", "c", 1), new("r", "unknown", 2)]));
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource(rows, columns, [new("r", "c", null)]));
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource([], [], [new("r", "c", null)]));
        var caseSensitive = new HeatmapChartSource([new("x", "Same"), new("X", "Same")], [new("x", "Same")],
            [new("x", "x", null), new("X", "x", 1)]).Query();
        Assert.Equal(2, caseSensitive.Cells.Length);
    }

    [Fact]
    public void AxisAdmissionStopsAfterCapPlusOneAndDisposesEndlessInputs()
    {
        foreach (var rowAxis in new[] { true, false })
        {
            var advances = 0; var disposed = false;
            IEnumerable<HeatmapAxisItem> Endless()
            {
                try { while (true) { advances++; yield return new(advances.ToString(), "Same"); } }
                finally { disposed = true; }
            }
            Assert.Throws<ArgumentException>(() => rowAxis
                ? new HeatmapChartSource(Endless(), [], [])
                : new HeatmapChartSource([], Endless(), []));
            Assert.Equal(65, advances); Assert.True(disposed);
        }
    }

    [Fact]
    public void CellAdmissionUsesRectangularLimitAndDisposesEndlessInputs()
    {
        var advances = 0; var disposed = false;
        IEnumerable<HeatmapCell> Endless()
        {
            try { while (true) { advances++; yield return new("r", "c", 1); } }
            finally { disposed = true; }
        }
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource([new("r", "R")], [new("c", "C")], Endless()));
        Assert.Equal(2, advances); Assert.True(disposed);
        advances = 0; disposed = false;
        var axes = Enumerable.Range(0, 64).Select(i => new HeatmapAxisItem(i.ToString(), "Same")).ToArray();
        Assert.Throws<ArgumentException>(() => new HeatmapChartSource(axes, axes, Endless()));
        Assert.Equal(4097, advances); Assert.True(disposed);
    }

    [Fact]
    public void InvalidTextNullInputsVersionsOptionsAndCoordinatesAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new HeatmapAxisItem(null!, "A"));
        Assert.Throws<ArgumentNullException>(() => new HeatmapAxisItem("a", null!));
        foreach (var key in new[] { "", " ", new string('x', 257), "\uD800", "\uDC00" })
        {
            Assert.Throws<ArgumentException>(() => new HeatmapAxisItem(key, "A"));
            Assert.Throws<ArgumentException>(() => new HeatmapCell(key, "c", null));
            Assert.Throws<ArgumentException>(() => new HeatmapCell("r", key, null));
        }
        Assert.Throws<ArgumentException>(() => new HeatmapAxisItem("a", new string('x', 257)));
        Assert.Throws<ArgumentException>(() => new HeatmapAxisItem("a", "\uD800"));
        Assert.Throws<ArgumentNullException>(() => new HeatmapCell(null!, "c", null));
        Assert.Throws<ArgumentNullException>(() => new HeatmapCell("r", null!, null));
        Assert.Throws<ArgumentNullException>(() => new HeatmapChartSource(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new HeatmapChartSource([], null!, []));
        Assert.Throws<ArgumentNullException>(() => new HeatmapChartSource([], [], null!));
        Assert.Throws<ArgumentNullException>(() => new HeatmapChartSource([null!], [], []));
        Assert.Throws<ArgumentNullException>(() => new HeatmapChartSource([], [null!], []));
        Assert.Throws<ArgumentNullException>(() => new HeatmapChartSource([new("r", "")], [new("c", "")], [null!]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeatmapChartSource([], [], [], -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeatmapChartOptions(geometryBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeatmapChartOptions(geometryBudget: 4097));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeatmapChartOptions(workBudget: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HeatmapChartOptions(workBudget: 8193));
        var snapshot = Matrix(1, 1, [1]).Query();
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => HeatmapChartGeometry.HitTest(snapshot, invalid, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => HeatmapChartGeometry.HitTest(snapshot, 0, invalid));
        }
        Assert.Throws<ArgumentNullException>(() => HeatmapChartGeometry.HitTest(null!, 0, 0));
        Assert.Throws<ArgumentNullException>(() => HeatmapChartGeometry.GetHit(null!, 0));
    }

    [Fact]
    public void PaletteIsDeterministicMonotoneAndNeverUsesMissingColor()
    {
        var previous = HeatmapChartPalette.ColorAt(0);
        for (var i = 0; i <= 4096; i++)
        {
            var color = HeatmapChartPalette.ColorAt(i / 4096d);
            Assert.True(color.R >= previous.R); Assert.True(color.G >= previous.G); Assert.True(color.B <= previous.B);
            Assert.NotEqual(HeatmapChartPalette.MissingColor, color); previous = color;
        }
        foreach (var invalid in new[] { -double.Epsilon, Math.BitIncrement(1d), double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => HeatmapChartPalette.ColorAt(invalid));
    }

    [Fact]
    public void DeterministicFullExponentMatricesHaveFiniteMonotonePositionsAndExactLegends()
    {
        var random = new Random(9941);
        for (var iteration = 0; iteration < 60; iteration++)
        {
            var values = Enumerable.Range(0, 64).Select(_ => Math.ScaleB(random.NextDouble(), random.Next(-1074, 1024)) * (random.Next(2) == 0 ? -1 : 1)).Order().ToArray();
            var snapshot = Matrix(8, 8, values.Select(v => (double?)v).ToArray()).Query();
            var previous = 0d;
            for (var index = 0; index < 64; index++)
            {
                var cell = snapshot.Cells[index]; Assert.Equal(values[index], cell.Hit.Value);
                Assert.True(double.IsFinite(cell.Position!.Value)); Assert.InRange(cell.Position.Value, previous, 1);
                Assert.Equal(HeatmapChartPalette.ColorAt(cell.Position.Value), cell.Color); previous = cell.Position.Value;
            }
            AssertLegend(snapshot);
        }
    }

    private static void AssertLegend(HeatmapChartSnapshot snapshot)
    {
        Assert.NotNull(snapshot.Domain); Assert.NotEmpty(snapshot.Legend);
        var domain = snapshot.Domain!.Value;
        Assert.Equal(domain.Minimum, snapshot.Legend[0].Value); Assert.Equal(domain.Maximum, snapshot.Legend[^1].Value);
        for (var index = 0; index < snapshot.Legend.Length; index++)
        {
            var stop = snapshot.Legend[index];
            Assert.True(double.IsFinite(stop.Value)); Assert.True(double.IsFinite(stop.Position));
            Assert.InRange(stop.Position, 0, 1); Assert.Equal(HeatmapChartPalette.ColorAt(stop.Position), stop.Color);
            if (index > 0) Assert.True(stop.Value > snapshot.Legend[index - 1].Value);
            var oneCell = new HeatmapCell("r", "c", stop.Value);
            // Inspect the production cell carrying this exact value in the same min/max domain.
            var sameRange = Matrix(1, 3, [domain.Minimum, oneCell.Value, domain.Maximum]).Query();
            Assert.Equal(stop.Position, sameRange.Cells[1].Position); Assert.Equal(stop.Color, sameRange.Cells[1].Color);
        }
    }

    private static HeatmapChartSource Matrix(int rows, int columns, double?[] values)
        => new(Enumerable.Range(0, rows).Select(r => new HeatmapAxisItem($"r{r}", "Same")),
            Enumerable.Range(0, columns).Select(c => new HeatmapAxisItem($"c{c}", "Same")),
            values.Select((value, index) => new HeatmapCell($"r{index / columns}", $"c{index % columns}", value)));
}
