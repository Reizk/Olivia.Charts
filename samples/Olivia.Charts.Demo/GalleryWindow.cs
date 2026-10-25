using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Olivia.Avalonia;
using Olivia.Avalonia.Themes;

namespace Olivia.Charts.Demo;

public sealed class GalleryWindow : Window
{
    private readonly Grid _root = new() { ColumnDefinitions = new ColumnDefinitions("232,*") };
    private readonly StackPanel _navigation = new() { Spacing = 3 };
    private readonly StackPanel _options = new() { Spacing = 13 };
    private readonly ContentControl _host = new();
    private readonly TextBlock _title = new() { FontSize = 27, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _description = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 46 };
    private readonly TextBlock _dataNote = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _eyebrow = new() { FontSize = 11, LetterSpacing = 1.7 };
    private readonly TextBlock _resultCount = new() { FontSize = 11 };
    private readonly TextBox _search = new() { Name = "SearchExamples", PlaceholderText = "搜索图表…", Margin = new Thickness(0, 20, 0, 10) };
    private readonly ComboBox _group = new() { Name = "ExampleGroup", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
    private readonly Button _pause = new() { Name = "PauseExample", Content = "暂停", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _reset = new() { Name = "ResetExample", Content = "重置视图", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button _theme = new() { Name = "ToggleTheme", Content = "深色模式", VerticalAlignment = VerticalAlignment.Top, Padding = new Thickness(16, 9) };
    private readonly Border _chartCard = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12), ClipToBounds = true };
    private readonly Border _settingsCard = new() { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(20) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly SemaphoreSlim _navigationGate = new(1, 1);
    private long _navigationVersion;
    private bool _dark, _closing, _allowClose;
    public ChartScene? CurrentScene { get; private set; }
    public ChartExample? CurrentExample { get; private set; }
    public Task Shutdown { get; private set; } = Task.CompletedTask;
    public string? LastError { get; private set; }

    public GalleryWindow()
    {
        Title = "Olivia Charts · 图表探索";
        Width = 1440; Height = 920; MinWidth = 1180; MinHeight = 760;
        FontFamily = new FontFamily("Inter, Microsoft YaHei UI, Noto Sans CJK SC, sans-serif");
        var sidebar = new DockPanel { Margin = new Thickness(18, 24, 18, 18) };
        var brand = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "OLIVIA", FontSize = 25, FontWeight = FontWeight.Bold, LetterSpacing = 4, Foreground = Brushes.White },
                new TextBlock { Text = "C H A R T S", FontSize = 10, Foreground = Brush("#A8A4C5"), Margin = new Thickness(1, 3, 0, 0) },
                _search, _group, _resultCount
            }
        };
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var footer = new StackPanel { Spacing = 5, Margin = new Thickness(4, 18, 0, 0) };
        footer.Children.Add(new TextBlock { Text = "27 个图表示例", Foreground = Brush("#DDD9F1"), FontSize = 12 });
        footer.Children.Add(new TextBlock { Text = "原创合成数据 · 可重复探索", Foreground = Brush("#9691AF"), FontSize = 10 });
        DockPanel.SetDock(footer, Dock.Bottom); sidebar.Children.Add(footer);
        sidebar.Children.Add(new ScrollViewer { Content = _navigation, Margin = new Thickness(0, 12, 0, 0) });
        var sidebarFrame = new Border { Background = Brush("#211E32"), Child = sidebar };
        _root.Children.Add(new ThemeVariantScope { RequestedThemeVariant = ThemeVariant.Dark, Child = sidebarFrame });

