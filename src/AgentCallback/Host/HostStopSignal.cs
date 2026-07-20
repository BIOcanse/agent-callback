namespace AgentCallback.Host;

public sealed class HostStopSignal : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private int _requested;

    public CancellationToken Token => _source.Token;

    public void RequestAfterResponse()
    {
        if (Interlocked.Exchange(ref _requested, 1) != 0)
        {
            return;
        }

        _ = CancelAfterResponseAsync();
    }

    private async Task CancelAfterResponseAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await _source.CancelAsync();
    }

    public void Dispose() => _source.Dispose();
}
