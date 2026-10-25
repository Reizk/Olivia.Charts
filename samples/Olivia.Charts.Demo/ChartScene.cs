using Avalonia.Controls;
using Avalonia.Threading;
using Olivia.Avalonia;

namespace Olivia.Charts.Demo;

public sealed record ChartExample(string Id, string Title, string Group, string Description,
    string DataNote, Func<ChartScene> Create);

/// <summary>Owns one visible example. Navigation stops its timer before releasing sources.</summary>
public sealed class ChartScene : IAsyncDisposable
{
    private DispatcherTimer? _timer;
    private Task? _shutdown;
    public required Control View { get; init; }
    public required Action<UiPalette, bool> ApplyTheme { get; init; }
    public required Func<string> ReadStatus { get; init; }
    public required Func<Task> Release { get; init; }
    public Action? Reset { get; init; }
    public Action? Tick { get; init; }
    public Func<string>? ReadViewState { get; init; }
    public Func<bool>? IsFollowingLatest { get; init; }
    public Action? FollowLatest { get; init; }
    public List<Control> Options { get; } = [];
    public bool IsPaused { get; private set; }
    public bool IsClosed => _shutdown is not null;
    public long TickCount { get; private set; }
    public event Action? Changed;
    public void Activate()
    {
        if (Tick is null || _timer is not null || IsClosed) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        _timer.Start();
    }
    private void OnTick(object? sender, EventArgs args) => Advance();
    public void Advance()
    {
        if (IsClosed || IsPaused || Tick is null) return;
        Tick(); TickCount++; Changed?.Invoke();
    }
    public void SetPaused(bool paused)
    {
        if (IsClosed || Tick is null) return;
        IsPaused = paused;
        Changed?.Invoke();
    }
    public ValueTask DisposeAsync()
    {
        if (_shutdown is not null) return new(_shutdown);
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnTick; _timer = null; }
        Changed = null;
        _shutdown = Release();
        return new(_shutdown);
    }
}
