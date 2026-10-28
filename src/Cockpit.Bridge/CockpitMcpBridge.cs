using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Protocol;

namespace Cockpit.Bridge;

public sealed record BridgeOptions(
    string JobId,
    string Token,
    BusEndpoint Endpoint,
    TimeSpan RequestTimeout,
    TimeSpan ConnectTimeout)
{
    /// <summary>The job token arrives in this environment variable, never on the command line (argv is visible to other processes).</summary>
    public const string TokenVariable = "COCKPIT_JOB_TOKEN";

    public const string Usage =
        "usage: COCKPIT_JOB_TOKEN=<token> cockpit-mcp-bridge --job-id <id> [--endpoint pipe:ai-cockpit-bus | tcp:127.0.0.1:<port>] " +
        "[--timeout-seconds 1200] [--connect-timeout-seconds 10]";

    public static BridgeOptions Parse(string[] args) => Parse(args, Environment.GetEnvironmentVariable(TokenVariable));

    public static BridgeOptions Parse(string[] args, string? tokenFromEnvironment)
    {
        string? jobId = null, token = null;
        var endpoint = BusEndpoint.Default;
        double timeout = 1200, connect = 10;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--job-id": jobId = Next(); break;
                case "--scope": throw new ArgumentException($"--scope is no longer accepted: tokens on a command line are visible to other processes. Pass it in {TokenVariable}.");
                case "--endpoint": endpoint = BusEndpoint.Parse(Next()); break;
                case "--timeout-seconds": timeout = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--connect-timeout-seconds": connect = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }
        token = tokenFromEnvironment;
        if (string.IsNullOrEmpty(jobId)) throw new ArgumentException("--job-id is required.");
        if (string.IsNullOrEmpty(token)) throw new ArgumentException($"{TokenVariable} is not set.");
        if (timeout is < 1 or > 86_400 || connect is < 0.1 or > 300)
            throw new ArgumentException("Timeouts are out of range.");
        return new BridgeOptions(jobId, token, endpoint, TimeSpan.FromSeconds(timeout), TimeSpan.FromSeconds(connect));
    }
}

/// <summary>
/// Stateless stdio-to-bus proxy that Claude Code, Antigravity CLI or any MCP client spawns as a stdio server.
///
/// It deliberately does NOT filter tools or decide approvals: the agent can read its own MCP config and
/// run this program itself, so any check here is cosmetic. tools/list filtering, the approval gate,
/// leases and audit all happen in the Core behind the bus. What the bridge does own:
///   - correct MCP stdio framing (one compact JSON message per line, UTF-8 without BOM),
///   - a hard per-request deadline, answered locally if the Core goes quiet,
///   - dropping late responses so the client never sees two replies to one id,
///   - turning a dead bus into prompt JSON-RPC errors instead of a hung client.
/// </summary>
public sealed class CockpitMcpBridge
{
    private static readonly JsonElement NullId = JsonDocument.Parse("null").RootElement.Clone();

    private readonly BridgeOptions _options;
    private readonly StdioJsonLines _stdio;
    private readonly TextWriter _log;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile BusLink? _link;
    private volatile string? _fatal;

    public CockpitMcpBridge(BridgeOptions options, StdioJsonLines stdio, TextWriter log)
    {
        _options = options;
        _stdio = stdio;
        _log = log;
    }

    /// <summary>Runs until stdin closes (exit 0) or the Core rejects this job (exit 3).</summary>
    public async Task<int> RunAsync()
    {
        try
        {
            // Connect eagerly so a misconfigured job fails before the client's initialize times out.
            await EnsureLinkAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not BridgeFatalException)
        {
            Log($"Core not reachable yet ({ex.Message}); will retry on the next request.");
        }
        catch (BridgeFatalException ex)
        {
            Log(ex.Message);
        }

        while (_fatal is null)
        {
            string? line;
            try { line = await _stdio.ReadLineAsync(_shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            if (line is null) break; // client closed stdin: normal shutdown

            await HandleClientLineAsync(line).ConfigureAwait(false);
        }

        _shutdown.Cancel();
        var link = _link;
        _link = null;
        if (link is not null) await link.DisposeAsync().ConfigureAwait(false);
        foreach (var key in _pending.Keys.ToList()) await FailPendingAsync(key, JsonRpc.CockpitUnavailable, "Bridge shutting down.").ConfigureAwait(false);
        return _fatal is null ? 0 : 3;
    }

    private async Task HandleClientLineAsync(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException)
        {
            await _stdio.WriteLineAsync(JsonRpc.ErrorResponse(NullId, JsonRpc.ParseError, "Parse error")).ConfigureAwait(false);
            return;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                // JSON-RPC batches (allowed by MCP 2025-03-26, removed later): forward each member on its own.
                foreach (var member in doc.RootElement.EnumerateArray())
                    await ForwardAsync(member).ConfigureAwait(false);
                return;
            }
            await ForwardAsync(doc.RootElement).ConfigureAwait(false);
        }
    }

