using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AgentCallback.Providers.OpenCode;

public sealed record OpenCodeProbe(bool Available, string? Error);

public sealed record OpenCodeSessionProbe(
    bool Exists,
    bool Busy,
    bool Retryable,
    string? Error);

public sealed record OpenCodePromptResult(
    bool Accepted,
    bool Retryable,
    bool Uncertain,
    string? Error);

public interface IOpenCodeConversationTransport
{
    string Name { get; }

    Task<OpenCodeProbe> ProbeAsync(
        OpenCodeConnection connection,
        CancellationToken cancellationToken);

    Task<OpenCodeSessionProbe> ProbeSessionAsync(
        OpenCodeConnection connection,
        string sessionId,
        CancellationToken cancellationToken);

    Task<OpenCodePromptResult> PromptAsync(
        OpenCodeConnection connection,
        string sessionId,
        string message,
        CancellationToken cancellationToken);
}

public sealed class OpenCodeHttpTransport : IOpenCodeConversationTransport, IDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public OpenCodeHttpTransport()
        : this(new HttpClient(), ownsClient: true)
    {
    }

    public OpenCodeHttpTransport(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _ownsClient = ownsClient;
    }

    public string Name => "opencode-http-plugin";

    public async Task<OpenCodeProbe> ProbeAsync(
        OpenCodeConnection connection,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(connection, HttpMethod.Get, "/global/health");
        try
        {
            using var response = await SendAsync(request, ProbeTimeout, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new OpenCodeProbe(false, await DescribeFailureAsync(response, cancellationToken));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var healthy = document.RootElement.TryGetProperty("healthy", out var value) &&
                value.ValueKind == JsonValueKind.True;
            return healthy
                ? new OpenCodeProbe(true, null)
                : new OpenCodeProbe(false, "OpenCode server health response was not healthy.");
        }
        catch (Exception exception) when (IsConnectivityException(exception, cancellationToken))
        {
            return new OpenCodeProbe(false, $"OpenCode server probe failed: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return new OpenCodeProbe(
                false,
                $"OpenCode server health response was invalid: {exception.Message}");
        }
    }

    public async Task<OpenCodeSessionProbe> ProbeSessionAsync(
        OpenCodeConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        using (var sessionRequest = CreateRequest(
            connection,
            HttpMethod.Get,
            $"/session/{Uri.EscapeDataString(sessionId)}"))
        {
            try
            {
                using var sessionResponse = await SendAsync(
                    sessionRequest,
                    ProbeTimeout,
                    cancellationToken);
                if (sessionResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    return new OpenCodeSessionProbe(false, false, false, "OpenCode session was not found.");
                }

                if (!sessionResponse.IsSuccessStatusCode)
                {
                    return new OpenCodeSessionProbe(
                        false,
                        false,
                        IsRetryableStatus(sessionResponse.StatusCode),
                        await DescribeFailureAsync(sessionResponse, cancellationToken));
                }
            }
            catch (Exception exception) when (IsConnectivityException(exception, cancellationToken))
            {
                return new OpenCodeSessionProbe(false, false, true, exception.Message);
            }
        }

        using var statusRequest = CreateRequest(connection, HttpMethod.Get, "/session/status");
        try
        {
            using var statusResponse = await SendAsync(statusRequest, ProbeTimeout, cancellationToken);
            if (!statusResponse.IsSuccessStatusCode)
            {
                return new OpenCodeSessionProbe(
                    true,
                    false,
                    IsRetryableStatus(statusResponse.StatusCode),
                    await DescribeFailureAsync(statusResponse, cancellationToken));
            }

            await using var stream = await statusResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var busy = document.RootElement.TryGetProperty(sessionId, out var state) &&
                state.TryGetProperty("type", out var type) &&
                !string.Equals(type.GetString(), "idle", StringComparison.OrdinalIgnoreCase);
            return new OpenCodeSessionProbe(true, busy, false, null);
        }
        catch (Exception exception) when (IsConnectivityException(exception, cancellationToken))
        {
            return new OpenCodeSessionProbe(true, false, true, exception.Message);
        }
        catch (JsonException exception)
        {
            return new OpenCodeSessionProbe(
                true,
                false,
                false,
                $"OpenCode session status response was invalid: {exception.Message}");
        }
    }

    public async Task<OpenCodePromptResult> PromptAsync(
        OpenCodeConnection connection,
        string sessionId,
        string message,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            connection,
            HttpMethod.Post,
            $"/session/{Uri.EscapeDataString(sessionId)}/prompt_async");
        request.Content = JsonContent.Create(new
        {
            parts = new[] { new { type = "text", text = message } }
        });

        try
        {
            using var response = await SendAsync(request, PromptTimeout, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new OpenCodePromptResult(true, false, false, null);
            }

            var error = await DescribeFailureAsync(response, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Locked)
            {
                return new OpenCodePromptResult(false, true, false, error);
            }

            return (int)response.StatusCode >= 500
                ? new OpenCodePromptResult(false, false, true, error)
                : new OpenCodePromptResult(false, false, false, error);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return new OpenCodePromptResult(
                false,
                false,
                true,
                $"OpenCode prompt timed out after a possible write: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            return new OpenCodePromptResult(
                false,
                false,
                true,
                $"OpenCode prompt connection failed after a possible write: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutSource.Token);
    }

    private static HttpRequestMessage CreateRequest(
        OpenCodeConnection connection,
        HttpMethod method,
        string path)
    {
        var request = new HttpRequestMessage(method, connection.ServerUrl + path);
        var credential = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{connection.Username}:{connection.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credential);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static async Task<string> DescribeFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (content.Length > 512)
        {
            content = content[..512];
        }

        return string.IsNullOrWhiteSpace(content)
            ? $"OpenCode server returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : $"OpenCode server returned HTTP {(int)response.StatusCode}: {content}";
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool IsConnectivityException(
        Exception exception,
        CancellationToken cancellationToken) =>
        exception is HttpRequestException ||
        (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested);
}
