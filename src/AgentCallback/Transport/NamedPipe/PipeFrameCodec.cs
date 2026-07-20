using System.Buffers.Binary;
using System.Text.Json;

namespace AgentCallback.Transport.NamedPipe;

internal static class PipeFrameCodec
{
    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, HostJson.Options);
        if (payload.Length > HostProtocol.MaxFrameBytes)
        {
            throw new InvalidOperationException("Host protocol frame exceeds the 1 MiB limit.");
        }

        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, sizeof(int)), payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > HostProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException($"Invalid host protocol frame length: {length}.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return JsonSerializer.Deserialize<T>(payload, HostJson.Options) ??
            throw new InvalidDataException("Host protocol frame contains no JSON value.");
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("Host protocol stream ended mid-frame.");
            }

            read += count;
        }
    }
}
