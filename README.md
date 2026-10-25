# Olivia Charts

独立的 .NET 8 图表计算库、Avalonia 控件和图表示例程序。当前版本为 **0.1.0-alpha.2**，在私有仓库中开发。

## 运行示例

安装 .NET 10 SDK 和 .NET 8 Runtime，然后在仓库根目录运行：

```sh
dotnet restore Olivia.Charts.sln --locked-mode
dotnet build Olivia.Charts.sln -c Release --no-restore
dotnet run --project samples/Olivia.Charts.Demo -c Release --no-build
```

所有项目面向 `net8.0`。`global.json` 允许使用更高版本 SDK（包括预览版），方便现有开发环境构建；持续集成使用稳定版 .NET 10 SDK。本次 Windows 本地验证使用 .NET 11 预览 SDK、.NET 8 Runtime。

Demo 以图表工作台组织 27 个示例：基础曲线与统计、面积和堆叠、层级与流向、历史与金融、实时数据、百万点数据、三维曲面、数据绑定及工作区。支持分类、搜索、明暗主题、可收起的参数面板，以及按示例提供的暂停和重置操作。基础曲线支持平滑、折线、阶梯三种连接；三维散点已从 Demo 移除。八路传感器缩放保持实时跟随，拖动进入历史视图后可点击“回到实时”或按 Home 恢复。数据由代码生成，不使用原产品的行情 CSV、业务资料或其他模块。

## 项目边界

| 项目 | 用途 |
| --- | --- |
| `src/Olivia.Charts.Core` | 图表模型、几何、聚合、流式会话、图表导出与配置 |
| `src/Olivia.Charts.Avalonia` | Avalonia 绘图控件及最小主题依赖 |
| `samples/Olivia.Charts.Demo` | 新图表演示程序 |
| `tests/Olivia.Charts.Core.Tests` | 计算、边界、流式行为回归 |
| `tests/Olivia.Charts.Headless.Tests` | 控件、页面参数、主题和生命周期检查 |
| `tests/Olivia.Charts.DesktopSmoke` | 原生桌面启动、逐页渲染与截图检查 |

Core 没有第三方包依赖。Avalonia 库仅依赖 Core 和 Avalonia；仓库不引用原产品的 Grid、Documents、PDF、SQLite 或旧 Demo 项目。

这一版保留 `Olivia.Core.*` 和 `Olivia.Avalonia.*` 源码命名空间，包和程序集改为 `Olivia.Charts.Core`、`Olivia.Charts.Avalonia`。**不能在同一应用中同时引用原 Olivia.Core/Olivia.Avalonia 和新包中的重叠类型。** Azure 主项目接入新包时还需替换旧图表实现；本次保留 Azure 原模块，未进行主产品集成切换。

## 测试与打包

```sh
dotnet test Olivia.Charts.sln -c Release --no-build
dotnet run --project tests/Olivia.Charts.DesktopSmoke -c Release --no-build -- artifacts/desktop
dotnet pack src/Olivia.Charts.Core -c Release --no-build -o artifacts/packages
dotnet pack src/Olivia.Charts.Avalonia -c Release --no-build -o artifacts/packages
```

桌面检查需要可用的图形会话。构建输出、截图和测试结果位于被忽略的 `artifacts/`、`bin/`、`obj/` 目录。打包生成本地 NuGet 文件，不会发布到公共包源。GitHub 工作流分别检查 Windows 和 Linux 的构建、自动化测试及打包。

- [功能与 Demo 覆盖范围](docs/COVERAGE.zh-CN.md)
- [验证记录与限制](docs/VALIDATION.zh-CN.md)
- [源码抽取路径与原始校验值](docs/source-map.json)

GitHub 使用仅含图表源码的新历史，不携带原产品的 Git 历史。当前未授予公共开源许可证；对外开放前需单独确定授权范围和许可证。
