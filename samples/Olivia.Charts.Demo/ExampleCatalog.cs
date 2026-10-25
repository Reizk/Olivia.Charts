using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Olivia.Avalonia;
using Olivia.Avalonia.Editing;
using Olivia.Avalonia.Themes;
using Olivia.Core.Charts;
using Olivia.Core.Editing;
using Olivia.Core.Workspaces;

namespace Olivia.Charts.Demo;

public static class ExampleCatalog
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static IReadOnlyList<ChartExample> All { get; } =
    [
        E("bars", "分组柱状图", "基础图表", "比较不同月份的三组数据，切换排列方式。", "3 个系列 · 12 个月", Bars),
        E("scatter", "散点图", "基础图表", "查看两组观测值的分布，缩放后选取原始点。", "2 个系列 · 120 个点", () => Scatter(false)),
        E("proportion", "环形与饼图", "基础图表", "按原始数值展示比例，每个分类都可以选择。", "6 个分类 · 原始权重", Proportion),
        E("radar", "雷达图", "基础图表", "在相同量程下比较两个方案的六项指标。", "2 个方案 · 6 项指标", Radar),
        E("sparkline", "迷你图", "基础图表", "用紧凑的曲线、面积或柱形表达趋势。", "32 个观测值", Sparkline),
        E("area", "面积图", "面积与区间", "观察三个系列随时间的变化。", "3 个系列 · 24 个时段", () => Area(false)),
        E("spline", "曲线与阶梯面积", "面积与区间", "比较线性、平滑曲线与阶梯连接的效果。", "3 个系列 · 8 个时段", () => Area(true)),
        E("stacked", "堆叠面积", "面积与区间", "同时展示总量与各系列的贡献。", "3 个系列 · 24 个时段", () => Stacked(StackedAreaChartInterpolation.Linear)),
        E("stacked-spline", "堆叠曲线面积", "面积与区间", "平滑的堆叠边界，可切换百分比视图。", "3 个系列 · 8 个时段", () => Stacked(StackedAreaChartInterpolation.Spline)),
        E("stacked-step", "堆叠阶梯面积", "面积与区间", "表达按区间变化的数据与组成比例。", "3 个系列 · 阶梯连接", () => Stacked(StackedAreaChartInterpolation.Step)),
        E("range", "区间图", "面积与区间", "展示上下界，比较区间柱形与带状面积。", "24 组上下界", Range),
        E("heatmap", "热力图", "层次与关系", "通过色阶查看每周各时段的数值变化。", "7 天 × 12 个时段", Heatmap),
        E("treemap", "矩形树图", "层次与关系", "通过矩形面积比较分支，点击深入查看。", "3 个分组 · 9 个子项", () => Hierarchy(false)),
        E("sunburst", "旭日图", "层次与关系", "通过同心扇区查看层次与占比。", "3 个分组 · 9 个子项", () => Hierarchy(true)),
        E("flow", "流向图", "层次与关系", "追踪从来源到结果的流量与分配。", "6 个节点 · 8 条连接", Flow),
        E("history", "历史曲线", "时间与金融", "缩放与平移时间窗口，选择原始数据点。", "720 个观测值 · UTC", () => History(false)),
        E("history-range", "时间范围导航", "时间与金融", "下方概览控制主图显示的时间范围。", "主图与概览共享数据", () => History(true)),
        E("dual-axis", "双轴曲线", "时间与金融", "使用独立量程比较温度和流量。", "2 条曲线 · 2 个量纲", () => MultiSeries(true)),
        E("multi-series", "多系列曲线", "时间与金融", "在同一坐标轴上比较三条时间曲线。", "3 条曲线 · 360 个时点", () => MultiSeries(false)),
        E("price", "金融图表", "时间与金融", "查看蜡烛图、成交量和技术指标，也可绘制标注。", "180 根原创合成 K 线", Price),
        E("sensors", "八路实时传感器", "实时与大数据", "四个面板同步查看八路信号，可暂停与继续。", "8 路信号 · 有界保留", Sensors),
        E("scatter-live", "实时散点", "实时与大数据", "观察两组点随时间移动，暂停后仔细检查。", "120 个点 · 确定性动画", () => Scatter(true)),
        E("large-data", "百万点曲线", "实时与大数据", "缩放查看细节，显示按视口准备的曲线。", "2 条曲线 · 共 200 万点", LargeData),
        E("point3d", "三维散点", "三维图表", "旋转与缩放相机，从不同方向观察空间分布。", "480 个空间点", Point3D),
        E("surface", "实时三维曲面", "三维图表", "观察连续曲面变化，切换底面投影与图例。", "41 × 41 网格 · CPU 渲染", Surface),
        E("binding", "字段聚合", "数据与配置", "切换聚合规则，比较原始记录在图表中的结果。", "72 条原创业务记录", Binding),
        E("workspace", "散点配置", "数据与配置", "使用可调的点预算观察数据准入与显示状态。", "120 个原始点 · 显式预算", () => Scatter(false, true))
    ];

    private static ChartExample E(string id, string title, string group, string description, string note, Func<ChartScene> factory) =>
        new(id, title, group, description, note, factory);

    public static IEnumerable<ChartExample> Search(string? text, string? group = null) => All.Where(e =>
        (string.IsNullOrEmpty(group) || e.Group == group) && (string.IsNullOrWhiteSpace(text) ||
        $"{e.Title} {e.Group} {e.Description} {e.Id}".Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)));

    private static ChartScene Scene(Control view, Action<UiPalette> theme, Func<string> status,
        Action release, Action? reset = null, Action? tick = null, Func<Task>? idle = null) => new()
    {
        View = view,
        ApplyTheme = (palette, _) => { theme(palette); view.InvalidateVisual(); },
        ReadStatus = status,
        Reset = reset,
        Tick = tick,
        Release = () => { release(); return idle?.Invoke() ?? Task.CompletedTask; }
    };

    private static Control Choice(string label, string[] values, Action<int> apply, int selected = 0)
    {
        var input = new ComboBox { ItemsSource = values, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        input.SelectionChanged += (_, _) => { if (input.SelectedIndex >= 0) apply(input.SelectedIndex); };
        return new StackPanel { Spacing = 7, Children = { new TextBlock { Text = label, FontSize = 12 }, input } };
    }
    private static Control Toggle(string label, bool initial, Action<bool> apply)
    {
        var input = new CheckBox { Content = label, IsChecked = initial };
        input.IsCheckedChanged += (_, _) => apply(input.IsChecked == true);
        return input;
    }
    private static Control ActionButton(string label, Action action)
    {
        var input = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        input.Click += (_, _) => action();
        return input;
    }
    private static AreaSeries[] AreaSeries(int count = 24) => Enumerable.Range(0, 3).Select(s => new AreaSeries(
        "series-" + s, new[] { "North", "Central", "South" }[s], Enumerable.Range(0, count).Select(i =>
            new AreaSample(s * 100 + i, i + 1, 24 + s * 12 + 10 * Math.Sin(i * .34 + s))), s + 1)).ToArray();

    private static ChartScene Bars()
    {
        var chart = new BarPlot();
        var categories = Enumerable.Range(1, 12).Select(i => new BarCategory("m" + i, i.ToString("00"))).ToArray();
        chart.SetSource(new(categories, Enumerable.Range(0, 3).Select(s => new BarSeries("s" + s,
            new[] { "North", "Central", "South" }[s], categories.Select((c, i) =>
                new BarSample(s * 100 + i, c.Key, 40 + s * 12 + 22 * Math.Sin(i * .5 + s))))), 1));
        var scene = Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource, chart.ResetView);
        var modes = Enum.GetValues<BarChartLayout>();
        scene.Options.Add(Choice("排列方式", modes.Select(x => x.ToString()).ToArray(), i => chart.SetOptions(new(modes[i]))));
        scene.Options.Add(ActionButton("放大", () => chart.Zoom(1.5)));
        return scene;
    }
    private static ChartScene Area(bool spline)
    {
        var source = new AreaChartSource(AreaSeries(spline ? 8 : 24), 1);
        if (!spline)
        {
            var chart = new AreaPlot(); chart.SetSource(source);
            var scene = Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource, chart.ResetView);
            scene.Options.Add(ActionButton("放大", () => chart.Zoom(1.5)));
            return scene;
        }
        var smooth = new SplineAreaPlot { ShowLabels = false }; smooth.SetSource(source, new(mode: AreaChartMode.SplineArea), SplineAreaPresentation.Numeric);
        var result = Scene(smooth, p => smooth.Palette = p, () => smooth.AccessibleSummary, smooth.ReleaseSource,
            () => smooth.SetSource(source, new(mode: smooth.Options.Mode)));
        AreaChartMode[] modes = [AreaChartMode.LinearArea, AreaChartMode.SplineArea, AreaChartMode.StepAreaAfter];
        result.Options.Add(Choice("连接方式", ["线性", "平滑曲线", "阶梯"], i => smooth.SetOptions(new(mode: modes[i])), 1));
        result.Options.Add(Toggle("显示数值标签", false, value => smooth.ShowLabels = value));
        return result;
    }
    private static ChartScene Stacked(StackedAreaChartInterpolation interpolation)
    {
        var chart = new StackedAreaPlot { ChartTitle = "Regional activity", ArgumentLabel = "Period", ValueAxisTitle = "Units" };
        var source = new StackedAreaChartSource(AreaSeries(interpolation == StackedAreaChartInterpolation.Spline ? 8 : 24), 1);
        var mode = StackedAreaChartMode.Stacked;
        void Apply() => chart.SetSource(source, new(mode, interpolation: interpolation));
        Apply();
        var scene = Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource, Apply);
        scene.Options.Add(Choice("数值方式", ["总量堆叠", "百分比"], i => { mode = i == 0 ? StackedAreaChartMode.Stacked : StackedAreaChartMode.Normalized100; Apply(); }));
        scene.Options.Add(Toggle("显示数值标签", false, v => chart.ShowLabels = v));
        return scene;
    }
    private static ChartScene Scatter(bool live, bool workspace = false)
    {
        var chart = new ScatterPlot(); long revision = 0;
        void Update() => chart.SetSource(new(Enumerable.Range(0, 2).Select(s => new ScatterSeries("s" + s,
            s == 0 ? "Observed" : "Reference", Enumerable.Range(0, 60).Select(i => new ScatterSample(s * 100 + i,
                50 + 35 * Math.Sin(i * .47 + revision * .025 + s), 50 + 30 * Math.Cos(i * .21 + revision * .018 + s))))), ++revision));
        Update();
        var scene = Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource, chart.ResetView, live ? Update : null);
        scene.Options.Add(ActionButton("放大", () => chart.Zoom(1.5)));
        if (workspace) scene.Options.Add(Choice("点预算", ["128", "64（不足时明确提示）", "256"], i => chart.SetOptions(new(new[] { 128, 64, 256 }[i]))));
        return scene;
    }
    private static ChartScene Proportion()
    {
        var chart = new ProportionPlot();
        chart.SetSource(new(new[] { "Desktop", "Mobile", "Web", "Services", "Tools", "Other" }.Select((label, i) => new ProportionCategory("p" + i, label, new[] { 32, 26, 18, 12, 8, 4 }[i])), 1));
        chart.SetOptions(new(.58));
        var scene = Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource);
        scene.Options.Add(Choice("图形", ["环形", "饼图"], i => chart.SetOptions(new(i == 0 ? .58 : 0))));
        return scene;
    }
    private static ChartScene Radar()
    {
        var chart = new RadarPlot();
        var session = new RadarSession(new(new[] { "Quality", "Speed", "Reach", "Stability", "Cost", "Support" }.Select((x, i) => new RadarAxis("a" + i, x)),
            [new RadarSeries("a", "Plan A", [82, 68, 88, 74, 57, 90]), new RadarSeries("b", "Plan B", [65, 88, 70, 92, 78, 62])], 1));
        chart.Bind(session);
        var scene = Scene(chart, p => chart.Palette = p, () => session.SelectionDescription, () => { chart.Close(); session.Dispose(); });
        scene.Options.Add(Toggle("显示方案 B", true, value => session.SetVisible("b", value)));
        scene.Options.Add(Choice("最大量程", ["100", "120", "150"], i => session.SetMaximum(new[] { 100, 120, 150 }[i])));
        return scene;
    }
    private static ChartScene Sparkline()
    {
        var chart = new SparklinePreview { Margin = new Thickness(32), VerticalAlignment = VerticalAlignment.Center, Height = 320 };
        var values = new SparklineSnapshot(Enumerable.Range(0, 32).Select(i => (decimal)(12 + 20 * Math.Sin(i * .21))));
        chart.Update(values, SparklineMode.Line);
        var scene = Scene(chart, _ => { }, () => "32 个观测值 · 固定生成公式", () => chart.Update(new([]), SparklineMode.Line));
        var modes = Enum.GetValues<SparklineMode>();
        scene.Options.Add(Choice("形状", modes.Select(m => m.ToString()).ToArray(), i => chart.Update(values, modes[i])));
        return scene;
    }
    private static ChartScene Range()
    {
        var chart = new RangePlot();
        chart.SetSource(new([new RangeSeries("forecast", "Expected interval", Enumerable.Range(0, 24).Select(i => new RangeSample(i, i + 1, 20 + 9 * Math.Sin(i * .3), 35 + 9 * Math.Sin(i * .3))))], 1));
        var scene = Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource);
        var modes = Enum.GetValues<RangeChartMode>();
        scene.Options.Add(Choice("显示方式", ["分组区间柱", "重叠区间柱", "区间面积"], i => chart.SetOptions(new(modes[i]))));
        return scene;
    }
    private static ChartScene Heatmap()
    {
        var chart = new HeatmapPlot();
        var rows = new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }.Select((s, i) => new HeatmapAxisItem("r" + i, s)).ToArray();
        var columns = Enumerable.Range(0, 12).Select(i => new HeatmapAxisItem("c" + i, (i * 2).ToString("00") + ":00")).ToArray();
        chart.SetSource(new(rows, columns, rows.SelectMany((r, y) => columns.Select((c, x) => new HeatmapCell(r.Key, c.Key, 50 + 40 * Math.Sin(x * .36 + y * .5)))), 1));
        return Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource);
    }
    private static TreemapNode[] Nodes() => Enumerable.Range(0, 3).SelectMany(g =>
        new[] { new TreemapNode("g" + g, new[] { "Platform", "Products", "Services" }[g], null, 0) }.Concat(
            Enumerable.Range(0, 3).Select(i => new TreemapNode($"n{g}-{i}", new[] { "Core", "Cloud", "Tools" }[i], "g" + g, (g + 2) * (i + 2) * 7)))).ToArray();
    private static ChartScene Hierarchy(bool sunburst)
    {
        if (sunburst)
        {
            var chart = new SunburstPlot(); chart.SetSource(new(Nodes(), 1));
            return Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource,
                () => chart.SetSource(new(Nodes(), 1)));
        }
        var tree = new TreemapPlot(); tree.SetSource(new(Nodes(), 1));
        return Scene(tree, p => tree.Palette = p, () => tree.AccessibleSummary, tree.ReleaseSource,
            () => tree.SetSource(new(Nodes(), 1)));
    }
    private static ChartScene Flow()
    {
        var chart = new FlowPlot();
        chart.SetSource(new([new("a", "Inbound"), new("b", "Partners"), new("c", "Product"), new("d", "Service"), new("e", "Direct"), new("f", "Online")],
            [new("ac", "a", "c", 45), new("ad", "a", "d", 25), new("bc", "b", "c", 15), new("bd", "b", "d", 15),
             new("ce", "c", "e", 35), new("cf", "c", "f", 25), new("de", "d", "e", 10), new("df", "d", "f", 30)], 1));
        return Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource);
    }
    private static HistorySeries HistorySource(int channel, int count = 720) => new(Enumerable.Range(0, count).Select(i =>
        new HistorySample(i, Epoch.AddMinutes(i).UtcTicks, 30 + channel * 20 + 18 * Math.Sin(i * .035 + channel) + 4 * Math.Cos(i * .17))), channel + 1);
    private static ChartScene History(bool range)
    {
        var chart = new HistoryPlot(); chart.SetSeries(HistorySource(0));
        if (!range) return Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary,
            chart.ReleaseSource, chart.ResetView, idle: () => chart.WhenIdle);
        var navigator = new HistoryRangeNavigator(chart);
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,180") };
        grid.Children.Add(chart); Grid.SetRow(navigator, 1); grid.Children.Add(navigator);
        return Scene(grid, p => { chart.Palette = p; navigator.Palette = p; }, () => chart.AccessibleSummary,
            () => { navigator.ReleaseSource(); chart.ReleaseSource(); }, chart.ResetView, idle: () => Task.WhenAll(chart.WhenIdle, navigator.WhenIdle));
    }
    private static ChartScene MultiSeries(bool dual)
    {
        var chart = new DualAxisHistoryPlot();
        HistoryAxisDefinition[] axes = dual ? [new("temperature", "Temperature", "°C", HistoryAxisSide.Left), new("flow", "Flow", "L/min", HistoryAxisSide.Right)] : [new("units", "Activity", "units", HistoryAxisSide.Left)];
        chart.SetSource(new(axes, Enumerable.Range(0, dual ? 2 : 3).Select(i => new HistorySeriesDefinition("s" + i,
            dual ? i == 0 ? "Temperature" : "Flow" : new[] { "North", "Central", "South" }[i], axes[dual ? i : 0].Id, HistorySource(i, 360))), 1));
        return Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource,
            chart.ResetView, idle: () => chart.WhenIdle);
    }
    private static ChartScene Price()
    {
        var chart = new PricePlot();
        var bars = Enumerable.Range(0, 180).Select(i =>
        {
            var open = 100 + i * .08 + 7 * Math.Sin(i * .14);
            var close = open + 1.7 * Math.Sin(i * .77 + .3);
            return new PriceBar(i, Epoch.AddMinutes(i), open, Math.Max(open, close) + 1.2, Math.Min(open, close) - 1, close, 1000 + i * 17 % 1700);
        });
        var source = new PriceChartSource("synthetic", "Synthetic OHLC", bars, 1, 1);
        chart.SetSource(source);
        chart.SetBollingerEnabled(true); chart.SetVolumeEnabled(true);
        var scene = new ChartScene
        {
            View = chart,
            ApplyTheme = (p, dark) => { chart.Palette = p; chart.ChartTheme = (dark ? OliviaThemeDefinition.VS2019Dark : OliviaThemeDefinition.Win11Light).Chart; },
            ReadStatus = () => chart.AccessibleSummary,
            Reset = chart.ResetView,
            Release = () => { chart.ReleaseSource(); return Task.CompletedTask; }
        };
        var intervals = Enum.GetValues<PriceTimeInterval>();
        scene.Options.Add(Choice("时间周期", new[] { "原始分钟数据" }.Concat(intervals.Select(PriceTimeAggregator.GetLabel)).ToArray(), i =>
        {
            if (i == 0) chart.SetSource(source, new());
            else chart.SetAggregatedSource(PriceTimeAggregator.Aggregate(source, intervals[i - 1]), new());
        }));
        scene.Options.Add(Toggle("布林带", true, chart.SetBollingerEnabled));
        scene.Options.Add(Toggle("成交量", true, chart.SetVolumeEnabled));
        scene.Options.Add(Toggle("CCI 指标", false, chart.SetCciEnabled));
        scene.Options.Add(Toggle("Williams %R", false, chart.SetWilliamsREnabled));
        scene.Options.Add(ActionButton("绘制趋势线", () => chart.BeginTrendLinePlacement()));
        scene.Options.Add(ActionButton("绘制斐波那契回撤", () => chart.BeginRetracementPlacement()));
        scene.Options.Add(ActionButton("绘制斐波那契扇形", () => chart.BeginFanPlacement()));
        scene.Options.Add(ActionButton("绘制斐波那契弧线", () => chart.BeginArcPlacement()));
        return scene;
    }
    private sealed class DemoClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Epoch.AddDays(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static ChartScene Sensors()
    {
        var chart = new RealtimeSensorPlot(); var clock = new DemoClock();
        var session = new RealtimeSensorSession(action => Dispatcher.UIThread.Post(action), frame => chart.SetFrame(frame),
            new(capacity: 2000, initialSampleCount: 1000, pointBudget: 500), clock);
        session.Initialize(); session.PublishTick();
        chart.ZoomRequested += (scale, anchor) => { session.Zoom(scale, anchor); session.PublishTick(); };
        return Scene(chart, p => chart.Palette = p, () => session.Statistics.Error?.Message ?? chart.Status,
            chart.ReleaseFrame, tick: () => { clock.Now += TimeSpan.FromMilliseconds(100); session.ProduceTick(); session.PublishTick(); }, idle: session.StopAsync);
    }
    private static ChartScene LargeData()
    {
        var chart = new LargeDataPlot();
        chart.SetSource(new(1, [LargeDataSeries.Generate(1_000_000, 1), LargeDataSeries.Generate(1_000_000, 2)]));
        var scene = Scene(chart, p => chart.Palette = p, () => chart.Status, chart.ReleaseSource, chart.ResetView, idle: () => chart.WhenIdle);
        scene.Options.Add(ActionButton("放大到局部", () => chart.Zoom(4)));
        return scene;
    }
    private static ChartScene Point3D()
    {
        var chart = new Point3DPlot();
        chart.SetSource(new("helix", Enumerable.Range(0, 480).Select(i => new Point3DSample(i,
            Math.Sin(i * .11) * (1 + i * .003), Math.Cos(i * .11) * (1 + i * .003), i * .018 - 4)), 1));
        var scene = Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource, chart.ResetCamera);
        scene.Options.Add(ActionButton("旋转视角", () => chart.Orbit(.25, .1)));
        scene.Options.Add(ActionButton("放大", () => chart.Zoom(1.2)));
        return scene;
    }
    private static ChartScene Surface()
    {
        var chart = new SurfacePlot(); var version = 0L;
        var axis = Enumerable.Range(0, 41).Select(i => (i - 20) / 5d).ToArray();
        void Update()
        {
            var phase = version * .07;
            var source = new SurfaceChartSource("wave", axis, axis,
                axis.SelectMany(x => axis.Select(y => (double?)(Math.Sin(x + phase) * Math.Cos(y + phase * .6) * 2))), ++version);
            chart.SetSource(source, -2, 2);
        }
        Update();
        var scene = new ChartScene
        {
            View = chart, Reset = chart.ResetCamera, Tick = Update, ReadStatus = () => chart.AccessibleSummary,
            ApplyTheme = (p, dark) => { chart.Palette = p; chart.ChartTheme = (dark ? OliviaThemeDefinition.VS2019Dark : OliviaThemeDefinition.Win11Light).Chart; },
            Release = () => { chart.ReleaseSource(); return chart.RenderingCompletion; }
        };
        scene.Options.Add(Toggle("显示底面投影", chart.ShowProjection, v => chart.ShowProjection = v));
        scene.Options.Add(Toggle("显示图例", chart.ShowLegend, v => chart.ShowLegend = v));
        return scene;
    }
    private static ChartScene Binding()
    {
        var chart = new BarPlot();
        var source = new TypedChartBindingSource(Enumerable.Range(0, 72).Select(i => new ChartBindingRow(i,
            new("c" + i % 3, new[] { "North", "Central", "South" }[i % 3]),
            new("p" + i % 4, new[] { "Desktop", "Cloud", "Mobile", "Tools" }[i % 4]), i % 12 + 1, 100 + i * 7, 200 + i * 13)));
        var function = ChartBindingFunction.Sum;
        void Apply() => chart.SetSource(TypedChartBinding.Build(source, ChartBindingRequest.Default with { Function = function }).Bars);
        Apply();
        var scene = Scene(chart, p => chart.Palette = p, () => chart.AccessibleSummary, chart.ReleaseSource, chart.ResetView);
        var functions = Enum.GetValues<ChartBindingFunction>();
        scene.Options.Add(Choice("聚合规则", ["求和", "平均值", "最小值", "最大值"], i => { function = functions[i]; Apply(); }));
        return scene;
    }
}
