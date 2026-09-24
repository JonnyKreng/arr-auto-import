using System.Threading.Channels;

namespace ArrImportSolver;

// Lets the UI force an immediate worker poll instead of waiting for the next timer tick.
public sealed class PollNotifier
{
    private readonly Channel<bool> _channel = Channel.CreateUnbounded<bool>();

    public void Signal() => _channel.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken ct = default) => _channel.Reader.ReadAsync(ct);
}