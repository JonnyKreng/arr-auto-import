using ArrImportSolver;

namespace ArrImportSolver.Tests;

public class PollNotifierTests
{
    [Fact]
    public async Task Signal_WritesToChannel()
    {
        var notifier = new PollNotifier();
        notifier.Signal();
        var result = await notifier.WaitAsync(CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task WaitAsync_CanBeCancelled()
    {
        var notifier = new PollNotifier();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await notifier.WaitAsync(cts.Token);
        });
    }
}
