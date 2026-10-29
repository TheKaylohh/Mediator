using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Diagnostics;

namespace Mediator.Core.Browser;

/// <summary>A request to the Chrome extension failed: not connected, it disconnected, it timed out, or the page refused.</summary>
public sealed class BrowserBridgeException(string message) : Exception(message);

/// <summary>
/// V13: the Mediator end of the "Mediator for Chrome" extension. A WebSocket server on 127.0.0.1 (never another
/// interface), built on a plain <see cref="TcpListener"/> plus <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>:
/// HttpListener would need an administrator-registered URL ACL for 127.0.0.1, this needs nothing.
///
/// Who may connect: the upgrade request must carry Origin chrome-extension://&lt;the extension's fixed id&gt; (a web page
/// cannot send that header, so sites cannot talk to it) and a loopback Host (no DNS rebinding). Then both sides prove
/// they know the pairing secret (HMAC-SHA256 over both nonces, server first), so a process that grabbed the port cannot
/// drive the user's browser, and a stray client cannot pose as the extension.
///
/// One extension connection is active at a time (the newest authenticated one). Requests are JSON-RPC style
/// {id, method, params}; answers {id, result} or {id, error:{message}} complete a TaskCompletionSource per id.
/// Messages of any size are sent and received as a series of frames (<see cref="FrameBytes"/>), up to <see cref="MaxMessageBytes"/>.
/// </summary>
public sealed class ChromeBridgeServer : IAsyncDisposable
{
    public const int DefaultPort = 19845;
    public const string PathPrefix = "/chrome-bridge";
    public const int FrameBytes = 64 * 1024;
    public const int MaxMessageBytes = 32 * 1024 * 1024;
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly string _token;
    private readonly string _allowedOrigin;
    private readonly IMediatorLog? _log;
    private readonly int _firstPort;
    private readonly int _portAttempts;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _handshakes = new(4);
    private TcpListener? _listener;
    private Connection? _active;
    private long _nextId;

    /// <param name="token">The pairing secret (also written to the extension's pairing.json).</param>
    /// <param name="allowedOrigin">chrome-extension://&lt;id&gt; of the one extension allowed in.</param>
    /// <param name="firstPort">0: any free port (tests).</param>
    public ChromeBridgeServer(string token, string allowedOrigin, IMediatorLog? log = null, int firstPort = DefaultPort, int portAttempts = 10)
    {
        if (token.Length < 32) throw new ArgumentException("The pairing token must be at least 32 characters.", nameof(token));
        _token = token;
        _allowedOrigin = allowedOrigin;
        _log = log;
        _firstPort = firstPort;
        _portAttempts = Math.Max(1, portAttempts);
    }

    /// <summary>The port in use after <see cref="Start"/>.</summary>
    public int Port { get; private set; }
    public bool IsConnected => Volatile.Read(ref _active) is not null;
    /// <summary>The connected extension's version (from its hello), or null.</summary>
    public string? ExtensionVersion => Volatile.Read(ref _active)?.Version;
    /// <summary>Raised with true when an extension finishes pairing, false when the active one goes away.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>Binds 127.0.0.1 on the first free port from the configured one and starts accepting.</summary>
    public void Start()
    {
        if (_listener is not null) return;
        SocketException? last = null;
        for (int i = 0; i < _portAttempts; i++)
        {
            int port = _firstPort == 0 ? 0 : _firstPort + i;
            var l = new TcpListener(IPAddress.Loopback, port);
            if (OperatingSystem.IsWindows()) l.Server.ExclusiveAddressUse = true;
            try { l.Start(); }
            catch (SocketException ex) { last = ex; continue; }
            _listener = l;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            _ = Task.Run(AcceptLoopAsync);
            _log?.Info($"Chrome extension bridge listening on 127.0.0.1:{Port}{PathPrefix}");
            return;
        }
        throw new InvalidOperationException($"No free port for the Chrome extension bridge ({_firstPort}-{_firstPort + _portAttempts - 1}): {last?.Message}");
    }

