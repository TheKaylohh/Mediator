using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mediator.Core.Tools;

namespace Mediator.Connectors.Resolve;

/// <summary>An exact frame rate, e.g. 30000/1001 for 29.97 fps. Timeline times are whole frames of this rate.</summary>
public readonly record struct FrameRate(long Num, long Den)
{
    public double Fps => (double)Num / Den;
    public override string ToString() => Den == 1 ? Num.ToString(CultureInfo.InvariantCulture) : string.Create(CultureInfo.InvariantCulture, $"{Num}/{Den}");
    /// <summary>The nominal integer rate used for non-drop timecode (29.97 → 30).</summary>
    public int Nominal => (int)Math.Round(Fps);

    public static FrameRate Parse(string text)
    {
        var parts = text.Trim().Split('/');
        if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long n)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long d) && n > 0 && d > 0)
            return Reduce(n, d);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && f > 0) return FromDouble(f);
        throw new FormatException($"Not a frame rate: '{text}'. Use e.g. 24, 25, 30, 29.97 or 30000/1001.");
    }

    /// <summary>Snaps NTSC rates (23.976, 29.97, 59.94) to their exact x000/1001 form; other rates to the nearest 1/1000.</summary>
    public static FrameRate FromDouble(double fps)
    {
        foreach (int baseRate in new[] { 24, 30, 48, 60, 120 })
            if (Math.Abs(fps - baseRate * 1000.0 / 1001) < 0.01) return new(baseRate * 1000L, 1001);
        if (Math.Abs(fps - Math.Round(fps)) < 0.001) return new((long)Math.Round(fps), 1);
        return Reduce((long)Math.Round(fps * 1000), 1000);
    }

    public static FrameRate Reduce(long n, long d)
    {
        long g = Gcd(n, d);
        return new(n / g, d / g);
    }

    private static long Gcd(long a, long b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }

    /// <summary>Seconds → nearest whole frame.</summary>
    public long Frames(double seconds) => (long)Math.Round(seconds * Num / Den, MidpointRounding.AwayFromZero);
}

/// <summary>What the edit tools know about one media file. Ids ("c1", "c2") are what the model refers to.</summary>
public sealed record ClipInfo(string Id, string Path, double DurationSec, FrameRate? Fps, int? Width, int? Height,
    bool HasVideo, int AudioStreams, int? AudioChannels, int? AudioRate, string? Codec, string ProbedWith)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public interface IMediaProbe
{
    string Describe { get; }
    Task<ClipInfo?> ProbeAsync(string path, string id, CancellationToken ct);
}

/// <summary>ffprobe if installed, otherwise the built-in header reader (MP4/MOV/M4V/WAV only).</summary>
public sealed class CompositeMediaProbe(IMediaProbe? ffprobe, IMediaProbe native) : IMediaProbe
{
    public static CompositeMediaProbe Detect(IProcessRunner? runner = null) =>
        new(ToolLocator.Ffprobe() is string exe ? new FfprobeMediaProbe(exe, runner ?? DefaultProcessRunner.Instance) : null, new NativeMediaProbe());

    public string Describe => ffprobe is null ? "built-in reader (MP4/MOV/WAV; install ffmpeg for MKV/MP3 and codec details)" : ffprobe.Describe;

    public async Task<ClipInfo?> ProbeAsync(string path, string id, CancellationToken ct) =>
        (ffprobe is null ? null : await ffprobe.ProbeAsync(path, id, ct).ConfigureAwait(false)) ?? await native.ProbeAsync(path, id, ct).ConfigureAwait(false);
}

/// <summary>`ffprobe -v error -print_format json -show_format -show_streams &lt;file&gt;`.</summary>
public sealed class FfprobeMediaProbe(string executable, IProcessRunner runner) : IMediaProbe
{
    public string Describe => "ffprobe (" + executable + ")";

