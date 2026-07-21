using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentCallback.Application;
using AgentCallback.Domain;
using AgentCallback.Transport.NamedPipe;

namespace AgentCallback.Mcp;

public sealed class McpServer
{
    private const int MaxLineCharacters = 1024 * 1024;
    private readonly HostPipeClient _client;

    public McpServer(HostPipeClient client)
    {
        _client = client;
    }

    public async Task RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            line = line.TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            object? response;
            try
            {
                if (line.Length > MaxLineCharacters)
                {
                    throw new InvalidDataException("MCP request exceeds the 1 MiB limit.");
                }

                using var document = JsonDocument.Parse(line);
                response = await HandleAsync(document.RootElement, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                response = JsonRpcError(id: null, -32603, exception.Message);
            }

            if (response is null)
            {
                continue;
            }

            await output.WriteLineAsync(JsonSerializer.Serialize(response, HostJson.Options));
            await output.FlushAsync(cancellationToken);
        }
    }

    private async Task<object?> HandleAsync(
        JsonElement request,
        CancellationToken cancellationToken)
    {
        var hasId = request.TryGetProperty("id", out var idElement);
        var id = hasId ? idElement.Clone() : (JsonElement?)null;
        var method = request.TryGetProperty("method", out var methodElement)
            ? methodElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(method))
        {
            return JsonRpcError(id, -32600, "JSON-RPC method is required.");
        }

        if (method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var result = method switch
            {
                "initialize" => InitializeResult(),
                "ping" => new { },
                "tools/list" => new { tools = ToolDefinitions() },
                "tools/call" => await CallToolAsync(request, cancellationToken),
                _ => throw new McpMethodNotFoundException(method)
            };
            return JsonRpcResult(id, result);
        }
        catch (McpMethodNotFoundException exception)
        {
            return JsonRpcError(id, -32601, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return JsonRpcResult(id, ToolError(exception.Message));
        }
    }

    private async Task<object> CallToolAsync(
        JsonElement request,
        CancellationToken cancellationToken)
    {
        var parameters = request.TryGetProperty("params", out var paramsElement)
            ? paramsElement
            : throw new InvalidOperationException("tools/call params are required.");
        var name = RequiredString(parameters, "name");
        var arguments = parameters.TryGetProperty("arguments", out var argumentElement)
            ? argumentElement
            : JsonSerializer.SerializeToElement(new { });

        object result = name switch
        {
            "callback_provider_status" => await _client.CallAsync<ProviderStatusRequest, ProviderStatus>(
                "provider.status",
                new ProviderStatusRequest(OptionalString(arguments, "provider") ?? "codex"),
                cancellationToken),
            "callback_register_process" => await RegisterAsync(
                arguments,
                CallbackSourceKind.Process,
                cancellationToken),
            "callback_register_event" => await RegisterAsync(
                arguments,
                CallbackSourceKind.Event,
                cancellationToken),
            "callback_get" => await _client.CallAsync<CallbackIdRequest, CallbackRecord>(
                "callback.get",
                new CallbackIdRequest(RequiredString(arguments, "callback_id")),
                cancellationToken),
            "callback_list" => await _client.CallAsync<CallbackListRequest, IReadOnlyList<CallbackRecord>>(
                "callback.list",
                new CallbackListRequest(
                    OptionalString(arguments, "state"),
                    OptionalInt32(arguments, "limit") ?? 100),
                cancellationToken),
            "callback_cancel" => await _client.CallAsync<CallbackIdRequest, CallbackRecord>(
                "callback.cancel",
                new CallbackIdRequest(RequiredString(arguments, "callback_id")),
                cancellationToken),
            "callback_acknowledge" => await _client.CallAsync<CallbackIdRequest, CallbackRecord>(
                "callback.acknowledge",
                new CallbackIdRequest(RequiredString(arguments, "callback_id")),
                cancellationToken),
            _ => throw new McpMethodNotFoundException($"Unknown tool: {name}")
        };

        var text = JsonSerializer.Serialize(result, HostJson.Options);
        return new
        {
            content = new[] { new { type = "text", text } },
            structuredContent = result,
            isError = false
        };
    }

    private async Task<object> RegisterAsync(
        JsonElement arguments,
        CallbackSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        var provider = OptionalString(arguments, "provider") ??
            Environment.GetEnvironmentVariable("AGENT_CALLBACK_PROVIDER") ??
            "codex";
        var threadId = AgentTargetResolver.Resolve(
            provider,
            OptionalString(arguments, "thread_id"));
        var request = new RegisterCallbackRequest
        {
            Label = OptionalString(arguments, "label"),
            Provider = provider,
            TargetThreadId = threadId,
            SourceKind = sourceKind,
            ProcessId = sourceKind == CallbackSourceKind.Process
                ? RequiredInt32(arguments, "process_id")
                : null,
            ExpectedCommandLineContains = OptionalString(
                arguments,
                "expected_command_line_contains"),
            WorkingDirectory = OptionalString(arguments, "working_directory") ??
                Environment.CurrentDirectory,
            Instruction = RequiredString(arguments, "instruction"),
            EvidencePaths = StringArray(arguments, "evidence_paths"),
            ExpiresUtc = OptionalDateTimeOffset(arguments, "expires_utc")
        };
        return await _client.CallAsync<RegisterCallbackRequest, RegisterCallbackResult>(
            "callback.register",
            request,
            cancellationToken);
    }

    private static object InitializeResult()
    {
        var version = typeof(McpServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        return new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = "agent-callback", version }
        };
    }

    private static object[] ToolDefinitions() =>
    [
        Tool(
            "callback_provider_status",
            "Check whether one installed agent provider is available.",
            new Dictionary<string, object>
            {
                ["provider"] = StringProperty("Provider name; defaults to codex.")
            },
            []),
        Tool(
            "callback_register_process",
            "Register one stored continuation that becomes ready when an already-running local process exits.",
            new Dictionary<string, object>
            {
                ["process_id"] = IntegerProperty("Process id to observe."),
                ["instruction"] = StringProperty("Stored continuation instruction."),
                ["provider"] = StringProperty("Agent provider name; defaults to codex."),
                ["thread_id"] = StringProperty("Provider conversation target; normally discovered from the agent environment."),
                ["working_directory"] = StringProperty("Existing working directory."),
                ["label"] = StringProperty("Optional short label."),
                ["expected_command_line_contains"] = StringProperty("Optional PID identity marker."),
                ["evidence_paths"] = StringArrayProperty("Paths Codex may inspect after callback."),
                ["expires_utc"] = StringProperty("Optional ISO 8601 expiration timestamp.")
            },
            ["process_id", "instruction"]),
        Tool(
            "callback_register_event",
            "Register one stored continuation and return a secret for a local program to trigger later.",
            new Dictionary<string, object>
            {
                ["instruction"] = StringProperty("Stored continuation instruction."),
                ["provider"] = StringProperty("Agent provider name; defaults to codex."),
                ["thread_id"] = StringProperty("Provider conversation target; normally discovered from the agent environment."),
                ["working_directory"] = StringProperty("Existing working directory."),
                ["label"] = StringProperty("Optional short label."),
                ["evidence_paths"] = StringArrayProperty("Paths Codex may inspect after callback."),
                ["expires_utc"] = StringProperty("Optional ISO 8601 expiration timestamp.")
            },
            ["instruction"]),
        Tool(
            "callback_get",
            "Read one callback including its stored instruction, outcome, and delivery state.",
            new Dictionary<string, object> { ["callback_id"] = StringProperty("Callback id.") },
            ["callback_id"]),
        Tool(
            "callback_list",
            "List local callbacks, optionally filtered by state.",
            new Dictionary<string, object>
            {
                ["state"] = StringProperty("Optional callback state."),
                ["limit"] = IntegerProperty("Maximum records, from 1 to 500.")
            },
            []),
        Tool(
            "callback_cancel",
            "Cancel a callback that has not started delivery.",
            new Dictionary<string, object> { ["callback_id"] = StringProperty("Callback id.") },
            ["callback_id"]),
        Tool(
            "callback_acknowledge",
            "Acknowledge a delivered callback after its evidence has been handled.",
            new Dictionary<string, object> { ["callback_id"] = StringProperty("Callback id.") },
            ["callback_id"])
    ];

    private static object Tool(
        string name,
        string description,
        Dictionary<string, object> properties,
        string[] required) => new
        {
            name,
            description,
            inputSchema = new
            {
                type = "object",
                properties,
                required,
                additionalProperties = false
            },
            annotations = new
            {
                readOnlyHint = name is "callback_provider_status" or "callback_get" or "callback_list",
                destructiveHint = name == "callback_cancel",
                idempotentHint = name is "callback_get" or "callback_list" or
                "callback_cancel" or "callback_acknowledge",
                openWorldHint = false
            }
        };

    private static object StringProperty(string description) =>
        new { type = "string", description };

    private static object IntegerProperty(string description) =>
        new { type = "integer", description };

    private static object StringArrayProperty(string description) =>
        new { type = "array", items = new { type = "string" }, description };

    private static object ToolError(string message) => new
    {
        content = new[] { new { type = "text", text = message } },
        isError = true
    };

    private static object JsonRpcResult(JsonElement? id, object result) => new
    {
        jsonrpc = "2.0",
        id,
        result
    };

    private static object JsonRpcError(JsonElement? id, int code, string message) => new
    {
        jsonrpc = "2.0",
        id,
        error = new { code, message }
    };

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ??
        throw new InvalidOperationException($"{name} is required.");

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw new InvalidOperationException($"{name} must be a string.");
    }

    private static int RequiredInt32(JsonElement element, string name) =>
        OptionalInt32(element, name) ??
        throw new InvalidOperationException($"{name} is required.");

    private static int? OptionalInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.TryGetInt32(out var result)
            ? result
            : throw new InvalidOperationException($"{name} must be an integer.");
    }

    private static DateTimeOffset? OptionalDateTimeOffset(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        if (value is null)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var result)
            ? result
            : throw new InvalidOperationException($"{name} must be an ISO 8601 timestamp.");
    }

    private static IReadOnlyList<string> StringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{name} must be an array of strings.");
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            result.Add(item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? ""
                : throw new InvalidOperationException($"{name} must contain only strings."));
        }

        return result;
    }

    private sealed class McpMethodNotFoundException : Exception
    {
        public McpMethodNotFoundException(string method)
            : base($"Method not found: {method}")
        {
        }
    }
}