        var body = new Grid { Margin = new Thickness(28, 22, 28, 16), RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 20 };
        Grid.SetColumn(body, 1); _root.Children.Add(body);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        header.Children.Add(new StackPanel { Spacing = 5, Children = { _eyebrow, _title, _description } });
        _theme.Click += (_, _) => SetDarkTheme(!_dark);
        Grid.SetColumn(_theme, 1); header.Children.Add(_theme); body.Children.Add(header);

        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("*,238"), ColumnSpacing = 18 };
        Grid.SetRow(workspace, 1); body.Children.Add(workspace);
        _chartCard.Child = _host; workspace.Children.Add(_chartCard);
        var settings = new StackPanel { Spacing = 20 };
        settings.Children.Add(new TextBlock { Text = "探索设置", FontSize = 16, FontWeight = FontWeight.SemiBold });
        settings.Children.Add(_options);
        settings.Children.Add(new StackPanel { Spacing = 8, Children = { _pause, _reset } });
        settings.Children.Add(new Border { Height = 1, Background = Brush("#DCD9E5"), Margin = new Thickness(0, 2) });
        settings.Children.Add(new TextBlock { Text = "关于数据", FontSize = 12, FontWeight = FontWeight.SemiBold });
        settings.Children.Add(_dataNote);
        settings.Children.Add(new TextBlock { Text = "点击图形查看数值。支持的图表可使用滚轮缩放、拖动平移及方向键选择。", FontSize = 12, TextWrapping = TextWrapping.Wrap, Opacity = .7 });
        _settingsCard.Child = new ScrollViewer { Content = settings };
        Grid.SetColumn(_settingsCard, 1); workspace.Children.Add(_settingsCard);
        Grid.SetRow(_status, 2); body.Children.Add(_status);
        Content = _root;

        _group.ItemsSource = new[] { "所有分类" }.Concat(ExampleCatalog.All.Select(e => e.Group).Distinct()).ToArray();
        _group.SelectedIndex = 0;
        _group.SelectionChanged += (_, _) => RefreshNavigation();
        _search.TextChanged += (_, _) => RefreshNavigation();
        _pause.Click += (_, _) => { if (CurrentScene is { } scene) { scene.SetPaused(!scene.IsPaused); RefreshStatus(); } };
        _reset.Click += (_, _) => { CurrentScene?.Reset?.Invoke(); RefreshStatus(); };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        Opened += async (_, _) => { _statusTimer.Start(); if (CurrentScene is null) await NavigateSafelyAsync("bars"); };
        Closing += async (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            if (_closing) return;
            try { await StopAsync(); }
            finally { _allowClose = true; Close(); }
        };
        SetDarkTheme(false); RefreshNavigation();
    }

    public void SetDarkTheme(bool dark)
    {
        _dark = dark; RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        _theme.Content = dark ? "浅色模式" : "深色模式";
        Background = Brush(dark ? "#171922" : "#F3F3F8");
        Foreground = Brush(dark ? "#EBECF5" : "#272439");
        _chartCard.Background = _settingsCard.Background = Brush(dark ? "#222531" : "#FFFFFF");
        _chartCard.BorderBrush = _settingsCard.BorderBrush = Brush(dark ? "#343747" : "#E3E1EB");
        _eyebrow.Foreground = Brush(dark ? "#ABA0FF" : "#6C56C8");
        _description.Foreground = _status.Foreground = Brush(dark ? "#B2B4C5" : "#747185");
        CurrentScene?.ApplyTheme(Palette(), dark);
    }
    private UiPalette Palette() => (_dark ? OliviaThemeDefinition.VS2019Dark : OliviaThemeDefinition.Win11Light).Palette;

    public void Filter(string text, string? group = null)
    {
        _search.Text = text;
        _group.SelectedItem = group ?? "所有分类";
        RefreshNavigation();
    }
    private void RefreshNavigation()
    {
        var examples = ExampleCatalog.Search(_search.Text, _group.SelectedIndex <= 0 ? null : _group.SelectedItem as string).ToArray();
        _resultCount.Text = $"{examples.Length} 个示例"; _resultCount.Foreground = Brush("#AAA5C0");
        _navigation.Children.Clear();
        foreach (var group in examples.GroupBy(e => e.Group))
        {
            _navigation.Children.Add(new TextBlock { Text = group.Key, Foreground = Brush("#9D96B6"), FontSize = 10, Margin = new Thickness(8, 13, 0, 5) });
            foreach (var item in group)
            {
                var button = new Button
                {
                    Content = item.Title, HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8), FontSize = 12,
                    Background = Brush(CurrentExample?.Id == item.Id ? "#6551AA" : "#211E32"), Foreground = Brush("#EEEAF7"),
                    BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6), Tag = item.Id
                };
                button.Click += async (_, _) => await NavigateSafelyAsync(item.Id);
                _navigation.Children.Add(button);
            }
        }
        if (examples.Length == 0) _navigation.Children.Add(new TextBlock { Text = "没有匹配的图表\n试试其他关键词或分类。", Foreground = Brush("#CCC5DE"), Margin = new Thickness(8, 20), TextWrapping = TextWrapping.Wrap });
    }
    private async Task NavigateSafelyAsync(string id)
    {
        try { await SelectExampleAsync(id); }
        catch (Exception error) { LastError = error.Message; _status.Text = "示例加载失败：" + error.Message; }
    }
    public async Task SelectExampleAsync(string id)
    {
        var example = ExampleCatalog.All.Single(e => e.Id == id);
        if (_closing) return;
        var version = ++_navigationVersion;
        await _navigationGate.WaitAsync();
        try
        {
            if (_closing || version != _navigationVersion) return;
            _host.Content = null;
            if (CurrentScene is { } previous) await previous.DisposeAsync();
            CurrentScene = null;
            if (_closing || version != _navigationVersion) return;
            var scene = example.Create();
            CurrentScene = scene; CurrentExample = example; LastError = null;
            scene.ApplyTheme(Palette(), _dark);
            _title.Text = example.Title; _eyebrow.Text = "CHART GALLERY  /  " + example.Group;
            _description.Text = example.Description; _dataNote.Text = example.DataNote + "\n\n数据在本地生成，不连接外部服务。";
            _options.Children.Clear();
            foreach (var option in scene.Options) _options.Children.Add(option);
            if (scene.Options.Count == 0) _options.Children.Add(new TextBlock { Text = "直接在图表中探索", FontSize = 12, Opacity = .7 });
            _pause.IsVisible = scene.Tick is not null; _reset.IsVisible = scene.Reset is not null;
            _host.Content = scene.View; scene.Activate();
            RefreshNavigation(); RefreshStatus();
        }
        finally { _navigationGate.Release(); }
    }
    private void RefreshStatus()
    {
        if (CurrentScene is not { } scene || scene.IsClosed) return;
        _status.Text = $"{CurrentExample?.DataNote} · {(scene.Tick is null ? "静态数据" : scene.IsPaused ? "已暂停" : "实时更新")}";
        _pause.Content = scene.IsPaused ? "继续" : "暂停";
    }
    public Task StopAsync()
    {
        if (_closing) return Shutdown;
        _closing = true; ++_navigationVersion; _statusTimer.Stop();
        Shutdown = ReleaseAsync();
        return Shutdown;
    }
    private async Task ReleaseAsync()
    {
        await _navigationGate.WaitAsync();
        try
        {
            _host.Content = null;
            if (CurrentScene is { } scene) await scene.DisposeAsync();
            CurrentScene = null;
        }
        finally { _navigationGate.Release(); }
    }
    private static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));
}
