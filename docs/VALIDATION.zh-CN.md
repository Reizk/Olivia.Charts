# 独立版本验证记录

2026-10-25，0.1.0-alpha.1。结果仅代表本次实际执行的检查。

## 独立性与构建

将精选交付文件复制到原 Azure 仓库和工作树之外的新目录，未复制 `bin`、`obj`、缓存、截图或旧 Git 元数据。在新目录执行锁定恢复、Release 构建、测试及两个包的打包，全部通过。构建为 0 警告、0 错误。

环境：Windows 10 19045、.NET SDK 11.0.100-rc.2.26504.105、.NET Runtime 8.0.32，目标框架 net8.0，Avalonia 12.1.4。使用 SDK 预览版的提示不等于项目使用预览目标框架。GitHub 工作流另外配置稳定 .NET 10 SDK 的 Windows/Linux 检查；远程结果以实际工作流记录为准。

| 检查 | 本地结果 |
| --- | --- |
| Core 自动化测试 | 1,136 通过，0 失败，0 跳过 |
| Headless 控件/页面测试 | 87 通过，0 失败，0 跳过 |
| 原生 Windows 页面检查 | 27 页均完成明暗主题渲染、工作状态检查和关闭，54 张截图 |
| NuGet 打包 | Core、Avalonia 两包成功，版本 0.1.0-alpha.1 |
| 外部包消费程序 | 通过：仅引用 Avalonia 包，传递获得 Core；两根柱的值域为 [-4,8]，加载程序集为新包名称 |

外部消费程序在另一目录使用单独的空包缓存恢复，没有 ProjectReference。可复用仓库内 `eng/package-smoke`：先运行 README 中的打包命令，再执行：

```sh
dotnet restore eng/package-smoke --configfile eng/package-smoke/NuGet.Config --packages artifacts/package-cache
dotnet run --project eng/package-smoke -c Release --no-restore
```

两个 nupkg 的有效载荷分别为 README 和 `lib/net8.0/Olivia.Charts.Core.dll`、`lib/net8.0/Olivia.Charts.Avalonia.dll`。Core 无包依赖；Avalonia 包依赖 Core 0.1.0-alpha.1 与 Avalonia 12.1.4。未包含旧产品程序集、业务数据或原 Git 历史。

## 本次发现并修复的问题

- RealtimeSensorSession 的时钟在创建定时器时抛异常，原 PeriodicTimer 在 .NET 8 下可能留下未构造完成的可终结对象，导致测试进程在 GC 阶段崩溃。改用单槽、合并节拍的内部计时器，保留取消和背压语义；回归检查包含强制终结阶段。
- 新传感器页的同步投递方式不符合 LatestValueMailbox 的异步调度要求。改用 UI 队列投递，并明确验证刚进入就暂停仍有 4 窗格、每窗格 2 路数据的首帧。
- 平滑面积图原展示文案绑定旧示例数据。新增通用数值展示模式，新 Demo 使用原创合成数据；保留旧模式兼容性。

## 验收边界

Headless 测试执行参数选项切换、主题、暂停/恢复、快速切页、关闭与数据源释放。原生桌面程序实际启动 Avalonia 平台后逐页检查并渲染截图；这些结果不等同于所有物理鼠标、键盘和操作系统输入路径均已覆盖。

本次没有宣称高 DPI、IME、长期持续运行、输入延迟或真实显示帧率专项通过，也没有将 CPU 曲面渲染改为 GPU 后端。Linux 原生窗口尚未在本机验证；Linux CI 仅覆盖其构建和 Headless 测试。

旧 Demo 中尚未提供新界面的能力见 [覆盖范围](COVERAGE.zh-CN.md)。Azure 主产品仍使用原实现，切换到新包需要单独消除重复类型；不能将独立包消费检查当作主产品集成验收。