    /// <summary>V14.1: a message the paired extension sent on its own ({type, ...}, not an answer to a request), e.g. a side panel chat.</summary>
    public event Action<JsonObject>? ExtensionMessage;

    /// <summary>V14.1: sends a one-way message ({type, ...}) to the paired extension. False when none is connected or the send failed.</summary>
    public async Task<bool> NotifyAsync(JsonObject message, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _active) is not { } conn) return false;
        try { await conn.SendAsync(message, ct).ConfigureAwait(false); return true; }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException) { return false; }
    }

    /// <summary>Waits until an extension is paired (true) or the time is up (false).</summary>
    public async Task<bool> WaitForConnectionAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (IsConnected) return true;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(bool up) { if (up) tcs.TrySetResult(true); }
        ConnectionChanged += On;
        try
        {
            if (IsConnected) return true;
            try { return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException) { return IsConnected; }
        }
        finally { ConnectionChanged -= On; }
    }

    /// <summary>Sends one request to the extension and returns its result.</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonObject? parameters, TimeSpan timeout, CancellationToken ct = default)
    {
        var conn = Volatile.Read(ref _active) ?? throw new BrowserBridgeException(
            "The Mediator for Chrome extension is not connected. Open Chrome; if it still does not connect, use Connectors > Chrome extension > Set up in Mediator.");
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Pending[id] = tcs;
        try
        {
            var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
            await conn.SendAsync(msg, ct).ConfigureAwait(false);
            try { return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new BrowserBridgeException($"Chrome did not answer {method} within {timeout.TotalSeconds:0} s."); }
        }
        catch (WebSocketException ex) { throw new BrowserBridgeException("The Chrome extension disconnected: " + ex.Message); }
        finally { conn.Pending.TryRemove(id, out _); }
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { _log?.Warn("Chrome bridge accept failed: " + ex.Message); continue; }
            if (!await _handshakes.WaitAsync(0, ct).ConfigureAwait(false)) { client.Dispose(); continue; } // a flood of half-open sockets
            _ = Task.Run(async () =>
            {
                Connection? conn = null;
                try { conn = await HandshakeAsync(client, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or OperationCanceledException or JsonException or InvalidOperationException)
                {
                    _log?.Info("Chrome bridge: connection refused (" + ex.Message + ")");
                }
                finally { _handshakes.Release(); }
                if (conn is null) { client.Dispose(); return; }
                await RunAsync(conn, client).ConfigureAwait(false);
            }, CancellationToken.None);
        }
    }

    /// <summary>HTTP upgrade (checked), then the pairing exchange. Null when refused (the reason is logged).</summary>
    private async Task<Connection?> HandshakeAsync(TcpClient client, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(HandshakeTimeout);
        var ct = timeout.Token;
        var stream = client.GetStream();
        var request = await ReadHttpHeadAsync(stream, ct).ConfigureAwait(false);
        string? refusal = CheckUpgrade(request, Port, _allowedOrigin, out string? key);
        if (refusal is not null)
        {
            await WriteAsciiAsync(stream, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct).ConfigureAwait(false);
            _log?.Info("Chrome bridge: refused an upgrade: " + refusal);
            return null;
        }
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
        await WriteAsciiAsync(stream, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n", ct).ConfigureAwait(false);
        var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
        var conn = new Connection(ws);

        // 1. hello {nonce, version}  2. challenge {nonce, proof=H(server|cn|sn)}  3. auth {proof=H(client|sn|cn)}  4. ready
        var hello = await conn.ReceiveAsync(ct).ConfigureAwait(false);
        string clientNonce = (string?)hello?["nonce"] ?? "";
        if ((string?)hello?["type"] != "hello" || clientNonce.Length is < 16 or > 128)
            return await RefuseAsync(conn, "no hello").ConfigureAwait(false);
        string serverNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await conn.SendAsync(new JsonObject { ["type"] = "challenge", ["nonce"] = serverNonce, ["proof"] = Proof(_token, $"server|{clientNonce}|{serverNonce}") }, ct).ConfigureAwait(false);
        var auth = await conn.ReceiveAsync(ct).ConfigureAwait(false);
        string expected = Proof(_token, $"client|{serverNonce}|{clientNonce}");
        string got = (string?)auth?["proof"] ?? "";
        if ((string?)auth?["type"] != "auth" || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(got)))
            return await RefuseAsync(conn, "wrong pairing proof").ConfigureAwait(false);
        await conn.SendAsync(new JsonObject { ["type"] = "ready" }, ct).ConfigureAwait(false);
        conn.Version = (string?)hello?["version"];
        return conn;
    }

    private async Task<Connection?> RefuseAsync(Connection conn, string why)
    {
        _log?.Info("Chrome bridge: pairing refused: " + why);
        try { await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, why, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException) { }
        conn.Socket.Dispose();
        return null;
    }

    private async Task RunAsync(Connection conn, TcpClient client)
    {
        var old = Interlocked.Exchange(ref _active, conn);
        old?.Abort("a newer extension connection replaced it");
        _log?.Info($"Chrome extension connected (version {conn.Version ?? "?"}).");
        ConnectionChanged?.Invoke(true);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var msg = await conn.ReceiveAsync(_stop.Token).ConfigureAwait(false);
                if (msg is null) break;
                string? type = (string?)msg["type"];
                if (type == "ping") { await conn.SendAsync(new JsonObject { ["type"] = "pong" }, _stop.Token).ConfigureAwait(false); continue; }
                if (type == "pong") continue;
                if (msg["id"] is JsonValue idv && idv.TryGetValue(out long id) && conn.Pending.TryGetValue(id, out var tcs))
                {
                    if (msg["error"] is JsonObject err) tcs.TrySetException(new BrowserBridgeException((string?)err["message"] ?? "The extension reported an error."));
                    else tcs.TrySetResult(msg["result"]?.DeepClone());
                }
                else if (type is not null && msg is JsonObject note)
                {
                    // V14.1: messages the extension starts itself (the side panel chat).
                    try { ExtensionMessage?.Invoke(note); }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { _log?.Warn("Chrome extension message failed: " + ex.Message); }
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or ObjectDisposedException)
        {
            if (!_stop.IsCancellationRequested) _log?.Info("Chrome extension connection ended: " + ex.Message);
        }
        finally
        {
            conn.Abort("the extension disconnected");
            client.Dispose();
            if (Interlocked.CompareExchange(ref _active, null, conn) == conn)
            {
                _log?.Info("Chrome extension disconnected.");
                ConnectionChanged?.Invoke(false);
            }
        }
    }

    /// <summary>Why an upgrade request is refused, or null when it is a well-formed WebSocket upgrade from the extension.</summary>
    internal static string? CheckUpgrade(HttpHead? request, int port, string allowedOrigin, out string? key)
    {
        key = null;
        if (request is null) return "not an HTTP request";
        if (request.Method != "GET") return "method " + request.Method;
        string path = request.Target.Split('?')[0].TrimEnd('/');
        if (!string.Equals(path, PathPrefix, StringComparison.Ordinal)) return "path " + path;
        var h = request.Headers;
        if (!h.TryGetValue("Upgrade", out var up) || !up.Equals("websocket", StringComparison.OrdinalIgnoreCase)) return "not a WebSocket upgrade";
        if (!h.TryGetValue("Connection", out var conn) || !conn.Split(',').Any(p => p.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))) return "no Connection: Upgrade";
        if (!h.TryGetValue("Sec-WebSocket-Version", out var ver) || ver.Trim() != "13") return "WebSocket version";
        if (!h.TryGetValue("Sec-WebSocket-Key", out key) || key.Trim().Length is < 16 or > 64) return "no key";
        key = key.Trim();
        if (!h.TryGetValue("Origin", out var origin) || !string.Equals(origin.Trim(), allowedOrigin, StringComparison.Ordinal)) return "origin " + (origin ?? "(none)");
        if (!h.TryGetValue("Host", out var host)) return "no Host";
        string hostOnly = host.Trim().ToLowerInvariant();
        string[] allowedHosts = [$"127.0.0.1:{port}", $"localhost:{port}"];
        if (!allowedHosts.Contains(hostOnly, StringComparer.Ordinal)) return "host " + hostOnly;
        return null;
    }

    internal sealed record HttpHead(string Method, string Target, IReadOnlyDictionary<string, string> Headers);

    /// <summary>Reads the request line and headers (up to 8 KB). The client sends no WebSocket data before our 101, so nothing is over-read.</summary>
    private static async Task<HttpHead?> ReadHttpHeadAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int n = 0;
        while (n < buf.Length)
        {
            int read = await stream.ReadAsync(buf.AsMemory(n, 1), ct).ConfigureAwait(false);
            if (read == 0) return null;
            n++;
            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n') return ParseHttpHead(Encoding.ASCII.GetString(buf, 0, n));
        }
        return null;
    }

    internal static HttpHead? ParseHttpHead(string text)
    {
        var lines = text.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            int colon = l.IndexOf(':');
            if (colon <= 0) continue;
            headers[l[..colon].Trim()] = l[(colon + 1)..].Trim();
        }
        return new HttpHead(first[0], first[1], headers);
    }

    private static Task WriteAsciiAsync(Stream s, string text, CancellationToken ct) => s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    /// <summary>Lower-case hex HMAC-SHA256 of <paramref name="text"/> keyed with the UTF-8 bytes of the token (the extension computes the same).</summary>
    public static string Proof(string token, string text) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        var conn = Interlocked.Exchange(ref _active, null);
        if (conn is not null)
        {
            try { await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Mediator is closing", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is WebSocketException or IOException or TimeoutException or ObjectDisposedException) { }
            conn.Abort("Mediator is closing");
        }
        _stop.Dispose();
    }

    /// <summary>One WebSocket: framed send/receive of JSON messages and the requests waiting for an answer.</summary>
    private sealed class Connection(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);
        public WebSocket Socket { get; } = socket;
        public ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> Pending { get; } = new();
        public string? Version { get; set; }

        public async Task SendAsync(JsonNode msg, CancellationToken ct)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (bytes.Length == 0) { await Socket.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); return; }
                for (int off = 0; off < bytes.Length; off += FrameBytes)
                {
                    int len = Math.Min(FrameBytes, bytes.Length - off);
                    await Socket.SendAsync(bytes.AsMemory(off, len), WebSocketMessageType.Text, off + len >= bytes.Length, ct).ConfigureAwait(false);
                }
            }
            finally { _send.Release(); }
        }

        /// <summary>The next whole text message as JSON (frames joined), or null when the peer closed.</summary>
        public async Task<JsonNode?> ReceiveAsync(CancellationToken ct)
        {
            var buffer = new byte[FrameBytes];
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await Socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                if (r.MessageType != WebSocketMessageType.Text) throw new WebSocketException(WebSocketError.InvalidMessageType, "binary messages are not used");
                if (ms.Length + r.Count > MaxMessageBytes)
                {
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "message too big", CancellationToken.None).ConfigureAwait(false);
                    throw new WebSocketException(WebSocketError.Faulted, $"message over {MaxMessageBytes / (1024 * 1024)} MB");
                }
                ms.Write(buffer, 0, r.Count);
                if (r.EndOfMessage) break;
            }
            return JsonNode.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length));
        }

        public void Abort(string why)
        {
            foreach (var (_, tcs) in Pending) tcs.TrySetException(new BrowserBridgeException("The Chrome extension disconnected (" + why + ")."));
            try { Socket.Abort(); } catch (ObjectDisposedException) { }
            Socket.Dispose();
        }
    }
}
