# Developing Olivia Charts

[Project home](../README.md) · [中文首页](../README.zh-CN.md)

## Requirements

Use the .NET 10 SDK and .NET 8 Runtime. All projects target `net8.0`. The SDK selection in `global.json` also allows newer SDKs, including preview versions. The GitHub Actions workflow configures stable .NET 10 and .NET 8 on Windows and Linux.

## Build and run

From the repository root:

```sh
dotnet restore Olivia.Charts.sln --locked-mode
dotnet build Olivia.Charts.sln -c Release --no-restore
dotnet run --project samples/Olivia.Charts.Demo -c Release --no-build
```

Close a running demo before rebuilding it on Windows to avoid locked output files.

## Project structure

| Project | Purpose |
| --- | --- |
| `src/Olivia.Charts.Core` | Chart models, geometry, aggregation, streaming sessions, export, and configuration |
| `src/Olivia.Charts.Avalonia` | Avalonia chart controls and shared chart themes |
| `samples/Olivia.Charts.Demo` | Interactive chart workbench |
| `tests/Olivia.Charts.Core.Tests` | Calculation, boundary, and streaming behavior tests |
| `tests/Olivia.Charts.Headless.Tests` | Control, page, theme, and lifecycle tests |
| `tests/Olivia.Charts.DesktopSmoke` | Native desktop rendering and screenshot checks |

Core has no third-party package dependencies. The Avalonia library references Core and Avalonia. To explore how charts are configured, start with the demo's [example catalog](../samples/Olivia.Charts.Demo/ExampleCatalog.cs).

The assemblies and packages are named `Olivia.Charts.Core` and `Olivia.Charts.Avalonia`; source namespaces remain `Olivia.Core.*` and `Olivia.Avalonia.*`. Do not reference overlapping types from the original `Olivia.Core` or `Olivia.Avalonia` assemblies alongside these standalone packages in the same application.

## Test

After building:

```sh
dotnet test Olivia.Charts.sln -c Release --no-build
dotnet run --project tests/Olivia.Charts.DesktopSmoke -c Release --no-build -- artifacts/desktop
```

The desktop checks need a graphical session and save screenshots and results under `artifacts/desktop`. Build outputs, screenshots, and test results are excluded from version control. The GitHub workflow runs builds, automated tests, package creation, and an external package consumption check on Windows and Linux.

## Build local packages

```sh
dotnet pack src/Olivia.Charts.Core -c Release --no-build -o artifacts/packages
dotnet pack src/Olivia.Charts.Avalonia -c Release --no-build -o artifacts/packages
dotnet restore eng/package-smoke --configfile eng/package-smoke/NuGet.Config --packages artifacts/package-cache
dotnet run --project eng/package-smoke -c Release --no-restore
```

These commands create local NuGet packages and verify a consumer that references the packages rather than the source projects. They do not publish packages to a public feed.

## Further reading

- [Demo features and controls](DEMO-OVERVIEW.zh-en.md) — Chinese and English.
- [Feature coverage](COVERAGE.zh-CN.md) — Chinese.
- [Validation history and test boundaries](VALIDATION.zh-CN.md) — Chinese.
- [Source extraction map](source-map.json).