    public async Task<ClipInfo?> ProbeAsync(string path, string id, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable);
        foreach (var a in new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path }) psi.ArgumentList.Add(a);
        var r = await runner.RunAsync(psi, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return r.Ok ? Parse(r.Stdout, path, id) : null;
    }

    public static ClipInfo? Parse(string json, string path, string id)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double duration = root.TryGetProperty("format", out var fmt) && Str(fmt, "duration") is string d
            && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) ? dv : 0;
        FrameRate? fps = null;
        int? w = null, h = null, channels = null, rate = null;
        string? codec = null;
        bool video = false;
        int audio = 0;
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                switch (Str(s, "codec_type"))
                {
                    case "video" when !video:
                        if (s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1) break; // cover art
                        video = true;
                        codec = Str(s, "codec_name");
                        w = s.TryGetProperty("width", out var wv) ? wv.GetInt32() : null;
                        h = s.TryGetProperty("height", out var hv) ? hv.GetInt32() : null;
                        string? r = Str(s, "avg_frame_rate") is { } avg && avg != "0/0" ? avg : Str(s, "r_frame_rate");
                        if (r is not null && r != "0/0") { var raw = FrameRate.Parse(r); fps = FrameRate.FromDouble(raw.Fps); }
                        break;
                    case "audio":
                        audio++;
                        channels ??= s.TryGetProperty("channels", out var cv) ? cv.GetInt32() : null;
                        rate ??= Str(s, "sample_rate") is { } sr && int.TryParse(sr, NumberStyles.None, CultureInfo.InvariantCulture, out int srv) ? srv : null;
                        codec ??= Str(s, "codec_name");
                        break;
                }
            }
        return duration <= 0 ? null : new ClipInfo(id, path, duration, fps, w, h, video, audio, channels, rate, codec, "ffprobe");
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Header-only reader for the formats a phone or camera usually records: ISO-BMFF (MP4/MOV/M4V) and WAV.
/// MP4: duration from mvhd; per track, handler (vide/soun), tkhd size, mdhd timescale and stts sample timing (fps).
/// No decoding and no codec details; ffprobe is used when installed.
/// </summary>
public sealed class NativeMediaProbe : IMediaProbe
{
    public string Describe => "built-in header reader";