    private async Task ForwardAsync(JsonElement message)
    {
        var shape = JsonRpc.Inspect(message);
        if (shape == MessageShape.Invalid)
        {
            await _stdio.WriteLineAsync(JsonRpc.ErrorResponse(NullId, JsonRpc.InvalidRequest, "Invalid JSON-RPC message.")).ConfigureAwait(false);
            return;
        }

        string raw = message.GetRawText();
        string? key = null;
        if (shape == MessageShape.Request)
        {
            var id = message.GetProperty("id").Clone();
            key = JsonRpc.IdKey(id);
            var pending = new PendingRequest(id, message.GetProperty("method").GetString()!);
            if (!_pending.TryAdd(key, pending))
            {
                await _stdio.WriteLineAsync(JsonRpc.ErrorResponse(id, JsonRpc.InvalidRequest, "Duplicate request id.")).ConfigureAwait(false);
                return;
            }
            pending.ArmTimeout(_options.RequestTimeout, () => _ = OnTimeoutAsync(key));
        }

        try
        {
            var link = await EnsureLinkAsync().ConfigureAwait(false);
            await link.Writer.WriteAsync(raw, _shutdown.Token).ConfigureAwait(false);
        }
        catch (BridgeFatalException ex)
        {
            if (key is not null) await FailPendingAsync(key, JsonRpc.CockpitUnavailable, ex.Message).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        {
            DropLink();
            if (key is not null)
                await FailPendingAsync(key, JsonRpc.CockpitUnavailable,
                    $"AI Cockpit is not reachable at {_options.Endpoint} ({ex.Message}). Is the Cockpit app running?").ConfigureAwait(false);
        }
    }

    private async Task OnTimeoutAsync(string key)
    {
        if (!_pending.TryRemove(key, out var p)) return; // already answered
        p.Dispose();
        await _stdio.WriteLineAsync(JsonRpc.ErrorResponse(p.Id, JsonRpc.CockpitTimeout,
            $"AI Cockpit did not answer {p.Method} within {_options.RequestTimeout.TotalSeconds:0} s (approval or execution took too long).")).ConfigureAwait(false);
        // Tell the Core to stop work on it; any late answer is dropped in OnCoreFrameAsync.
        var link = _link;
        if (link is null) return;
        try { await link.Writer.WriteAsync(JsonRpc.CancelledNotification(p.Id, "bridge timeout"), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private async Task FailPendingAsync(string key, int code, string message)
    {
        if (!_pending.TryRemove(key, out var p)) return;
        p.Dispose();
        await _stdio.WriteLineAsync(JsonRpc.ErrorResponse(p.Id, code, message)).ConfigureAwait(false);
    }

    private async Task<BusLink> EnsureLinkAsync()
    {
        if (_fatal is not null) throw new BridgeFatalException(_fatal);
        var link = _link;
        if (link is not null) return link;

        await _connectLock.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            if (_link is not null) return _link;
            link = await ConnectAsync().ConfigureAwait(false);
            _link = link;
            _ = Task.Run(() => ReadCoreLoopAsync(link));
            return link;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task<BusLink> ConnectAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        cts.CancelAfter(_options.ConnectTimeout);
        Stream stream;
        if (_options.Endpoint.Kind == "pipe")
        {
            // CurrentUserOnly: refuse a pipe whose owner is a different user (squatting defense on the client side).
            var pipe = new NamedPipeClientStream(".", _options.Endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.ConnectAsync(cts.Token).ConfigureAwait(false); }
            catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
            stream = pipe;
        }
        else
        {
            var tcp = new TcpClient { NoDelay = true };
            try { await tcp.ConnectAsync(_options.Endpoint.Host, _options.Endpoint.Port, cts.Token).ConfigureAwait(false); }
            catch { tcp.Dispose(); throw; }
            stream = tcp.GetStream();
        }

        var writer = new FrameWriter(stream);
        try
        {
            var hello = new BusHello(BusHello.CurrentProtocolVersion, _options.JobId, _options.Token, Environment.ProcessId, _options.Endpoint.Kind);
            await writer.WriteAsync(hello.ToJson(), cts.Token).ConfigureAwait(false);
            var frame = await FrameIO.ReadFrameAsync(stream, cts.Token).ConfigureAwait(false)
                ?? throw new IOException("Core closed the connection during the handshake.");
            var welcome = BusWelcome.Parse(frame);
            if (!welcome.Ok)
            {
                // Authentication failures are permanent for this process: the job is gone or the token is wrong.
                _fatal = $"AI Cockpit rejected this bridge: {welcome.Error}";
                throw new BridgeFatalException(_fatal);
            }
            Log($"Connected to AI Cockpit {welcome.ServerVersion} via {_options.Endpoint} as job {_options.JobId}.");
            return new BusLink(stream, writer);
        }
        catch
        {
            await writer.DisposeAsync().ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ReadCoreLoopAsync(BusLink link)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var frame = await FrameIO.ReadFrameAsync(link.Stream, _shutdown.Token).ConfigureAwait(false);
                if (frame is null) break;
                await OnCoreFrameAsync(frame).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
        {
            if (!_shutdown.IsCancellationRequested) Log($"Bus connection lost: {ex.Message}");
        }

        if (ReferenceEquals(_link, link))
        {
            DropLink();
            // Requests in flight on a dead link will never be answered; fail them now rather than at their deadline.
            foreach (var key in _pending.Keys.ToList())
                await FailPendingAsync(key, JsonRpc.CockpitUnavailable, "Connection to AI Cockpit was lost; the action may or may not have completed. Check the Cockpit activity log.").ConfigureAwait(false);
        }
    }

    private async Task OnCoreFrameAsync(byte[] frame)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(frame); }
        catch (JsonException) { Log("Dropped a malformed frame from the Core."); return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (JsonRpc.Inspect(root) == MessageShape.Response)
            {
                string key = JsonRpc.IdKey(root.GetProperty("id"));
                if (!_pending.TryRemove(key, out var p))
                    return; // late answer after a local timeout: the client already got an error for this id
                p.Dispose();
            }
            // Re-serialize only if the Core ever sent multi-line JSON; stdio framing forbids raw newlines.
            string text = Utf8.NoBom.GetString(frame);
            if (text.Contains('\n') || text.Contains('\r')) text = JsonNode.Parse(frame)!.ToJsonString();
            await _stdio.WriteLineAsync(text).ConfigureAwait(false);
        }
    }

    private void DropLink()
    {
        var link = _link;
        _link = null;
        if (link is not null) _ = link.DisposeAsync().AsTask();
    }

    private void Log(string message) =>
        _log.WriteLine($"[cockpit-mcp-bridge {_options.JobId}] {message}");

    private sealed class PendingRequest : IDisposable
    {
        private Timer? _timer;
        public JsonElement Id { get; }
        public string Method { get; }
        public PendingRequest(JsonElement id, string method) { Id = id; Method = method; }
        public void ArmTimeout(TimeSpan timeout, Action onTimeout) =>
            _timer = new Timer(_ => onTimeout(), null, timeout, Timeout.InfiniteTimeSpan);
        public void Dispose() => _timer?.Dispose();
    }

    private sealed class BusLink : IAsyncDisposable
    {
        public Stream Stream { get; }
        public FrameWriter Writer { get; }
        public BusLink(Stream stream, FrameWriter writer) { Stream = stream; Writer = writer; }
        public async ValueTask DisposeAsync()
        {
            await Writer.DisposeAsync().ConfigureAwait(false);
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class BridgeFatalException : Exception
{
    public BridgeFatalException(string message) : base(message) { }
}
