using Olivia.Core.Charts;

var source = new BarChartSource([new("a", "A"), new("b", "B")],
    [new("series", "Example", [new(1, "a", -4), new(2, "b", 8)])], 1);
var snapshot = source.Query();
if (snapshot.Bars.Length != 2 || snapshot.Domain != new HistoryValueDomain(-4, 8))
    throw new InvalidOperationException("Packaged chart calculation returned an unexpected result.");
var core = typeof(BarChartSource).Assembly.GetName();
var controls = typeof(Olivia.Avalonia.BarPlot).Assembly.GetName();
if (core.Name != "Olivia.Charts.Core" || controls.Name != "Olivia.Charts.Avalonia")
    throw new InvalidOperationException("Expected standalone assemblies.");
if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "Olivia.Core" or "Olivia.Avalonia"))
    throw new InvalidOperationException("Original product assembly unexpectedly loaded.");
Console.WriteLine($"PASS: {core.Name}, {controls.Name}; {snapshot.Bars.Length} bars; domain {snapshot.Domain}");
