using System.Text;

namespace Mediator.Protocol;

public static class Utf8
{
    /// <summary>UTF-8 that never writes a byte order mark. JSON-RPC peers reject a leading BOM.</summary>
    public static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>UTF-8 with a BOM. Only for .ps1 files read by Windows PowerShell 5.1, which assumes ANSI otherwise.</summary>
    public static readonly UTF8Encoding WithBom = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false);
}

/// <summary>
/// Newline-delimited JSON over the process's raw stdin/stdout (the MCP stdio transport and the
/// Core-to-worker channel). Uses the raw handles, never Console.Out, because the console encoding on
/// Windows defaults to the OEM code page and some encodings emit a BOM on first write.
/// </summary>
public sealed class StdioJsonLines : IAsyncDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _firstLine = true;

    private StdioJsonLines(Stream input, Stream output)
    {
        _reader = new StreamReader(input, Utf8.NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
        _writer = new StreamWriter(output, Utf8.NoBom, bufferSize: 64 * 1024) { NewLine = "\n", AutoFlush = false };
    }

    /// <summary>
    /// Takes over stdin/stdout and redirects Console.Out to stderr, so a stray Console.WriteLine
    /// anywhere in the process (including libraries) cannot corrupt the protocol stream.
    /// </summary>
    public static StdioJsonLines OpenProcessStdio()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        Console.SetOut(Console.Error);
        return new StdioJsonLines(input, output);
    }

    public static StdioJsonLines Over(Stream input, Stream output) => new(input, output);

    /// <summary>Next non-empty line with any BOM and trailing CR removed; null at end of input.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            string? line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
            if (_firstLine)
            {
                _firstLine = false;
                if (line.Length > 0 && line[0] == '﻿') line = line[1..];
            }
            if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
            if (line.Length == 0) continue;
            return line;
        }
    }

    /// <summary>Writes one message. The text must be compact JSON with no raw newlines.</summary>
    public async ValueTask WriteLineAsync(string json, CancellationToken ct = default)
    {
        if (json.Contains('\n') || json.Contains('\r'))
            throw new ArgumentException("A stdio JSON message must be a single line.", nameof(json));
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(json.AsMemory(), CancellationToken.None).ConfigureAwait(false);
            await _writer.WriteAsync('\n').ConfigureAwait(false);
            await _writer.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            _reader.Dispose();
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
