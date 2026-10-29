using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Diagnostics;
using Mediator.Core.Platform;
using Mediator.Protocol;

namespace Mediator.Core.Bus;

public sealed record BusOptions
{
    public string PipeName { get; init; } = BusEndpoint.DefaultPipeName;
    /// <summary>
    /// Allow the on-demand loopback TCP listener for WSL jobs (needs WSL mirrored networking). It never runs
    /// unless a LoopbackTcp job exists: it starts on an ephemeral 127.0.0.1 port when the first such job is
    /// issued and stops when the last one is revoked or expires.
    /// </summary>
    public bool EnableTcpForWslJobs { get; init; } = true;
    public TimeSpan PipeHelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>Unauthenticated TCP sockets are dropped after this. Short, because any local process can connect.</summary>
    public TimeSpan TcpHelloTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Most TCP sockets allowed in the handshake phase at once; extra ones are closed immediately.</summary>
    public int MaxPendingTcpHandshakes { get; init; } = 8;
    public TimeSpan ExpirySweepInterval { get; init; } = TimeSpan.FromSeconds(15);
    public string ServerVersion { get; init; } = "4.0.0";
    /// <summary>Reject a pipe client whose OS-reported PID differs from the PID it claims in its hello.</summary>
    public bool VerifyClientPid { get; init; } = true;
    /// <summary>Largest hello frame accepted from a peer that has not authenticated yet (V6; was the 16 MiB frame limit).</summary>
    public int MaxHelloBytes { get; init; } = 8 * 1024;
    /// <summary>Requests one connection may have running at once; more are answered with an error, not queued (V6).</summary>
    public int MaxInFlightPerConnection { get; init; } = 32;
}

/// <summary>Raised for the overlay's live activity panel.</summary>
public sealed record ToolCallEvent(string JobId, string CallId, string Tool, string Phase, string? Detail);

/// <summary>
/// The Mediator bus: one persistent server owned by the Core. Every MCP request from every CLI session
/// arrives here through mediator-mcp-bridge, and every rule is enforced here: tool visibility, argument
/// shape, leases, approval, execution timeouts and audit. The bridge is a dumb pipe and is never trusted.
/// </summary>
public sealed class McpBusServer : IAsyncDisposable
{
    internal static readonly string[] SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
    internal const string HandshakeAuditTool = "bus/handshake";

    private readonly BusOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _loops = new();
    private readonly ConcurrentDictionary<BusConnection, byte> _connections = new();
    private readonly object _tcpLock = new();
    private readonly SemaphoreSlim _pendingTcp;
    private TcpListenerScope? _tcp;
    private Timer? _sweep;
    private bool _firstPipeCreated;

    internal JobRegistry Jobs { get; }
    internal ToolRegistry Tools { get; }
    internal PermissionGate Gate { get; }
    internal LeaseManager Leases { get; }
    internal AuditLog Audit { get; }
    internal OperationStore Operations { get; }
    internal IMediatorLog Log { get; }
    internal string ServerVersion => _options.ServerVersion;
    internal int MaxInFlight => _options.MaxInFlightPerConnection;

    public event Action<ToolCallEvent>? ToolCallActivity;

    /// <param name="operations">The durable operation store. Null = a non-durable in-memory store (bus unit tests only;
    /// the runtime always passes the durable one).</param>
    public McpBusServer(BusOptions options, JobRegistry jobs, ToolRegistry tools, PermissionGate gate, LeaseManager leases,
        AuditLog audit, IMediatorLog? log = null, OperationStore? operations = null)
    {
        Operations = operations ?? OperationStore.OpenAsync(":memory:").GetAwaiter().GetResult();
        _options = options;
        _pendingTcp = new SemaphoreSlim(options.MaxPendingTcpHandshakes, options.MaxPendingTcpHandshakes);
        Jobs = jobs;
        Tools = tools;
        Gate = gate;
        Leases = leases;
        Audit = audit;
        Log = log ?? NullLog.Instance;
        Jobs.JobRevoked += OnJobRevoked;
        Jobs.JobIssued += OnJobIssued;
    }

    public BusEndpoint PipeEndpoint => new("pipe", _options.PipeName, "", 0);

    /// <summary>The loopback endpoint while the TCP listener runs (only while a LoopbackTcp job exists); otherwise null.</summary>
    public BusEndpoint? TcpEndpoint
    {
        get { lock (_tcpLock) return _tcp?.Endpoint; }
    }

