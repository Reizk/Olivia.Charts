using System.Threading.Channels;

namespace Olivia.Core.Streaming;

/// <summary>One pending tick, with a single reader and no catch-up queue. The supplied clock
/// may fail to create a timer without leaving a partially constructed finalizable object.</summary>
internal sealed class CoalescingPeriodicTimer : IDisposable
{
    private readonly Channel<bool> _ticks = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
        AllowSynchronousContinuations = false
    });
    private readonly ITimer _timer;

    public CoalescingPeriodicTimer(TimeSpan interval, TimeProvider clock)
    {
        _timer = clock.CreateTimer(static state =>
            ((ChannelWriter<bool>)state!).TryWrite(true), _ticks.Writer, interval, interval);
    }

    public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellation)
    {
        while (await _ticks.Reader.WaitToReadAsync(cancellation).ConfigureAwait(false))
            if (_ticks.Reader.TryRead(out _)) return true;
        return false;
    }

    public void Dispose()
    {
        _timer.Dispose();
        _ticks.Writer.TryComplete();
    }
}