    public Task<ClipInfo?> ProbeAsync(string path, string id, CancellationToken ct)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ClipInfo? info = ext switch
            {
                ".mp4" or ".mov" or ".m4v" or ".m4a" => ReadMp4(fs, path, id),
                ".wav" => ReadWav(fs, path, id),
                _ => null,
            };
            return Task.FromResult(info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return Task.FromResult<ClipInfo?>(null);
        }
    }

    private static ClipInfo? ReadWav(Stream s, string path, string id)
    {
        var br = new BinaryReader(s);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") return null;
        br.ReadUInt32();
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") return null;
        int channels = 0, rate = 0;
        long byteRate = 0, dataBytes = 0;
        while (s.Position + 8 <= s.Length)
        {
            string chunk = Encoding.ASCII.GetString(br.ReadBytes(4));
            uint size = br.ReadUInt32();
            long next = s.Position + size + (size & 1);
            if (chunk == "fmt ")
            {
                br.ReadUInt16();
                channels = br.ReadUInt16();
                rate = br.ReadInt32();
                byteRate = br.ReadUInt32();
            }
            else if (chunk == "data") dataBytes = size;
            s.Position = Math.Min(next, s.Length);
        }
        return byteRate <= 0 ? null : new ClipInfo(id, path, (double)dataBytes / byteRate, null, null, null, false, 1, channels, rate, "pcm", "header");
    }

    private sealed class Track
    {
        public string? Handler;
        public int? Width, Height;
        public long Timescale, MediaDuration, Samples, SampleDeltaTotal;
        public int? Channels;
    }

    private static ClipInfo? ReadMp4(Stream s, string path, string id)
    {
        long movieTimescale = 0, movieDuration = 0;
        var tracks = new List<Track>();
        Track? current = null;

        void Walk(long start, long end, int depth)
        {
            long pos = start;
            var head = new byte[8];
            var big = new byte[8];
            while (pos + 8 <= end && depth < 8)
            {
                s.Position = pos;
                s.ReadExactly(head);
                long size = BinaryPrimitives.ReadUInt32BigEndian(head);
                string type = Encoding.ASCII.GetString(head, 4, 4);
                long headerLen = 8;
                if (size == 1)
                {
                    s.ReadExactly(big);
                    size = (long)BinaryPrimitives.ReadUInt64BigEndian(big);
                    headerLen = 16;
                }
                else if (size == 0) size = end - pos;
                if (size < headerLen || pos + size > end) return;
                long body = pos + headerLen, bodyEnd = pos + size;
                switch (type)
                {
                    case "moov" or "mdia" or "minf" or "stbl":
                        Walk(body, bodyEnd, depth + 1);
                        break;
                    case "trak":
                        current = new Track();
                        tracks.Add(current);
                        Walk(body, bodyEnd, depth + 1);
                        current = null;
                        break;
                    case "mvhd":
                    {
                        var b = Read(body, (int)Math.Min(32, bodyEnd - body));
                        if (b[0] == 1) { movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20)); movieDuration = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(24)); }
                        else { movieTimescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12)); movieDuration = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16)); }
                        break;
                    }
                    case "tkhd" when current is not null:
                    {
                        var b = Read(body, (int)Math.Min(96, bodyEnd - body));
                        int off = b[0] == 1 ? 88 : 76;
                        if (b.Length >= off + 8)
                        {
                            current.Width = (int)(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off)) >> 16);
                            current.Height = (int)(BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off + 4)) >> 16);
                        }
                        break;
                    }
                    case "mdhd" when current is not null:
                    {
                        var b = Read(body, (int)Math.Min(32, bodyEnd - body));
                        if (b[0] == 1) { current.Timescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20)); current.MediaDuration = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(24)); }
                        else { current.Timescale = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(12)); current.MediaDuration = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16)); }
                        break;
                    }
                    // The media handler (mdia/hdlr) comes first; QuickTime also has a data handler inside minf ('alis', 'url ')
                    // that must not overwrite it.
                    case "hdlr" when current is not null && current.Handler is null:
                    {
                        var b = Read(body, 12);
                        current.Handler = Encoding.ASCII.GetString(b, 8, 4);
                        break;
                    }
                    case "stsd" when current is not null && current.Handler == "soun":
                    {
                        // full box (4) + entry count (4) + entry: size(4) format(4) reserved(6) dref(2) reserved(8) channelcount(2)
                        var b = Read(body, (int)Math.Min(36, bodyEnd - body));
                        if (b.Length >= 34) current.Channels = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(32));
                        break;
                    }
                    case "stts" when current is not null:
                    {
                        var countBytes = Read(body + 4, 4);
                        uint entries = BinaryPrimitives.ReadUInt32BigEndian(countBytes);
                        if (entries > 1_000_000) break;
                        var table = Read(body + 8, (int)Math.Min(entries * 8, bodyEnd - body - 8));
                        for (int i = 0; i + 8 <= table.Length; i += 8)
                        {
                            long count = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i));
                            long delta = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i + 4));
                            current.Samples += count;
                            current.SampleDeltaTotal += count * delta;
                        }
                        break;
                    }
                }
                pos += size;
            }
        }

        byte[] Read(long at, int count)
        {
            s.Position = at;
            var buffer = new byte[Math.Max(0, count)];
            s.ReadExactly(buffer);
            return buffer;
        }

        Walk(0, s.Length, 0);
        if (movieTimescale <= 0) return null;
        var videoTrack = tracks.FirstOrDefault(t => t.Handler == "vide");
        var audioTracks = tracks.Where(t => t.Handler == "soun").ToList();
        FrameRate? fps = null;
        if (videoTrack is { Samples: > 0, SampleDeltaTotal: > 0, Timescale: > 0 })
            fps = FrameRate.FromDouble((double)videoTrack.Samples * videoTrack.Timescale / videoTrack.SampleDeltaTotal);
        var audio = audioTracks.FirstOrDefault();
        return new ClipInfo(id, path, (double)movieDuration / movieTimescale, fps, videoTrack?.Width, videoTrack?.Height,
            videoTrack is not null, audioTracks.Count, audio?.Channels, audio is { Timescale: > 0 } ? (int)audio.Timescale : null, null, "header");
    }
}
