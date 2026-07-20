using System.IO.Pipes;
using System.Text.Json;
using AgentCallback.Infrastructure;

namespace AgentCallback.Transport.NamedPipe;

public sealed class HostPipeClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private readonly AppPaths _paths;

    public HostPipeClient(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<TResponse> CallAsync<TRequest, TResponse>(
        string operation,
        TRequest request,
        CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            _paths.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HostUnavailableException(
                "Agent Callback Host is not reachable. Start it with `agent-callback host run`.");
        }
        catch (IOException exception)
        {
            throw new HostUnavailableException(
                "Agent Callback Host is not reachable. Start it with `agent-callback host run`.",
                exception);
        }

        var payload = JsonSerializer.SerializeToElement(request, HostJson.Options);
        await PipeFrameCodec.WriteAsync(
            pipe,
            new HostRequest(HostProtocol.Version, operation, payload),
            cancellationToken);
        var response = await PipeFrameCodec.ReadAsync<HostResponse>(pipe, cancellationToken);
        if (response.ProtocolVersion != HostProtocol.Version)
        {
            throw new InvalidOperationException(
                $"Host protocol mismatch: expected {HostProtocol.Version}, received {response.ProtocolVersion}.");
        }

        if (!response.Ok)
        {
            throw new InvalidOperationException(response.Error ?? "Agent Callback Host rejected the request.");
        }

        if (!response.Payload.HasValue)
        {
            throw new InvalidDataException("Agent Callback Host returned no payload.");
        }

        return response.Payload.Value.Deserialize<TResponse>(HostJson.Options) ??
            throw new InvalidDataException("Agent Callback Host returned an invalid payload.");
    }
}

public sealed class HostUnavailableException : IOException
{
    public HostUnavailableException(string message)
        : base(message)
    {
    }

    public HostUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
