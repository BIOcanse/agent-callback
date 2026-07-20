using System.Text.Json;
using AgentCallback.Infrastructure;
using AgentCallback.Mcp;
using AgentCallback.Transport.NamedPipe;

namespace AgentCallback.Tests;

public sealed class McpServerTests
{
    [Fact]
    public async Task Initialize_AcceptsUtf8BomAndReturnsProtocolMetadata()
    {
        var paths = new AppPaths(Path.GetTempPath(), "unused.db", "unused-pipe");
        var server = new McpServer(new HostPipeClient(paths));
        using var input = new StringReader(
            "\uFEFF{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n");
        using var output = new StringWriter();

        await server.RunAsync(input, output, CancellationToken.None);

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.Equal("agent-callback", result.GetProperty("serverInfo").GetProperty("name").GetString());
    }
}