    /// <summary>The endpoint a job's bridge must use, matching the transport bound to its grant.</summary>
    public BusEndpoint EndpointFor(JobGrant grant) => grant.Transport == BusTransport.LoopbackTcp
        ? TcpEndpoint ?? throw new InvalidOperationException("The TCP listener is not running for this WSL job.")
        : PipeEndpoint;

    public void Start()
    {
        // Create the first pipe instance synchronously so a squatter or a second Core fails loudly at startup.
        var first = CreatePipeInstance();
        lock (_tcpLock) _loops.Add(Task.Run(() => PipeAcceptLoopAsync(first)));
        _sweep = new Timer(_ => SweepExpired(), null, _options.ExpirySweepInterval, _options.ExpirySweepInterval);
        if (Jobs.HasActive(BusTransport.LoopbackTcp)) EnsureTcpListener();
        Log.Info($"Bus listening on pipe:{_options.PipeName}; TCP starts on demand for WSL jobs");
    }

    private void SweepExpired()
    {
        try { Jobs.RevokeExpired(); }
        catch (Exception ex) { Log.Warn($"Expiry sweep failed: {ex.Message}"); }
    }

    // ── Named pipe ───────────────────────────────────────────────────────────────────────────────────

    private NamedPipeServerStream CreatePipeInstance()
    {
        if (OperatingSystem.IsWindows())
        {
            // FirstPipeInstance on the first instance only: if another process already owns the name
            // (pipe squatting, or a second Core), creation fails instead of silently sharing it.
            var opts = PipeOptions.Asynchronous | (_firstPipeCreated ? PipeOptions.None : PipeOptions.FirstPipeInstance);
            var pipe = NamedPipeServerStreamAcl.Create(_options.PipeName, PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, opts, 0, 0,
                PipeSecurityFactory.CurrentUserLocalOnly());
            _firstPipeCreated = true;
            return pipe;
        }
        _firstPipeCreated = true;
        return new NamedPipeServerStream(_options.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private async Task PipeAcceptLoopAsync(NamedPipeServerStream first)
    {
        NamedPipeServerStream? next = first;
        while (!_stop.IsCancellationRequested)
        {
            var server = next ?? TryCreatePipeInstance();
            next = null;
            if (server is null)
            {
                await Task.Delay(500, _stop.Token).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
                continue;
            }
            try
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException ex)
            {
                Log.Warn($"Pipe accept failed: {ex.Message}");
                await server.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            int? clientPid = OperatingSystem.IsWindows() ? PipeSecurityFactory.GetClientProcessId(server) : null;
            _ = Task.Run(() => RunConnectionAsync(server, BusTransport.NamedPipe, clientPid, _options.PipeHelloTimeout));
        }
    }

    private NamedPipeServerStream? TryCreatePipeInstance()
    {
        try { return CreatePipeInstance(); }
        catch (IOException ex) { Log.Error("Could not create a pipe instance", ex); return null; }
    }

    // ── On-demand loopback TCP for WSL jobs ──────────────────────────────────────────────────────────

    private sealed class TcpListenerScope
    {
        public required TcpListener Listener { get; init; }
        public required CancellationTokenSource Stop { get; init; }
        public required BusEndpoint Endpoint { get; init; }
    }

    private void OnJobIssued(JobGrant grant)
    {
        if (grant.Transport == BusTransport.LoopbackTcp) EnsureTcpListener();
    }

    /// <summary>Starts the listener on an ephemeral loopback port if it is not running. Returns its endpoint.</summary>
    public BusEndpoint EnsureTcpListener()
    {
        if (!_options.EnableTcpForWslJobs)
            throw new InvalidOperationException("Loopback TCP for WSL jobs is disabled in BusOptions.");
        lock (_tcpLock)
        {
            if (_tcp is not null) return _tcp.Endpoint;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var scope = new TcpListenerScope
            {
                Listener = listener,
                Stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token),
                Endpoint = new BusEndpoint("tcp", "", "127.0.0.1", port),
            };
            _tcp = scope;
            _loops.Add(Task.Run(() => TcpAcceptLoopAsync(scope)));
            Log.Info($"TCP listener started on 127.0.0.1:{port} for WSL jobs");
            return scope.Endpoint;
        }
    }

    private void StopTcpListenerIfIdle()
    {
        TcpListenerScope? stopping;
        lock (_tcpLock)
        {
            if (_tcp is null || Jobs.HasActive(BusTransport.LoopbackTcp)) return;
            stopping = _tcp;
            _tcp = null;
        }
        stopping.Stop.Cancel();
        stopping.Listener.Stop();
        Log.Info($"TCP listener on port {stopping.Endpoint.Port} stopped: no WSL jobs left");
    }

    private async Task TcpAcceptLoopAsync(TcpListenerScope scope)
    {
        while (!scope.Stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await scope.Listener.AcceptTcpClientAsync(scope.Stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                if (scope.Stop.IsCancellationRequested) break;
                Log.Warn($"TCP accept failed: {ex.Message}");
                continue;
            }

            // Cap sockets that have not authenticated yet, so a local flood cannot exhaust the bus.
            if (!_pendingTcp.Wait(0))
            {
                Log.Warn("Too many unauthenticated TCP connections; dropping one.");
                client.Dispose();
                continue;
            }
            client.NoDelay = true;
            _ = Task.Run(async () =>
            {
                using (client)
                    await RunConnectionAsync(client.GetStream(), BusTransport.LoopbackTcp, null, _options.TcpHelloTimeout, _pendingTcp).ConfigureAwait(false);
            });
        }
    }

    // ── Handshake and connection lifetime ─────────────────────────────────────────────────────────────

    private async Task RunConnectionAsync(Stream stream, BusTransport transport, int? clientPid, TimeSpan helloTimeout, SemaphoreSlim? handshakeSlot = null)
    {
        await using var streamScope = stream.ConfigureAwait(false);
        var writer = new FrameWriter(stream);
        bool slotHeld = handshakeSlot is not null;
        void ReleaseSlot()
        {
            if (!slotHeld) return;
            slotHeld = false;
            handshakeSlot!.Release();
        }

        try
        {
            BusHello hello;
            try
            {
                using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                helloCts.CancelAfter(helloTimeout);
                var frame = await FrameIO.ReadFrameAsync(stream, _options.MaxHelloBytes, helloCts.Token).ConfigureAwait(false);
                if (frame is null) return;
                hello = BusHello.Parse(frame);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or JsonException)
            {
                Log.Warn($"Dropped {transport} client before hello completed: {ex.GetType().Name}");
                return;
            }

            var (reject, grant) = Authenticate(hello, transport, clientPid);
            if (reject is not null)
                await AuditHandshakeRejectionAsync(hello, transport, clientPid, reject).ConfigureAwait(false);
            try
            {
                await writer.WriteAsync(new BusWelcome(reject is null, reject, _options.ServerVersion).ToJson(), _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                return;
            }
            if (reject is not null || grant is null)
            {
                Log.Warn($"Rejected {transport} bridge (claimed job '{hello.JobId}', pid {hello.BridgePid}, os pid {clientPid?.ToString(CultureInfo.InvariantCulture) ?? "n/a"}): {reject}");
                return;
            }
            ReleaseSlot();

            var conn = new BusConnection(this, grant, stream, writer);
            _connections[conn] = 0;
            Log.Info($"Bridge connected: job {grant.JobId} via {transport}, pid {hello.BridgePid}");
            try
            {
                await conn.RunAsync(_stop.Token).ConfigureAwait(false);
            }
            finally
            {
                _connections.TryRemove(conn, out _);
                conn.Dispose();
                Log.Info($"Bridge disconnected: job {grant.JobId}");
            }
        }
        finally
        {
            ReleaseSlot();
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private (string? Reject, JobGrant? Grant) Authenticate(BusHello hello, BusTransport transport, int? clientPid)
    {
        if (hello.ProtocolVersion != BusHello.CurrentProtocolVersion)
            return ($"Bus protocol {hello.ProtocolVersion} is not supported (server speaks {BusHello.CurrentProtocolVersion}). Update mediator-mcp-bridge.", null);
        if (!Jobs.TryAuthenticate(hello.JobId, hello.Token, out var grant) || grant is null)
            return ("Unknown, expired or revoked job, or wrong token.", null);
        if (grant.Transport != transport)
            return ($"Transport mismatch: this job is bound to {grant.Transport} but connected over {transport}.", null);
        if (transport == BusTransport.NamedPipe && _options.VerifyClientPid && clientPid is int osPid && osPid != hello.BridgePid)
            return ($"Client PID mismatch (os {osPid}, claimed {hello.BridgePid}).", null);
        return (null, grant);
    }

    private Task AuditHandshakeRejectionAsync(BusHello hello, BusTransport transport, int? clientPid, string reason)
    {
        // Never log the presented token; record what was claimed and from where.
        string args = new JsonObject
        {
            ["transport"] = transport.ToString(),
            ["bridgePid"] = hello.BridgePid,
            ["osPid"] = clientPid,
            ["protocolVersion"] = hello.ProtocolVersion,
        }.ToJsonString();
        string jobId = hello.JobId.Length <= 64 ? hello.JobId : hello.JobId[..64];
        return Audit.RecordAsync(new AuditEntry(jobId, HandshakeAuditTool, "-", ToolRisk.ReadOnly, args, "rejected", "not_run", reason, 0));
    }

    private void OnJobRevoked(string jobId)
    {
        Leases.ReleaseAll(jobId);
        foreach (var conn in _connections.Keys.Where(c => c.Job.JobId == jobId))
            conn.Close();
        StopTcpListenerIfIdle();
    }

    internal void Raise(ToolCallEvent e)
    {
        try { ToolCallActivity?.Invoke(e); }
        catch (Exception ex) { Log.Warn($"Activity listener threw: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        Jobs.JobRevoked -= OnJobRevoked;
        Jobs.JobIssued -= OnJobIssued;
        _sweep?.Dispose();
        _stop.Cancel();
        Task[] loops;
        lock (_tcpLock)
        {
            _tcp?.Listener.Stop();
            _tcp = null;
            loops = _loops.ToArray();
        }
        foreach (var c in _connections.Keys) c.Close();
        try { await Task.WhenAll(loops).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

internal sealed class RpcException : Exception
{
    public int Code { get; }
    public RpcException(int code, string message) : base(message) => Code = code;
}

/// <summary>One bridge connection: a read loop plus concurrently running request handlers.</summary>
internal sealed class BusConnection : IDisposable
{
    private static readonly JsonElement NullId = JsonDocument.Parse("null").RootElement.Clone();
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly McpBusServer _server;
    private readonly Stream _stream;
    private readonly FrameWriter _writer;
    private readonly CancellationTokenSource _closed = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new(StringComparer.Ordinal);
    private long _callSeq;

    public JobGrant Job { get; }

    public BusConnection(McpBusServer server, JobGrant job, Stream stream, FrameWriter writer)
    {
        _server = server;
        Job = job;
        _stream = stream;
        _writer = writer;
    }

    public async Task RunAsync(CancellationToken serverStop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStop, _closed.Token);
        var ct = linked.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await FrameIO.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                if (frame is null) break;
                Dispatch(frame, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
        catch (InvalidDataException ex) { _server.Log.Warn($"Job {Job.JobId}: framing error, closing: {ex.Message}"); }
        finally
        {
            foreach (var cts in _inflight.Values)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private void Dispatch(byte[] frame, CancellationToken ct)
    {
        // V6: System.Text.Json does not validate UTF-8 inside strings it has not decoded yet, so a frame with invalid
        // bytes in, say, the method name parsed and then threw later on the read loop. Validate up front.
        if (!System.Text.Unicode.Utf8.IsValid(frame))
        {
            _ = SendRawAsync(JsonRpc.ErrorResponse(NullId, JsonRpc.ParseError, "Parse error: the frame is not valid UTF-8."));
            return;
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(frame); }
        catch (JsonException)
        {
            _ = SendRawAsync(JsonRpc.ErrorResponse(NullId, JsonRpc.ParseError, "Parse error"));
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            switch (JsonRpc.Inspect(root))
            {
                case MessageShape.Request:
                {
                    var id = root.GetProperty("id").Clone();
                    string method = root.GetProperty("method").GetString()!;
                    var @params = root.TryGetProperty("params", out var p) ? p.Clone() : EmptyObject;
                    string key = JsonRpc.IdKey(id);
                    if (_inflight.Count >= _server.MaxInFlight)
                    {
                        _ = SendRawAsync(JsonRpc.ErrorResponse(id, JsonRpc.InvalidRequest,
                            $"Too many requests in progress on this connection (limit {_server.MaxInFlight}). Wait for earlier calls to finish."));
                        return;
                    }
                    var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (!_inflight.TryAdd(key, cts))
                    {
                        cts.Dispose();
                        _ = SendRawAsync(JsonRpc.ErrorResponse(id, JsonRpc.InvalidRequest, "Duplicate request id while the first is still running."));
                        return;
                    }
                    _ = Task.Run(() => HandleRequestAsync(id, key, method, @params, cts));
                    return;
                }
                case MessageShape.Notification:
                {
                    string method = root.GetProperty("method").GetString()!;
                    if (method == "notifications/cancelled"
                        && root.TryGetProperty("params", out var cp)
                        && cp.ValueKind == JsonValueKind.Object
                        && cp.TryGetProperty("requestId", out var rid)
                        && _inflight.TryGetValue(JsonRpc.IdKey(rid), out var target))
                    {
                        try { target.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    // notifications/initialized and others need no action.
                    return;
                }
                case MessageShape.Response:
                    return; // The bus never sends requests to the client, so responses are ignored.
                default:
                    _ = SendRawAsync(JsonRpc.ErrorResponse(NullId, JsonRpc.InvalidRequest, "Invalid JSON-RPC message."));
                    return;
            }
        }
    }

    private async Task HandleRequestAsync(JsonElement id, string key, string method, JsonElement @params, CancellationTokenSource cts)
    {
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(@params),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(@params, cts.Token).ConfigureAwait(false),
                _ => throw new RpcException(JsonRpc.MethodNotFound, $"Method '{method}' is not supported by the Mediator bus."),
            };
            var response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = result,
            };
            await SendRawAsync(response.ToJsonString()).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            await SendRawAsync(JsonRpc.ErrorResponse(id, ex.Code, ex.Message)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the client or by disconnect. MCP: the receiver should not answer a cancelled request.
        }
        catch (Exception ex)
        {
            _server.Log.Error($"Job {Job.JobId}: {method} failed", ex);
            await SendRawAsync(JsonRpc.ErrorResponse(id, JsonRpc.InternalError, "Internal error: " + ex.Message)).ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
            cts.Dispose();
        }
    }

    private JsonNode Initialize(JsonElement @params)
    {
        string? requested = @params.ValueKind == JsonValueKind.Object && @params.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
        string version = requested is not null && McpBusServer.SupportedProtocolVersions.Contains(requested)
            ? requested : McpBusServer.SupportedProtocolVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "mediator", ["version"] = _server.ServerVersion },
            ["instructions"] = "Tools are executed by the Mediator on the user's Windows desktop. Write actions wait for the user's approval in the Mediator window; a denial is final for that call.",
        };
    }

    private JsonNode ListTools()
    {
        var tools = new JsonArray();
        foreach (var t in _server.Tools.VisibleTo(Job))
        {
            tools.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["readOnlyHint"] = t.Risk == ToolRisk.ReadOnly,
                    ["destructiveHint"] = t.Risk == ToolRisk.Destructive,
                    ["openWorldHint"] = false,
                },
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonNode> CallToolAsync(JsonElement @params, CancellationToken ct)
    {
        if (@params.ValueKind != JsonValueKind.Object || !@params.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            throw new RpcException(JsonRpc.InvalidParams, "tools/call requires a string 'name'.");
        string name = nameEl.GetString()!;
        var args = @params.TryGetProperty("arguments", out var a) && a.ValueKind != JsonValueKind.Null ? a : EmptyObject;

        // Visibility is enforced again here: a client may call a tool it was never shown.
        if (!_server.Tools.TryGet(name, out var provider, out var tool) || !Job.AllowedTools.Contains(name))
        {
            // Calls to tools a job was never shown are a probing or injection signal: record them.
            await _server.Audit.RecordAsync(new AuditEntry(Job.JobId, name, "-", ToolRisk.ReadOnly, args.GetRawText(), "rejected", "not_run",
                "tool not registered or not granted to this job", 0)).ConfigureAwait(false);
            throw new RpcException(JsonRpc.InvalidParams, $"Unknown tool: {name}");
        }

        string callId = $"{Job.JobId}:{Interlocked.Increment(ref _callSeq).ToString(CultureInfo.InvariantCulture)}";
        var call = new ToolCallContext(Job, tool, args, callId);
        var clock = Stopwatch.StartNew();
        string argsJson = args.GetRawText();
        var ops = _server.Operations;

        // Durable operation record for anything with side effects (V6). ReadOnly tools have none to protect.
        OperationRecord? op = null;
        if (tool.Risk != ToolRisk.ReadOnly)
        {
            var (created, existing) = await ops.BeginAsync(Job.JobId, tool.Name, OperationStore.RequestKey(Job.JobId, tool.Name, args), argsJson).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.State == OperationState.Committed && existing.ResultJson is string stored)
                {
                    // Replay after commit: return what happened, never do it twice.
                    var prior = FromStored(stored);
                    var replay = prior with { Text = $"This exact call already completed (operation {existing.OperationId}); it was not run again. Its result:\n" + prior.Text };
                    await _server.Audit.RecordAsync(new AuditEntry(Job.JobId, tool.Name, callId, tool.Risk, argsJson, "replayed", "not_run",
                        $"identical to committed operation {existing.OperationId}", clock.ElapsedMilliseconds)).ConfigureAwait(false);
                    _server.Raise(new ToolCallEvent(Job.JobId, callId, tool.Name, "replayed", existing.OperationId));
                    return ToMcpResult(replay);
                }
                string why = OperationStore.IsInFlight(existing.State)
                    ? "is already in progress"
                    : $"is {OperationStore.Snake(existing.State)} ({existing.Note ?? "its outcome is not known"})";
                var refused = ToolResult.Fail($"An identical call (operation {existing.OperationId}) {why}. It may already have taken effect, so it was not run again. " +
                    "Check it with mediator_operation_status, or ask the user; do not retry it.");
                await _server.Audit.RecordAsync(new AuditEntry(Job.JobId, tool.Name, callId, tool.Risk, argsJson, "rejected", "not_run",
                    $"duplicate of {existing.OperationId} ({OperationStore.Snake(existing.State)})", clock.ElapsedMilliseconds)).ConfigureAwait(false);
                _server.Raise(new ToolCallEvent(Job.JobId, callId, tool.Name, "duplicate_refused", existing.OperationId));
                return ToMcpResult(refused);
            }
            op = created;
        }

        async Task Move(OperationState to, string? note = null, ToolResult? result = null, string? approval = null)
        {
            if (op is null) return;
            op = await ops.TransitionAsync(op.OperationId, to, note, result is null ? null : ToStored(result), approval).ConfigureAwait(false);
        }

        async Task<JsonNode> Finish(string decision, string outcome, ToolResult result)
        {
            string? detail = result.IsError ? result.Text : null;
            if (op is not null) detail = $"[{op.OperationId}] " + (detail ?? "");
            await _server.Audit.RecordAsync(new AuditEntry(Job.JobId, tool.Name, callId, tool.Risk, argsJson, decision, outcome,
                detail, clock.ElapsedMilliseconds)).ConfigureAwait(false);
            _server.Raise(new ToolCallEvent(Job.JobId, callId, tool.Name, outcome, result.IsError ? result.Text : null));
            return ToMcpResult(op is null ? result : result with { Structured = WithOperation(result.Structured, op) });
        }

        _server.Raise(new ToolCallEvent(Job.JobId, callId, tool.Name, "received", null));

        if (ArgumentValidator.Validate(tool.InputSchema, args) is string invalid)
        {
            await Move(OperationState.Rejected, invalid).ConfigureAwait(false);
            return await Finish("rejected", "not_run", ToolResult.Fail(invalid)).ConfigureAwait(false);
        }

        if (tool.LeaseResource is string resource && !_server.Leases.TryAcquire(resource, Job.JobId, out var holder))
        {
            var busy = ToolResult.Fail($"{resource} is in use by job {holder}. Wait for that job to finish; do not work around it.");
            await Move(OperationState.Rejected, busy.Text).ConfigureAwait(false);
            return await Finish("rejected", "not_run", busy).ConfigureAwait(false);
        }
        await Move(OperationState.Validated).ConfigureAwait(false);

        bool auto = PermissionGate.EffectiveMode(Job, tool) == ApprovalMode.Auto;
        if (!auto) _server.Raise(new ToolCallEvent(Job.JobId, callId, tool.Name, "awaiting_approval", null));
        var outcome = await _server.Gate.CheckAsync(call, c => provider.BuildPreviewAsync(call, c), ct).ConfigureAwait(false);
        switch (outcome.Decision)
        {
            case GateDecision.Cancelled:
                await Move(OperationState.Cancelled, "request cancelled while awaiting approval; not run").ConfigureAwait(false);
                await _server.Audit.RecordAsync(new AuditEntry(Job.JobId, tool.Name, callId, tool.Risk, argsJson, "cancelled", "not_run",
                    "Request cancelled while awaiting approval.", clock.ElapsedMilliseconds)).ConfigureAwait(false);
                throw new OperationCanceledException(ct);
            case GateDecision.Denied:
                await Move(OperationState.Denied, outcome.KillRequested ? "the user stopped the job" : "the user denied it").ConfigureAwait(false);
                return await Finish(outcome.KillRequested ? "killed" : "denied", "not_run",
                    ToolResult.Fail(outcome.KillRequested
                        ? "The user stopped this job. Do not continue."
                        : "The user denied this action. Do not retry it; ask the user how to proceed.")).ConfigureAwait(false);
            case GateDecision.TimedOut:
                await Move(OperationState.Denied, "no approval within the time limit").ConfigureAwait(false);
                return await Finish("timed_out", "not_run",
                    ToolResult.Fail("No approval within the time limit, so the action was not run.")).ConfigureAwait(false);
        }

        call = call with { ApprovedRows = outcome.ApprovedRows, ApprovedPreview = outcome.Preview };
        string approvedAs = auto ? "auto" : outcome.ApprovedRows is null ? "approved" : "approved_subset";
        await Move(OperationState.Approved, null, null, new JsonObject
        {
            ["decision"] = approvedAs,
            ["rows"] = outcome.ApprovedRows is null ? null : new JsonArray(outcome.ApprovedRows.Order(StringComparer.Ordinal).Select(r => (JsonNode)r).ToArray()),
            ["binding"] = outcome.Preview?["binding"]?.DeepClone(),
        }.ToJsonString()).ConfigureAwait(false);

        // Revocation is final: a grant revoked while the call waited for approval (job killed, expired, ended) never
        // executes, even if the approval arrived a moment later.
        if (!_server.Jobs.IsCurrent(Job))
        {
            await Move(OperationState.Cancelled, "the job's access was revoked before the call ran; not run").ConfigureAwait(false);
            return await Finish(approvedAs, "not_run", ToolResult.Fail("This job's access was revoked; the action was not run.")).ConfigureAwait(false);
        }

        // Durable BEFORE the side effect: from here on, a crash means "may have happened".
        await Move(OperationState.Executing).ConfigureAwait(false);
        _server.Raise(new ToolCallEvent(Job.JobId, callId, tool.Name, "running", null));
        using var exec = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var providerTask = Task.Run(() => provider.ExecuteAsync(call, exec.Token), CancellationToken.None);
        ToolResult result;
        try
        {
            result = await providerTask.WaitAsync(tool.ExecutionTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The tool did not return in time. Ask it to stop, but do not pretend it stopped: the outcome is unknown
            // until it reports. A late result is still recorded.
            exec.Cancel();
            string note = $"timed out after {tool.ExecutionTimeout.TotalSeconds:0} s while running; it may still complete";
            await Move(OperationState.OutcomeUnknown, note).ConfigureAwait(false);
            ObserveLate(providerTask, op, callId, tool, argsJson);
            return await Finish(approvedAs, "outcome_unknown", ToolResult.Fail(
                $"{tool.Name} did not finish within {tool.ExecutionTimeout.TotalSeconds:0} s. Its outcome is unknown: it may still complete. " +
                "Do not retry it; check with mediator_operation_status.")).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled by the client or a disconnect while the tool was running. Cancellation is a request, not a fact.
            exec.Cancel();
            await Move(OperationState.CancellationRequested, "the client cancelled while it was running").ConfigureAwait(false);
            string final;
            try
            {
                var late = await providerTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                await Move(late.IsError ? OperationState.Failed : OperationState.Committed, "finished after the cancellation request", late).ConfigureAwait(false);
                final = late.IsError ? "tool_error_after_cancel" : "committed_after_cancel";
            }
            catch (TimeoutException)
            {
                await Move(OperationState.OutcomeUnknown, "still running 5 s after cancellation; it may complete").ConfigureAwait(false);
                ObserveLate(providerTask, op, callId, tool, argsJson);
                final = "outcome_unknown";
            }
            catch (Exception ex)
            {
                await Move(OperationState.OutcomeUnknown, $"stopped on cancellation ({ex.GetType().Name}); partial effects are possible").ConfigureAwait(false);
                final = "outcome_unknown";
            }
            await _server.Audit.RecordAsync(new AuditEntry(Job.JobId, tool.Name, callId, tool.Risk, argsJson, approvedAs,
                final, $"[{op?.OperationId}] cancellation requested during execution", clock.ElapsedMilliseconds)).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _server.Log.Error($"Job {Job.JobId}: {tool.Name} threw", ex);
            await Move(OperationState.OutcomeUnknown, $"threw {ex.GetType().Name}; partial effects are possible").ConfigureAwait(false);
            return await Finish(approvedAs, "outcome_unknown", ToolResult.Fail($"{tool.Name} failed with an error and may have partly taken effect: {ex.Message}. Do not retry it blindly.")).ConfigureAwait(false);
        }
        await Move(result.IsError ? OperationState.Failed : OperationState.Committed, null, result).ConfigureAwait(false);
        return await Finish(approvedAs, result.IsError ? "tool_error" : "ok", result).ConfigureAwait(false);
    }

    /// <summary>Records the eventual outcome of a call that outlived its timeout or cancellation.</summary>
    private void ObserveLate(Task<ToolResult> providerTask, OperationRecord? op, string callId, ToolDescriptor tool, string argsJson)
    {
        if (op is null) return;
        _ = providerTask.ContinueWith(async t =>
        {
            try
            {
                string outcome;
                if (t.IsCompletedSuccessfully)
                {
                    await _server.Operations.TransitionAsync(op.OperationId, t.Result.IsError ? OperationState.Failed : OperationState.Committed,
                        "late result after the call had already been answered", ToStored(t.Result)).ConfigureAwait(false);
                    outcome = t.Result.IsError ? "late_tool_error" : "late_ok";
                }
                else outcome = "late_exception";
                await _server.Audit.RecordAsync(new AuditEntry(Job.JobId, tool.Name, callId, tool.Risk, argsJson, "late", outcome,
                    $"[{op.OperationId}] " + (t.IsCompletedSuccessfully ? t.Result.Text : t.Exception?.GetBaseException().Message), 0)).ConfigureAwait(false);
                _server.Raise(new ToolCallEvent(Job.JobId, callId, tool.Name, outcome, op.OperationId));
            }
            catch (Exception ex) when (ex is OperationStateException or ObjectDisposedException or InvalidOperationException)
            {
                _server.Log.Warn($"Late result for {op.OperationId} could not be recorded: {ex.Message}");
            }
        }, TaskScheduler.Default).Unwrap();
    }

    private static string ToStored(ToolResult r) => new JsonObject { ["isError"] = r.IsError, ["text"] = r.Text, ["structured"] = r.Structured?.DeepClone() }.ToJsonString();

    private static ToolResult FromStored(string json)
    {
        var o = JsonNode.Parse(json)!;
        return new ToolResult((bool)o["isError"]!, (string)o["text"]!, o["structured"]?.DeepClone());
    }

    private static JsonNode WithOperation(JsonNode? structured, OperationRecord op)
    {
        var obj = structured is JsonObject so ? (JsonObject)so.DeepClone() : structured is null ? new JsonObject() : new JsonObject { ["value"] = structured.DeepClone() };
        obj["operation"] = new JsonObject { ["id"] = op.OperationId, ["state"] = OperationStore.Snake(op.State) };
        return obj;
    }

    private static JsonNode ToMcpResult(ToolResult r)
    {
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.Text });
        foreach (var image in r.Images)
            content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(image.Data), ["mimeType"] = image.MediaType });
        var obj = new JsonObject
        {
            ["content"] = content,
            ["isError"] = r.IsError,
        };
        if (r.Structured is not null) obj["structuredContent"] = r.Structured.DeepClone();
        return obj;
    }

    private async Task SendRawAsync(string json)
    {
        try
        {
            await _writer.WriteAsync(json, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The bridge went away; its in-flight requests are cancelled by the read loop.
        }
    }

    public void Close()
    {
        try { _closed.Cancel(); } catch (ObjectDisposedException) { }
        try { _stream.Dispose(); } catch (IOException) { }
    }

    public void Dispose() => _closed.Dispose();
}
