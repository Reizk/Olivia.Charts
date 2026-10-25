# Olivia Charts Demo 功能简介与 Feature Overview

0.1.0-alpha.3 · 2026-10-25

## 中文简介

Olivia Charts Demo 是一个桌面图表工作台，包含 **5 个分类、24 个示例**，用于体验常用图表、时间序列、金融图表和实时数据展示。界面采用墨绿主色，支持明暗主题；所有示例数据均在本地生成。

### 图表功能

| 分类 | Demo 中的示例 | 主要功能 |
| --- | --- | --- |
| 基础图表（6） | 曲线图、分组柱状图、散点图、环形与饼图、雷达图、迷你图 | 比较趋势、数值、分布、占比和多项指标。曲线可切换平滑、折线与阶梯连接；柱状图可调整排列方式。 |
| 面积与区间（6） | 面积图、曲线与阶梯面积、堆叠面积、堆叠曲线面积、堆叠阶梯面积、区间图 | 展示变化趋势、总量构成和上下界。堆叠图可切换总量与百分比；区间图提供区间柱形与带状面积。 |
| 层次与关系（4） | 热力图、矩形树图、旭日图、流向图 | 用色阶表达数值强弱，用面积和扇区展示层次与占比，用连接展示流量分配；矩形树图支持点击深入分支。 |
| 时间与金融（5） | 历史曲线、时间范围导航、双轴曲线、多系列曲线、金融图表 | 浏览和比较时间序列，通过概览调整主图时间范围。金融图支持 K 线、周期切换、成交量、布林带、CCI、Williams %R，以及趋势线和斐波那契回撤、扇形、弧线工具。 |
| 实时与大数据（3） | 八路实时传感器、实时散点、百万点曲线 | 四个面板展示八路实时信号；实时散点展示点位变化；百万点示例提供两条各 100 万采样点的曲线，可缩放查看局部。 |

### 常用操作

- **查找与设置**：按分类浏览或搜索示例，切换明暗主题，展开或收起右侧参数面板。
- **缩放与平移**：柱状图和散点图可围绕鼠标位置滚轮缩放，按住左键拖动查看其他区域；点击“重置视图”或按 Home 恢复全图。
- **暂停与继续**：八路传感器和实时散点提供“暂停更新”和“继续更新”，方便观察数据。
- **实时跟随**：八路传感器在实时状态下缩放仍跟随新数据。拖动查看历史时采集继续，点击“回到实时”或按 Home 恢复跟随。

参数面板可调整当前图表支持的选项，例如连接方式、数据点、辅助网格、系列显示、量程和金融指标。

## English Overview

Olivia Charts Demo is a desktop chart workbench with **24 examples across 5 categories**. It demonstrates common charts, time series, financial charts, and live data visualization. The interface uses a forest green palette and supports light and dark themes. All sample data is generated locally.

### Chart Features

| Category | Examples in the Demo | Main features |
| --- | --- | --- |
| Basic charts (6) | Line chart, grouped bar chart, scatter plot, doughnut and pie chart, radar chart, sparkline | Compare trends, values, distributions, proportions, and multiple metrics. Switch between smooth, straight, and stepped lines, or change the bar layout. |
| Area and range (6) | Area chart, curved and stepped area, stacked area, stacked spline area, stacked step area, range chart | Show trends, contributions to a total, and upper and lower bounds. Switch stacked charts between totals and percentages, and range charts between bars and bands. |
| Hierarchy and relationships (4) | Heatmap, treemap, sunburst chart, flow chart | Use color to show intensity, areas and sectors to show hierarchy and proportions, and connections to show flow distribution. Click treemap branches to drill down. |
| Time and finance (5) | Historical curve, time range navigator, dual-axis chart, multi-series chart, financial chart | Explore and compare time series, and use an overview to adjust the main chart's time range. Financial tools include candlesticks, time interval selection, volume, Bollinger Bands, CCI, Williams %R, trend lines, and Fibonacci retracements, fans, and arcs. |
| Live and large data (3) | Eight-channel live sensors, live scatter plot, million-point curves | Display eight live signals in four panels, observe moving scatter points, and zoom into two curves with one million samples each. |

### Common Controls

- **Browse and configure**: Filter by category or search for an example, switch themes, and expand or collapse the settings panel.
- **Zoom and pan**: In bar and scatter charts, use the mouse wheel to zoom around the pointer. Drag with the left mouse button to explore other regions. Select “重置视图” (Reset View) or press Home to restore the full view.
- **Pause and resume**: The sensor and live scatter examples provide “暂停更新” (Pause Updates) and “继续更新” (Resume Updates) for closer inspection.
- **Follow live data**: Zooming the sensor chart while following live data keeps the view up to date. Dragging switches to historical browsing while data collection continues. Select “回到实时” (Back to Live) or press Home to resume following.

Use the settings panel to adjust options available for the current chart, such as line connections, data markers, grid lines, series visibility, axis limits, and financial indicators.
