using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCallback.Infrastructure;
using AgentCallback.Infrastructure.Security;

namespace AgentCallback.Providers.OpenCode;

public sealed record OpenCodeConnectionRegistration(
    string ServerUrl,
    string? Username,
    string Password,
    string? Source,
    int? ProcessId);

public sealed record OpenCodeConnection(
    string ConnectionId,
    string ServerUrl,
    string Username,
    string Password,
    string Source,
    int? ProcessId,
    DateTimeOffset UpdatedUtc);

public sealed record OpenCodeConnectionResult(
    string Provider,
    string ConnectionId,
    string ServerUrl,
    DateTimeOffset UpdatedUtc);

public interface IOpenCodeConnectionStore
{
    Task<OpenCodeConnectionResult> UpsertAsync(
        OpenCodeConnectionRegistration registration,
        CancellationToken cancellationToken);

    Task<OpenCodeConnection?> GetAsync(
        string connectionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<OpenCodeConnection>> ListAsync(CancellationToken cancellationToken);
}

public sealed class OpenCodeConnectionStore : IOpenCodeConnectionStore, IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumConnections = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly ISecretProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OpenCodeConnectionStore(AppPaths paths, ISecretProtector protector)
    {
        _paths = paths;
        _protector = protector;
    }

    public async Task<OpenCodeConnectionResult> UpsertAsync(
        OpenCodeConnectionRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Password);
        var serverUrl = NormalizeLoopbackServerUrl(registration.ServerUrl);
        var username = string.IsNullOrWhiteSpace(registration.Username)
            ? "opencode"
            : registration.Username.Trim();
        if (username.Length > 128)
        {
            throw new InvalidOperationException("OpenCode server username is too long.");
        }

        if (registration.Password.Length > 4096)
        {
            throw new InvalidOperationException("OpenCode server password is too long.");
        }

        var source = string.IsNullOrWhiteSpace(registration.Source)
            ? "plugin"
            : registration.Source.Trim();
        if (source.Length > 64)
        {
            throw new InvalidOperationException("OpenCode connection source is too long.");
        }

        var connectionId = CreateConnectionId(serverUrl, username);
        var updatedUtc = DateTimeOffset.UtcNow;
        var protectedPassword = Convert.ToBase64String(_protector.Protect(registration.Password));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            document.Connections.RemoveAll(item =>
                string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal));
            document.Connections.Add(new StoredConnection
            {
                ConnectionId = connectionId,
                ServerUrl = serverUrl,
                Username = username,
                ProtectedPassword = protectedPassword,
                Source = source,
                ProcessId = registration.ProcessId,
                UpdatedUtc = updatedUtc
            });
            document.Connections = document.Connections
                .OrderByDescending(item => item.UpdatedUtc)
                .Take(MaximumConnections)
                .ToList();
            await WriteDocumentAsync(document, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }

        return new OpenCodeConnectionResult("opencode", connectionId, serverUrl, updatedUtc);
    }

    public async Task<OpenCodeConnection?> GetAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return null;
        }

        var connections = await ListAsync(cancellationToken);
        return connections.FirstOrDefault(item =>
            string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<OpenCodeConnection>> ListAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            return document.Connections
                .OrderByDescending(item => item.UpdatedUtc)
                .Select(item => new OpenCodeConnection(
                    item.ConnectionId,
                    item.ServerUrl,
                    item.Username,
                    _protector.Unprotect(Convert.FromBase64String(item.ProtectedPassword)),
                    item.Source,
                    item.ProcessId,
                    item.UpdatedUtc))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    public static string NormalizeLoopbackServerUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.IsLoopback ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath != "/" && !string.IsNullOrEmpty(uri.AbsolutePath)))
        {
            throw new InvalidOperationException(
                "OpenCode server URL must be an absolute loopback HTTP(S) origin.");
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }

    private static string CreateConnectionId(string serverUrl, string username)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{serverUrl}\n{username}"));
        return $"oc_{Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private async Task<StoredDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.ProviderConnectionsPath))
        {
            return new StoredDocument();
        }

        await using var stream = new FileStream(
            _paths.ProviderConnectionsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var document = await JsonSerializer.DeserializeAsync<StoredDocument>(
            stream,
            JsonOptions,
            cancellationToken);
        if (document is null || document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException("OpenCode connection registry is invalid.");
        }

        return document;
    }

    private async Task WriteDocumentAsync(
        StoredDocument document,
        CancellationToken cancellationToken)
    {
        _paths.EnsureDataDirectory();
        var temporaryPath = _paths.ProviderConnectionsPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _paths.ProviderConnectionsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed class StoredDocument
    {
        public int SchemaVersion { get; set; } = OpenCodeConnectionStore.SchemaVersion;
        public List<StoredConnection> Connections { get; set; } = [];
    }

    private sealed class StoredConnection
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string ServerUrl { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string ProtectedPassword { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public int? ProcessId { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}
