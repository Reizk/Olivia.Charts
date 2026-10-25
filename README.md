<p align="center">
  <img src="samples/Olivia.Charts.Demo/Assets/chart-icon.png" width="88" height="88" alt="Olivia Charts logo">
</p>

<h1 align="center">Olivia Charts</h1>

<p align="center">Interactive charts for .NET and Avalonia.</p>

<p align="center">
  <strong>English</strong> · <a href="README.zh-CN.md">简体中文</a><br>
  <a href="#chart-gallery">Chart gallery</a> · <a href="#run-the-demo">Run the demo</a> · <a href="docs/DEVELOPMENT.md">Developer guide</a>
</p>

Olivia Charts brings everyday charts, time series, financial analysis, and live signals into an interactive desktop experience. Explore **24 examples across 5 categories**, switch between light and dark themes, and adjust each chart's available settings in the demo workbench.

**Development preview** · `0.1.0-alpha.3` · .NET 8 · Avalonia

![Olivia Charts workbench showing smooth curves in the light theme](docs/images/demo-line-light.png)

## Highlights

- **Explore your data.** Zoom around the pointer and drag through bar and scatter charts. Navigate time series with a range overview or compare measurements on separate axes.
- **Follow live signals.** View eight sensor channels across four panels. Pause updates, browse history while collection continues, and return to the live view.
- **Inspect financial series.** Work with candlesticks, time intervals, volume, Bollinger Bands, CCI, Williams %R, and trend or Fibonacci drawing tools.
- **Look closer at large datasets.** Explore two curves with one million samples each, with data prepared for the visible range.
- **Make the view your own.** Switch themes, show or hide settings, and adjust chart options such as line connections, markers, grids, and series visibility.

## Chart gallery

| Category | Included examples |
| --- | --- |
| **Basic charts · 6** | Line, grouped bar, scatter, doughnut and pie, radar, sparkline |
| **Area and range · 6** | Area, curved and stepped area, stacked area, stacked spline area, stacked step area, range |
| **Hierarchy and relationships · 4** | Heatmap, treemap, sunburst, flow |
| **Time and finance · 5** | Historical curve, time range navigator, dual-axis, multi-series, financial |
| **Live and large data · 3** | Eight-channel sensors, live scatter, million-point curves |

| Live sensors · Dark theme | Financial charts · Light theme |
| :---: | :---: |
| [![Eight live sensor channels across four panels](docs/images/demo-sensors-dark.png)](docs/images/demo-sensors-dark.png) | [![Candlesticks with volume, Bollinger Bands, and drawing controls](docs/images/demo-financial-light.png)](docs/images/demo-financial-light.png) |

All screenshots are from the demo. Sample data is generated locally; the demo runs without a data service.

## Run the demo

Install the **.NET 10 SDK** and **.NET 8 Runtime**, then run:

```sh
git clone https://github.com/Reizk/Olivia.Charts.git
cd Olivia.Charts
dotnet restore Olivia.Charts.sln --locked-mode
dotnet build Olivia.Charts.sln -c Release --no-restore
dotnet run --project samples/Olivia.Charts.Demo -c Release --no-build
```

Choose an example from the sidebar and open its settings to explore the available options. The demo interface is currently in Chinese; the [bilingual demo guide](docs/DEMO-OVERVIEW.zh-en.md) explains the controls.

The preview is currently built from source. Public NuGet packages are not available yet.

## Documentation

- [Demo features and controls](docs/DEMO-OVERVIEW.zh-en.md) — Chinese and English.
- [Developer guide](docs/DEVELOPMENT.md) — project structure, tests, and local packages.
- [Detailed feature coverage](docs/COVERAGE.zh-CN.md) — Chinese.

## License

An open-source license has not yet been assigned to this preview.
