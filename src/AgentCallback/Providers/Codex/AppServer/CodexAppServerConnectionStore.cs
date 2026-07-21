using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using AgentCallback.Infrastructure;

namespace AgentCallback.Providers.Codex.AppServer;

public sealed record CodexAppServerConnectionRegistration(
    string SocketPath,
    string? Source,
    int? ProcessId);

public sealed record CodexAppServerConnection(
    string ConnectionId,
    string SocketPath,
    string Source,
    int? ProcessId,
    DateTimeOffset UpdatedUtc);

public sealed record CodexAppServerConnectionResult(
    string Provider,
    string ConnectionId,
    string SocketPath,
    DateTimeOffset UpdatedUtc);

public interface ICodexAppServerConnectionStore
{
    Task<CodexAppServerConnectionResult> UpsertAsync(
        CodexAppServerConnectionRegistration registration,
        CancellationToken cancellationToken);

    Task<CodexAppServerConnection?> GetAsync(
        string connectionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CodexAppServerConnection>> ListAsync(
        CancellationToken cancellationToken);
}

[SupportedOSPlatform("linux")]
public sealed class CodexAppServerConnectionStore : ICodexAppServerConnectionStore, IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumConnections = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CodexAppServerConnectionStore(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<CodexAppServerConnectionResult> UpsertAsync(
        CodexAppServerConnectionRegistration registration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var socketPath = NormalizeSocketPath(registration.SocketPath);
        var source = string.IsNullOrWhiteSpace(registration.Source)
            ? "agent-callback-launcher"
            : registration.Source.Trim();
        if (source.Length > 64)
        {
            throw new InvalidOperationException("Codex app-server connection source is too long.");
        }

        var connectionId = CreateConnectionId(socketPath);
        var updatedUtc = DateTimeOffset.UtcNow;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            document.Connections.RemoveAll(item =>
                string.Equals(item.ConnectionId, connectionId, StringComparison.Ordinal));
            document.Connections.Add(new StoredConnection
            {
                ConnectionId = connectionId,
                SocketPath = socketPath,
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

        return new CodexAppServerConnectionResult(
            "codex",
            connectionId,
            socketPath,
            updatedUtc);
    }

    public async Task<CodexAppServerConnection?> GetAsync(
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

    public async Task<IReadOnlyList<CodexAppServerConnection>> ListAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await ReadDocumentAsync(cancellationToken);
            return document.Connections
                .OrderByDescending(item => item.UpdatedUtc)
                .Select(item => new CodexAppServerConnection(
                    item.ConnectionId,
                    item.SocketPath,
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

    public static string NormalizeSocketPath(string value)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException(
                "Codex app-server Unix socket connections are available only on Linux.");
        }

        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("Codex app-server socket path is required.");
        }

        var fullPath = Path.GetFullPath(value.Trim());
        if (!Path.IsPathFullyQualified(fullPath) || fullPath.Length > 4096)
        {
            throw new InvalidOperationException(
                "Codex app-server socket path must be a bounded absolute path.");
        }

        return fullPath;
    }

    private static string CreateConnectionId(string socketPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(socketPath));
        return $"cx_{Convert.ToHexString(bytes.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private async Task<StoredDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.CodexConnectionsPath))
        {
            return new StoredDocument();
        }

        await using var stream = new FileStream(
            _paths.CodexConnectionsPath,
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
            throw new InvalidDataException("Codex app-server connection registry is invalid.");
        }

        return document;
    }

    private async Task WriteDocumentAsync(
        StoredDocument document,
        CancellationToken cancellationToken)
    {
        _paths.EnsureDataDirectory();
        var temporaryPath = _paths.CodexConnectionsPath + $".tmp-{Guid.NewGuid():N}";
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

            File.SetUnixFileMode(
                temporaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, _paths.CodexConnectionsPath, overwrite: true);
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
        public int SchemaVersion { get; set; } =
            CodexAppServerConnectionStore.SchemaVersion;
        public List<StoredConnection> Connections { get; set; } = [];
    }

    private sealed class StoredConnection
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string SocketPath { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public int? ProcessId { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
    }
}

public static class CodexAppServerTarget
{
    public static string Format(string connectionId, string threadId) =>
        $"{connectionId}:{threadId}";

    public static bool TryParse(
        string value,
        out string connectionId,
        out string threadId)
    {
        connectionId = string.Empty;
        threadId = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1 ||
            value.IndexOf(':', separator + 1) >= 0)
        {
            return false;
        }

        connectionId = value[..separator];
        threadId = value[(separator + 1)..];
        return connectionId.StartsWith("cx_", StringComparison.Ordinal) &&
            connectionId.Length == 35 &&
            Guid.TryParse(threadId, out _);
    }
}
