using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace AgentCallback.Infrastructure.Security;

[SupportedOSPlatform("linux")]
public sealed class FileKeySecretProtector : ISecretProtector, IDisposable
{
    private const byte FormatVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AdditionalData = "agent-callback:file-key:v1"u8.ToArray();
    private static readonly UnixFileMode RequiredKeyMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode UnsafeKeyMode =
        UnixFileMode.GroupRead |
        UnixFileMode.GroupWrite |
        UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead |
        UnixFileMode.OtherWrite |
        UnixFileMode.OtherExecute;

    private readonly byte[] _key;
    private bool _disposed;

    public FileKeySecretProtector(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "The file-key protector is intended for Unix platforms.");
        }

        paths.EnsureDataDirectory();
        _key = LoadOrCreateKey(paths.MasterKeyPath);
    }

    public byte[] Protect(string value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var result = new byte[1 + NonceSize + TagSize + plaintext.Length];
        result[0] = FormatVersion;
        var nonce = result.AsSpan(1, NonceSize);
        var tag = result.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = result.AsSpan(1 + NonceSize + TagSize);
        RandomNumberGenerator.Fill(nonce);
        try
        {
            using var algorithm = new AesGcm(_key, TagSize);
            algorithm.Encrypt(nonce, plaintext, ciphertext, tag, AdditionalData);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public string Unprotect(byte[] value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length < 1 + NonceSize + TagSize || value[0] != FormatVersion)
        {
            throw new CryptographicException("The protected secret envelope is invalid.");
        }

        var nonce = value.AsSpan(1, NonceSize);
        var tag = value.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = value.AsSpan(1 + NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var algorithm = new AesGcm(_key, TagSize);
            algorithm.Decrypt(nonce, ciphertext, tag, plaintext, AdditionalData);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }

    private static byte[] LoadOrCreateKey(string keyPath)
    {
        var attempts = 0;
        while (attempts < 3)
        {
            attempts++;
            try
            {
                var key = RandomNumberGenerator.GetBytes(KeySize);
                try
                {
                    using var stream = new FileStream(keyPath, new FileStreamOptions
                    {
                        Mode = FileMode.CreateNew,
                        Access = FileAccess.Write,
                        Share = FileShare.None,
                        BufferSize = 4096,
                        Options = FileOptions.WriteThrough,
                        UnixCreateMode = RequiredKeyMode
                    });
                    stream.Write(key);
                    stream.Flush(flushToDisk: true);
                    return key;
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(key);
                    throw;
                }
            }
            catch (IOException) when (File.Exists(keyPath))
            {
                var mode = File.GetUnixFileMode(keyPath);
                if ((mode & UnsafeKeyMode) != 0)
                {
                    throw new UnauthorizedAccessException(
                        $"Agent Callback key permissions are unsafe: {keyPath}. Expected mode 0600.");
                }

                var existing = File.ReadAllBytes(keyPath);
                if (existing.Length != KeySize)
                {
                    CryptographicOperations.ZeroMemory(existing);
                    throw new CryptographicException(
                        "Agent Callback master key has an invalid length.");
                }

                return existing;
            }
        }

        throw new IOException("Agent Callback could not create or open its master key.");
    }
}
