using System.IO.Pipes;
using AgentCallback.Infrastructure;

namespace AgentCallback.Transport.NamedPipe;

public sealed class HostPipeServer
{
    private readonly AppPaths _paths;
    private readonly Func<HostRequest, CancellationToken, Task<HostResponse>> _handler;

    public HostPipeServer(
        AppPaths paths,
        Func<HostRequest, CancellationToken, Task<HostResponse>> handler)
    {
        _paths = paths;
        _handler = handler;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _paths.PipeName,
                PipeDirection.InOut,
                4,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                var request = await PipeFrameCodec.ReadAsync<HostRequest>(pipe, cancellationToken);
                var response = request.ProtocolVersion == HostProtocol.Version
                    ? await InvokeHandlerAsync(request, cancellationToken)
                    : HostResponse.Failure(
                        $"Host protocol mismatch: expected {HostProtocol.Version}, received {request.ProtocolVersion}.");
                await PipeFrameCodec.WriteAsync(pipe, response, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Agent Callback pipe request failed: {exception.Message}");
            }
        }
    }

    private async Task<HostResponse> InvokeHandlerAsync(
        HostRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _handler(request, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HostResponse.Failure(exception.Message);
        }
    }
}
