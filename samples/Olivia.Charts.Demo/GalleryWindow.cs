using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Olivia.Avalonia;

namespace Olivia.Charts.Demo;

public sealed class GalleryWindow : Window
{
    private readonly Bitmap _brandMark;
    private readonly Grid _root = new() { ColumnDefinitions = new("216,*"), RowDefinitions = new("74,*") };
    private readonly Grid _workspace = new() { ColumnDefinitions = new("*,252"), ColumnSpacing = 20 };
    private readonly StackPanel _navigation = new() { Spacing = 3 };
    private readonly StackPanel _options = new() { Spacing = 15 };
    private readonly ContentControl _host = new() { Margin = new Thickness(12, 6, 12, 12) };
    private readonly TextBlock _title = new() { FontSize = 29, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _description = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _state = new() { Name = "ViewState", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _stateDot = new() { Width = 7, Height = 7, CornerRadius = new(4), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _dataNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 23 };
    private readonly TextBlock _eyebrow = new() { FontSize = 11 };
    private readonly TextBlock _resultCount = new() { FontSize = 11 };
    private readonly TextBox _search = new() { Name = "SearchExamples", PlaceholderText = "搜索图表…", Margin = new(0, 15, 0, 10) };
    private readonly ComboBox _group = new() { Name = "ExampleGroup", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 12) };
    private readonly Button _pause = Action("暂停更新", "PauseExample", true);
    private readonly Button _reset = Action("重置视图", "ResetExample");
    private readonly Button _follow = Action("回到实时", "FollowLatest");
    private readonly Button _theme = Action("深色外观", "ToggleTheme");
    private readonly Button _settings = Action("收起参数", "ToggleSettings");
    private readonly Border _chartCard = new() { CornerRadius = new(13), BorderThickness = new(1), ClipToBounds = true };
    private readonly Border _settingsCard = new() { Name = "SettingsPanel", CornerRadius = new(13), BorderThickness = new(1), Padding = new(20) };
    private readonly Border _sidebar = new() { BorderThickness = new(0, 0, 1, 0) };
    private readonly Border _masthead = new() { BorderThickness = new(0, 0, 0, 1) };
    private readonly Border _toolbar = new() { BorderThickness = new(0, 0, 0, 1), Padding = new(20, 14) };
    private readonly Border _dataCard = new() { CornerRadius = new(9), Padding = new(14) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private long _navigationVersion;
    private bool _dark, _closing, _allowClose, _settingsVisible = true, _compact;
    public ChartScene? CurrentScene { get; private set; }
    public ChartExample? CurrentExample { get; private set; }
    public Task Shutdown { get; private set; } = Task.CompletedTask;
    public string? LastError { get; private set; }

    public GalleryWindow()
    {
        Title = "Olivia Charts · 图表工作台";
        var iconUri = new Uri("avares://Olivia.Charts.Demo/Assets/chart-icon.png");
        using (var stream = AssetLoader.Open(iconUri)) Icon = new WindowIcon(stream);
        using (var stream = AssetLoader.Open(iconUri)) _brandMark = new Bitmap(stream);
        Closed += (_, _) => _brandMark.Dispose();
        Width = 1440; Height = 920; MinWidth = 1000; MinHeight = 740;
        FontFamily = new("Inter, Microsoft YaHei UI, Noto Sans CJK SC, sans-serif");
        Styles.Add(new StyleInclude(new Uri("avares://Olivia.Charts.Demo/")) { Source = new("avares://Olivia.Charts.Demo/GalleryStyles.axaml") });
        var top = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(24, 0), VerticalAlignment = VerticalAlignment.Center };
        var logo = new Image { Name = "DemoLogo", Width = 50, Height = 50, Source = _brandMark };
        RenderOptions.SetBitmapInterpolationMode(logo, BitmapInterpolationMode.HighQuality);
        top.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { logo,
            new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = {
                new TextBlock { Text = "Olivia Charts", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Brush("#F4E9D2") },
                new TextBlock { Text = "数据可视化工作台", FontSize = 10, Foreground = Brush("#F4E9D2"), Opacity = .75 } } } } });
        var topActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = $"{ExampleCatalog.All.Count} 个精选示例", FontSize = 12, Foreground = Brush("#F4E9D2"), Opacity = .75, VerticalAlignment = VerticalAlignment.Center }, _theme } };
        Grid.SetColumn(topActions, 1); top.Children.Add(topActions); _masthead.Child = top;
        Grid.SetColumnSpan(_masthead, 2); _root.Children.Add(_masthead);

        var sidebar = new DockPanel { Margin = new(14, 22, 14, 16) };
        var navHeader = new StackPanel { Children = { new TextBlock { Text = "图表库", FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new(10, 0) }, _search, _group, _resultCount } };
        DockPanel.SetDock(navHeader, Dock.Top); sidebar.Children.Add(navHeader);
        var footer = new TextBlock { Text = "本地演示 · 无需联网", FontSize = 11, Opacity = .5, Margin = new(8, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); sidebar.Children.Add(footer);
        sidebar.Children.Add(new ScrollViewer { Content = _navigation, Margin = new(0, 8, 0, 0) });
        _sidebar.Child = sidebar; Grid.SetRow(_sidebar, 1); _root.Children.Add(_sidebar);

        var body = new Grid { Margin = new(28, 22, 28, 16), RowDefinitions = new("Auto,*,Auto"), RowSpacing = 20 };
        Grid.SetColumn(body, 1); Grid.SetRow(body, 1); _root.Children.Add(body);
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 14 };
        header.Children.Add(new StackPanel { Spacing = 7, Children = { _eyebrow, _title, _description } });
        _settings.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(_settings, 1); header.Children.Add(_settings); body.Children.Add(header);
        Grid.SetRow(_workspace, 1); body.Children.Add(_workspace);
        var chartBody = new Grid { RowDefinitions = new("Auto,*") };
        var toolbar = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        toolbar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _stateDot, _state } });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _follow, _reset, _pause } };
        Grid.SetColumn(tools, 1); toolbar.Children.Add(tools); _toolbar.Child = toolbar; chartBody.Children.Add(_toolbar);
        Grid.SetRow(_host, 1); chartBody.Children.Add(_host); _chartCard.Child = chartBody; _workspace.Children.Add(_chartCard);

        var settings = new StackPanel { Spacing = 20 };
        settings.Children.Add(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = "图表设置", FontSize = 15, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = "调整展示方式，探索数据细节", FontSize = 11, Opacity = .6 } } });
        settings.Children.Add(_options);
        _dataCard.Child = new StackPanel { Spacing = 9, Children = { new TextBlock { Text = "示例数据", FontSize = 11, FontWeight = FontWeight.SemiBold }, _dataNote } };
        settings.Children.Add(_dataCard);
        settings.Children.Add(new TextBlock { Text = "操作提示\n\n悬停或点击查看数值。支持的图表可滚轮缩放、拖动平移，也可使用键盘操作。", FontSize = 12, LineHeight = 21, TextWrapping = TextWrapping.Wrap, Opacity = .6 });
        _settingsCard.Child = new ScrollViewer { Content = settings };
        Grid.SetColumn(_settingsCard, 1); _workspace.Children.Add(_settingsCard);
        Grid.SetRow(_status, 2); body.Children.Add(_status); Content = _root;

        _group.ItemsSource = new[] { "所有分类" }.Concat(ExampleCatalog.All.Select(e => e.Group).Distinct()).ToArray(); _group.SelectedIndex = 0;
        _group.SelectionChanged += (_, _) => RefreshNavigation(); _search.TextChanged += (_, _) => RefreshNavigation();
        _theme.Click += (_, _) => SetDarkTheme(!_dark);
        _pause.Click += (_, _) => { if (CurrentScene is { } scene) scene.SetPaused(!scene.IsPaused); RefreshStatus(); };
        _reset.Click += (_, _) => { CurrentScene?.Reset?.Invoke(); RefreshStatus(); };
        _follow.Click += (_, _) => { if (CurrentScene is { } scene) { scene.SetPaused(false); scene.FollowLatest?.Invoke(); } RefreshStatus(); };
        _settings.Click += (_, _) => { _settingsVisible = !_settingsVisible; ArrangeSettings(); };
        SizeChanged += (_, _) => { var compact = Bounds.Width < 1180; if (_compact != compact) { _compact = compact; _settingsVisible = !compact; ArrangeSettings(); } };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        Opened += async (_, _) => { _statusTimer.Start(); if (CurrentScene is null) await NavigateSafelyAsync("line"); };
        Closing += async (_, e) =>
        {
            if (_allowClose) return; e.Cancel = true; if (_closing) return;
            try { await StopAsync(); } finally { _allowClose = true; Close(); }
        };
        SetDarkTheme(false); RefreshNavigation();
    }
    private void ArrangeSettings()
    {
        _settingsCard.IsVisible = _settingsVisible;
        _workspace.ColumnDefinitions[1].Width = new GridLength(_settingsVisible ? _compact ? 228 : 252 : 0);
        _workspace.ColumnSpacing = _settingsVisible ? 20 : 0;
        _settings.Content = _settingsVisible ? "收起参数" : "显示参数";
    }
    public void SetDarkTheme(bool dark)
    {
        _dark = dark; RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _theme.Content = dark ? "浅色外观" : "深色外观";
        void Resource(string name, string light, string night) => Resources[name] = Brush(dark ? night : light);
        Resource("ChartSurface", "#FFFEFA", "#11291F"); Resource("ChartText", "#243B30", "#F3ECD8");
        Resource("ChartBorder", "#DDE5DC", "#2B4538"); Resource("ChartAccent", "#0C432D", "#94C9A5");
        Resource("ChartPrimary", "#0C432D", "#1D6245"); Resource("ChartPrimaryHover", "#195E40", "#287756");
        Resource("ChartOnPrimary", "#FFF9EB", "#FFF9EB"); Resource("ChartHover", "#F0F5ED", "#1C392C");
        Resource("ChartSelected", "#E3EDE2", "#244B37");
        Resources["SystemAccentColor"] = Color.Parse(dark ? "#94C9A5" : "#0C432D");
        Background = Brush(dark ? "#0A1813" : "#F3F5EF"); Foreground = Palette().Text;
        _chartCard.Background = _settingsCard.Background = _sidebar.Background = Palette().Surface;
        _masthead.Background = Brush("#0C432D");
        _chartCard.BorderBrush = _settingsCard.BorderBrush = _masthead.BorderBrush = _sidebar.BorderBrush = _toolbar.BorderBrush = (IBrush)Resources["ChartBorder"]!;
        _dataCard.Background = Brush(dark ? "#1A3527" : "#F0F4EA");
        _eyebrow.Foreground = _description.Foreground = _status.Foreground = _resultCount.Foreground = Palette().Muted;
        CurrentScene?.ApplyTheme(Palette(), dark); RefreshStatus();
    }
    private UiPalette Palette() => new(Brush(_dark ? "#11291F" : "#FFFEFA"), Brush(_dark ? "#F3ECD8" : "#243B30"),
        Brush(_dark ? "#A1B3A4" : "#63786C"), Brush(_dark ? "#284237" : "#E7EDE2"),
        Brush(_dark ? "#94C9A5" : "#0C432D"), Brush(_dark ? "#244B37" : "#E3EDE2"), Brush(_dark ? "#DFB786" : "#AB6D35"));
    public void Filter(string text, string? group = null) { _search.Text = text; _group.SelectedItem = group ?? "所有分类"; RefreshNavigation(); }
    private void RefreshNavigation()
    {
        var examples = ExampleCatalog.Search(_search.Text, _group.SelectedIndex <= 0 ? null : _group.SelectedItem as string).ToArray();
        _resultCount.Text = $"{examples.Length} 个示例"; _navigation.Children.Clear();
        foreach (var group in examples.GroupBy(e => e.Group))
        {
            _navigation.Children.Add(new TextBlock { Text = group.Key, FontSize = 10, Opacity = .5, Margin = new(10, 16, 0, 7) });
            foreach (var item in group)
            {
                var row = new Grid { ColumnDefinitions = new("22,*"), ColumnSpacing = 8 };
                row.Children.Add(new PathIcon { Width = 15, Height = 15, Opacity = .7, Data = Geometry.Parse(ExampleIconPath(item.Id)) });
                var label = new TextBlock { Text = item.Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(label, 1); row.Children.Add(label);
                var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, Tag = item.Id };
                button.Classes.Add("navItem"); if (CurrentExample?.Id == item.Id) button.Classes.Add("selected");
                button.Click += async (_, _) => await NavigateSafelyAsync(item.Id); _navigation.Children.Add(button);
            }
        }
        if (examples.Length == 0) _navigation.Children.Add(new TextBlock { Text = "没有匹配的图表\n试试其他关键词或分类。", Margin = new(10, 20), TextWrapping = TextWrapping.Wrap, Opacity = .6 });
    }
    private static string ExampleIconPath(string id) => id switch
    {
        "bars" => "M1,12 L5,12 L5,22 L1,22 Z M9,3 L13,3 L13,22 L9,22 Z M17,8 L21,8 L21,22 L17,22 Z",
        "proportion" or "sunburst" => "M11,1 A10,10 0 1 0 21,11 L11,11 Z M14,1 L14,8 L21,8 A10,10 0 0 0 14,1 Z",
        "heatmap" or "treemap" => "M1,1 L10,1 L10,10 L1,10 Z M13,1 L22,1 L22,10 L13,10 Z M1,13 L10,13 L10,22 L1,22 Z M13,13 L22,13 L22,22 L13,22 Z",
        _ => "M1,17 L3,19 L8,10 L13,15 L22,4 L20,2 L13,11 L8,6 Z"
    };
    private async Task NavigateSafelyAsync(string id)
    {
        try { await SelectExampleAsync(id); } catch (Exception error) { LastError = error.Message; _status.Text = "示例加载失败：" + error.Message; }
    }
    public async Task SelectExampleAsync(string id)
    {
        var example = ExampleCatalog.All.Single(e => e.Id == id); if (_closing) return;
        var version = ++_navigationVersion; await _navigationGate.WaitAsync();
        try
        {
            if (_closing || version != _navigationVersion) return;
            _host.Content = null; if (CurrentScene is { } previous) await previous.DisposeAsync(); CurrentScene = null;
            if (_closing || version != _navigationVersion) return;
            var scene = example.Create(); CurrentScene = scene; CurrentExample = example; LastError = null;
            scene.ApplyTheme(Palette(), _dark); _title.Text = example.Title; _eyebrow.Text = "图表库  /  " + example.Group;
            _description.Text = example.Description; _dataNote.Text = example.DataNote + "\n原创合成数据";
            _options.Children.Clear(); foreach (var option in scene.Options) _options.Children.Add(option);
            if (scene.Options.Count == 0) _options.Children.Add(new TextBlock { Text = "直接在图表中探索", FontSize = 12, Opacity = .6 });
            _pause.IsVisible = scene.Tick is not null; _reset.IsVisible = scene.Reset is not null; _follow.IsVisible = scene.FollowLatest is not null;
            _host.Content = scene.View; scene.Activate(); RefreshNavigation(); RefreshStatus();
        }
        finally { _navigationGate.Release(); }
    }
    private void RefreshStatus()
    {
        if (CurrentScene is not { } scene || scene.IsClosed) return;
        _state.Text = scene.IsPaused ? "已暂停" : scene.ReadViewState?.Invoke() ?? (scene.Tick is null ? "静态数据" : "实时更新");
        _stateDot.Background = Brush(scene.IsPaused || scene.IsFollowingLatest?.Invoke() == false ? "#C49B54" : scene.Tick is null ? "#809482" : "#46976A");
        _status.Text = $"{CurrentExample?.DataNote}  ·  本地合成数据";
        _pause.Content = scene.IsPaused ? "继续更新" : "暂停更新";
        _follow.IsEnabled = scene.IsFollowingLatest?.Invoke() != true || scene.IsPaused;
    }
    public Task StopAsync()
    {
        if (_closing) return Shutdown; _closing = true; ++_navigationVersion; _statusTimer.Stop(); Shutdown = ReleaseAsync(); return Shutdown;
    }
    private async Task ReleaseAsync()
    {
        await _navigationGate.WaitAsync();
        try { _host.Content = null; if (CurrentScene is { } scene) await scene.DisposeAsync(); CurrentScene = null; }
        finally { _navigationGate.Release(); }
    }
    private static Button Action(string text, string name, bool primary = false)
    {
        var button = new Button { Content = text, Name = name, VerticalAlignment = VerticalAlignment.Center };
        button.Classes.Add("action"); if (primary) button.Classes.Add("primary"); return button;
    }
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
