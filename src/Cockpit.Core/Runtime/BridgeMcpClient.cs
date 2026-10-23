using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Cockpit.Core.Platform;
using Cockpit.Protocol;

namespace Cockpit.Core.Runtime;

public sealed record McpToolCallResult(bool IsError, string Text, JsonNode? Structured);

public sealed class McpClientException(string message) : Exception(message);

/// <summary>
/// A minimal MCP client that drives cockpit-mcp-bridge over stdio, exactly as Claude Code or Gemini CLI would.
/// Used by deterministic CLI commands (`cockpit organize --rules`), by `cockpit doctor`'s self-test and by the
/// integration tests, so those paths go through the same bus, gate, leases and audit as a model would.
/// </summary>
public sealed class BridgeMcpClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly StringBuilder _stderr = new();
    private readonly Task _reader;
    private long _nextId;

    public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }
    public int ProcessId => _process.Id;

    private BridgeMcpClient(Process process)
    {
        _process = process;
        _stdin = process.StandardInput.BaseStream;
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        _reader = Task.Run(ReadLoopAsync);
    }

    public static async Task<BridgeMcpClient> StartAsync(ComponentCommand bridge, IEnumerable<string> bridgeArguments, ChildProcessJob? job,
        string clientName = "cockpit-cli", CancellationToken ct = default)
    {
        var psi = bridge.ToStartInfo(bridgeArguments);
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Utf8.NoBom;
        psi.StandardErrorEncoding = Utf8.NoBom;
        psi.CreateNoWindow = true;
        var process = Process.Start(psi) ?? throw new McpClientException($"Could not start {bridge.Display}.");
        job?.Assign(process);
        var client = new BridgeMcpClient(process);
        try
        {
            var init = await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = "4.0.0" },
            }, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (init["error"] is JsonObject err) throw new McpClientException($"initialize failed: {err["message"]}");
            await client.NotifyAsync("notifications/initialized").ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken ct = default)
    {
        var reply = await RequestAsync("tools/list", new JsonObject(), TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (reply["error"] is JsonObject err) throw new McpClientException($"tools/list failed: {err["message"]}");
        return reply["result"]?["tools"]?.AsArray().Select(t => (string)t!["name"]!).ToList() ?? [];
    }

    public async Task<McpToolCallResult> CallToolAsync(string name, JsonObject arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var reply = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, timeout, ct).ConfigureAwait(false);
        if (reply["error"] is JsonObject err) throw new McpClientException($"{name}: {err["message"]} (code {err["code"]})");
        var result = reply["result"]!;
        string text = string.Join("\n", result["content"]?.AsArray().Select(c => (string?)c?["text"]).Where(t => t is not null) ?? []);
        return new McpToolCallResult((bool?)result["isError"] ?? false, text, result["structuredContent"]?.DeepClone());
    }

    private async Task<JsonNode> RequestAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new McpClientException($"{method} timed out after {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s. Bridge stderr: {Stderr}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method) => SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

    private async Task SendAsync(JsonObject message)
    {
        byte[] bytes = Utf8.NoBom.GetBytes(message.ToJsonString() + "\n");
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await _stdin.WriteAsync(bytes).ConfigureAwait(false);
            await _stdin.FlushAsync().ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new McpClientException($"The bridge closed its input ({ex.Message}). Bridge stderr: {Stderr}");
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = _process.StandardOutput;
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { continue; }
                if (msg?["id"] is JsonValue idValue && idValue.TryGetValue(out long id) && _pending.TryGetValue(id, out var tcs))
                    tcs.TrySetResult(msg);
            }
        }
        catch (IOException) { }
        foreach (var kv in _pending)
            kv.Value.TrySetException(new McpClientException($"The bridge exited. Stderr: {Stderr}"));
    }

    /// <summary>Closes stdin (a clean MCP shutdown) and returns the bridge's exit code.</summary>
    public async Task<int> CloseAsync(TimeSpan wait)
    {
        try { _stdin.Close(); } catch (IOException) { }
        try { await _process.WaitForExitAsync().WaitAsync(wait).ConfigureAwait(false); }
        catch (TimeoutException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync().ConfigureAwait(false); }
        await _reader.ConfigureAwait(false);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited) await CloseAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _process.Dispose();
        _write.Dispose();
    }
}
