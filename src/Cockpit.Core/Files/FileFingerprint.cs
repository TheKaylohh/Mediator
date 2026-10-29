using System.Globalization;
using System.IO.Hashing;

namespace Cockpit.Core.Files;

/// <summary>
/// Identity of a file's content at a moment in time, used to prove a file is unchanged before it is moved
/// back during rollback. XxHash64 is fast and non-cryptographic: it detects accidental change, not tampering.
/// Files up to <see cref="FullHashLimit"/> are hashed fully; larger ones hash size + first and last 4 MiB
/// ("xxh64p"), because reading hundreds of GB to organize a drive is not acceptable and a same-volume move
/// never touches content anyway.
/// </summary>
public sealed record FileFingerprint(long Size, long LastWriteUtcTicks, string Hash)
{
    public const long FullHashLimit = 64L * 1024 * 1024;
    private const int EdgeBytes = 4 * 1024 * 1024;

    public static async Task<FileFingerprint> ComputeAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        long size = info.Length;
        long ticks = info.LastWriteTimeUtc.Ticks;

        // FileShare.Read (no Write): a file another program is writing fails here instead of moving mid-write.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            bufferSize: 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hasher = new XxHash64();
        var buffer = new byte[1 << 20];
        string prefix;

        if (size <= FullHashLimit)
        {
            prefix = "xxh64";
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                hasher.Append(buffer.AsSpan(0, n));
        }
        else
        {
            prefix = "xxh64p";
            hasher.Append(BitConverter.GetBytes(size));
            await AppendRangeAsync(stream, hasher, buffer, 0, EdgeBytes, ct).ConfigureAwait(false);
            await AppendRangeAsync(stream, hasher, buffer, size - EdgeBytes, EdgeBytes, ct).ConfigureAwait(false);
        }

        string hex = hasher.GetCurrentHashAsUInt64().ToString("x16", CultureInfo.InvariantCulture);
        return new FileFingerprint(size, ticks, $"{prefix}:{hex}");
    }

    private static async Task AppendRangeAsync(FileStream stream, XxHash64 hasher, byte[] buffer, long offset, int count, CancellationToken ct)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        int remaining = count;
        while (remaining > 0)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("File shrank while being fingerprinted.");
            hasher.Append(buffer.AsSpan(0, n));
            remaining -= n;
        }
    }

    /// <summary>Cheap pre-check before hashing: size and timestamp.</summary>
    public bool QuickMatches(FileInfo current) =>
        current.Exists && current.Length == Size && current.LastWriteTimeUtc.Ticks == LastWriteUtcTicks;
}
