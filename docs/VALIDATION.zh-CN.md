# 独立版本验证记录

2026-10-25。结果仅代表各版本实际执行的检查。

## Demo 精简与圆形图标（alpha.2 后续）

- 移除“数据与配置”分类以及字段聚合、散点配置两个示例及其专用代码，Demo 当前为 25 页；同步搜索检查和覆盖范围文档。
- 内置 imagegen 生成圆形图标，保留透明 PNG 原件，并转换为界面 PNG 和含 7 个尺寸的 Windows ICO。顶栏、窗口和程序文件均接入；从编译后的 EXE 提取图标确认正确。
- Release 构建为 0 警告、0 错误；91 项 Headless 检查通过（0 失败/跳过）。Windows 原生 25 页明暗渲染通过，共 50 张页面截图；传感器缩放、平移和恢复跟随检查仍通过。
- 此次仅调整 Demo 和文档，图表库和包版本保持 alpha.2。

## 0.1.0-alpha.2：界面改版与实时交互修复

- 重做图表工作台布局和控件样式，统一明暗配色；参数面板可折叠，检查了 1000×760 窗口下图表和设置均可用。
- 增加独立 LinePlot 与“基础图表 / 曲线图”，验证平滑、折线、阶梯三种模式均有有效几何，数据点选择仍返回原始观测，缩放及重置生效。
- 移除 Demo 的三维散点入口和示例生成代码，保留库中的既有 API 以兼容已有消费者；三维曲面不受影响。新增一个、移除一个，仍为 27 页。
- 先用控件滚轮事件复现“缩放退出跟随”的失败，再修复 Demo 接线。缩放保持原有实时状态，平移/拖动进入历史模式，Home 与工具栏恢复跟随；暂停/继续单独测试。补齐 PanRequested、WindowRequested、FollowRequested 的订阅和清理。
- 93 项 Headless 检查通过（0 失败/跳过），覆盖上述真实控件滚轮、拖动、键盘和按钮路径；原生 Windows 27 页明暗渲染通过。两个 alpha.2 包已生成，使用独立新包缓存的消费检查通过。
- Core 算法未修改，沿用下方 1,136 项初版核心基线；远程完整结果以对应提交的 Windows/Linux 工作流为准。物理设备输入、高 DPI 和长期显示性能仍不宣称完成。

下方为 alpha.1 的独立抽取基线，版本号和原始计数保留。

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
