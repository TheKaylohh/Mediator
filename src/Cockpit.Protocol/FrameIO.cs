using System.Buffers.Binary;

namespace Cockpit.Protocol;

/// <summary>
/// Length-prefixed framing for the Cockpit bus: a 4-byte little-endian length, then that many bytes
/// of UTF-8 JSON (no BOM). One frame carries exactly one JSON-RPC message or handshake object.
/// Byte-mode pipes and TCP have no message boundaries, so framing is explicit.
/// </summary>
public static class FrameIO
{
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>Reads one frame. Returns null on a clean end of stream at a frame boundary.</summary>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxFrameBytes)
            throw new InvalidDataException($"Frame length {length} is outside 0..{MaxFrameBytes}.");

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Stream ended inside a frame payload.");
        return payload;
    }

    /// <summary>
    /// Fills the buffer completely. Returns false only if the stream ended before any byte was read;
    /// a partial read followed by end of stream is a protocol error.
    /// </summary>
    private static async ValueTask<bool> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0) return false;
                throw new EndOfStreamException($"Stream ended after {read} of {buffer.Length} bytes.");
            }
            read += n;
        }
        return true;
    }

    internal static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxFrameBytes)
            throw new InvalidDataException($"Frame of {payload.Length} bytes exceeds {MaxFrameBytes}.");
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }
}

/// <summary>
/// Serializes concurrent frame writes onto one stream. Header and payload go out in a single write so a
/// cancelled writer can never leave half a frame on the wire. Reads must happen on a separate loop:
/// never wait for a reply while holding this writer, or a full pipe buffer deadlocks both ends.
/// </summary>
public sealed class FrameWriter : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public FrameWriter(Stream stream) => _stream = stream;

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var frame = FrameIO.Encode(payload.Span);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Once started, finish the write even if the caller cancels; a torn frame poisons the stream.
            await _stream.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
            await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask WriteAsync(string json, CancellationToken ct) =>
        WriteAsync(Utf8.NoBom.GetBytes(json), ct);

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; }
        finally { _gate.Release(); }
    }
}
