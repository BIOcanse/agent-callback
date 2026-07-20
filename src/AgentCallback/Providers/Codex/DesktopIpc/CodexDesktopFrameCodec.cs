using System.Buffers.Binary;
using System.Text.Json;

namespace AgentCallback.Providers.Codex.DesktopIpc;

internal static class CodexDesktopFrameCodec
{
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async Task<JsonElement> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
        if (length == 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid Codex Desktop IPC frame length: {length}.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    public static async Task WriteAsync(
        Stream stream,
        object message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions.Value);
        if (payload.Length > MaxFrameBytes)
        {
            throw new InvalidDataException($"Codex Desktop IPC request exceeds {MaxFrameBytes} bytes.");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(0, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("Codex Desktop IPC closed while reading a frame.");
            }

            offset += read;
        }
    }
}
