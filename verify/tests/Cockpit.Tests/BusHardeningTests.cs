using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Protocol;

namespace Cockpit.Tests;

/// <summary>V6 Phase 3: hostile frames, limits, revocation races and concurrency against the real bus over a real pipe.</summary>
static class BusHardeningTests
{
    sealed class HardTools : IToolProvider
    {
        public int Writes;
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            new("echo", "echo", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""")!, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(30)),
            new("slow", "slow", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"ms":{"type":"integer"}}}""")!, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(30)),
            new("write_it", "write", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"n":{"type":"integer"}}}""")!, ToolRisk.Write, null, TimeSpan.FromSeconds(30)),
        ];
        public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult<JsonNode?>(new JsonObject { ["kind"] = "t" });
        public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
        {
            switch (call.Tool.Name)
            {
                case "slow": await Task.Delay(call.Arguments.GetProperty("ms").GetInt32(), ct); return ToolResult.Ok("slept");
                case "write_it": Interlocked.Increment(ref Writes); return ToolResult.Ok("wrote");
                default: return ToolResult.Ok(call.Job.JobId + ":" + call.Arguments.GetProperty("text").GetString());
            }
        }
    }

    /// <summary>Holds every approval until released; records how many it saw.</summary>
    sealed class HeldPrompt : IApprovalPrompt
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Asked;
        public async Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            await Release.Task; // deliberately ignores ct: models an approval that arrives after the job was revoked
            return ApprovalResponse.ApproveAll;
        }
    }

    sealed class Rig : IAsyncDisposable
    {
        public required McpBusServer Bus { get; init; }
        public required JobRegistry Jobs { get; init; }
        public required AuditLog Audit { get; init; }
        public required HardTools Tools { get; init; }
        public required string Pipe { get; init; }
        public async ValueTask DisposeAsync() { await Bus.DisposeAsync(); await Audit.DisposeAsync(); }
    }

    static async Task<Rig> Start(IApprovalPrompt prompt, int maxInFlight = 32)
    {
        string pipe = "cockpit-hard-" + Guid.NewGuid().ToString("N")[..10];
        var jobs = new JobRegistry();
        var registry = new ToolRegistry();
        var tools = new HardTools();
        registry.Register(tools);
        string dir = Path.Combine(Path.GetTempPath(), "cockpit-hard", Guid.NewGuid().ToString("N")[..8]);
        var audit = await AuditLog.OpenAsync(Path.Combine(dir, "audit.db"));
        var bus = new McpBusServer(new BusOptions { PipeName = pipe, MaxInFlightPerConnection = maxInFlight }, jobs, registry, new PermissionGate(prompt), new LeaseManager(), audit);
        bus.Start();
        return new Rig { Bus = bus, Jobs = jobs, Audit = audit, Tools = tools, Pipe = pipe };
    }

    /// <summary>A raw bus client: pipe, hello, then frames. Responses are read on a separate loop and matched by id.</summary>
    sealed class Raw : IAsyncDisposable
    {
        public NamedPipeClientStream Pipe { get; }
        private readonly FrameWriter _writer;
        private readonly Dictionary<string, Queue<TaskCompletionSource<JsonNode>>> _waiting = new(); // FIFO per id: a duplicate id has two waiters
        public readonly List<JsonNode> Unmatched = new();
        private readonly Task _reader;
        public bool Closed { get; private set; }

        private Raw(NamedPipeClientStream pipe)
        {
            Pipe = pipe;
            _writer = new FrameWriter(pipe);
            _reader = Task.Run(ReadLoop);
        }

        public static async Task<NamedPipeClientStream> ConnectAsync(string name)
        {
            var p = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await p.ConnectAsync(5000);
            return p;
        }

        public static async Task<Raw> OpenAsync(string name, JobGrant grant)
        {
            var p = await ConnectAsync(name);
            var w = new FrameWriter(p);
            await w.WriteAsync(new BusHello(BusHello.CurrentProtocolVersion, grant.JobId, grant.Token, Environment.ProcessId, "pipe").ToJson(), CancellationToken.None);
            var welcome = BusWelcome.Parse((await FrameIO.ReadFrameAsync(p, CancellationToken.None))!);
            if (!welcome.Ok) throw new Exception("hello rejected: " + welcome.Error);
            return new Raw(p);
        }

        private async Task ReadLoop()
        {
            try
            {
                while (true)
                {
                    var f = await FrameIO.ReadFrameAsync(Pipe, CancellationToken.None);
                    if (f is null) break;
                    var node = JsonNode.Parse(f)!;
                    string key = node["id"]?.ToJsonString() ?? "null";
                    lock (_waiting)
                    {
                        if (_waiting.TryGetValue(key, out var q) && q.Count > 0) q.Dequeue().TrySetResult(node);
                        else Unmatched.Add(node);
                    }
                }
            }
            catch (Exception) { }
            Closed = true;
            lock (_waiting) foreach (var t in _waiting.Values.SelectMany(q => q)) t.TrySetException(new EndOfStreamException("closed"));
        }

        public Task<JsonNode> Expect(JsonNode? id)
        {
            var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiting)
            {
                string key = id?.ToJsonString() ?? "null";
                var early = Unmatched.FirstOrDefault(u => u["id"]?.ToJsonString() == key);
                if (early is not null) { Unmatched.Remove(early); tcs.SetResult(early); }
                else { if (!_waiting.TryGetValue(key, out var q)) _waiting[key] = q = new(); q.Enqueue(tcs); }
            }
            return tcs.Task;
        }

        public ValueTask SendBytes(byte[] payload) => _writer.WriteAsync(payload, CancellationToken.None);
        public ValueTask Send(JsonObject msg) => _writer.WriteAsync(msg.ToJsonString(), CancellationToken.None);

        public async Task<JsonNode> Call(JsonNode id, string tool, JsonObject args, int timeoutMs = 20_000)
        {
            var wait = Expect(id);
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args } });
            return await wait.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }

        public async ValueTask DisposeAsync()
        {
            await Pipe.DisposeAsync();
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        }
    }

    static async Task<bool> ClosedWithin(Stream s, int ms)
    {
        var buf = new byte[1];
        try { return await s.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromMilliseconds(ms)) == 0; }
        catch (TimeoutException) { return false; }
        catch (IOException) { return true; }
    }

    static byte[] Header(int length)
    {
        var h = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(h, length);
        return h;
    }

    /// <summary>Hostile frames before and after the handshake: the bus drops or answers each, never allocates for a lie, and keeps serving.</summary>
    public static async Task HostileFrames()
    {
        await using var rig = await Start(new HeldPrompt());
        var grant = rig.Jobs.Issue("hostile", ["echo", "slow"], [], TimeSpan.FromMinutes(5));

        // Pre-auth: a header claiming 10 MiB (over the 8 KiB hello limit) is dropped at once.
        await using (var p = await Raw.ConnectAsync(rig.Pipe))
        {
            await p.WriteAsync(Header(10 * 1024 * 1024));
            await p.FlushAsync();
            Check.True(await ClosedWithin(p, 3000), "an oversized hello header closes the connection without reading a payload");
        }
        // Pre-auth: negative length, and a partial frame followed by disconnect.
        await using (var p = await Raw.ConnectAsync(rig.Pipe))
        {
            await p.WriteAsync(Header(-5));
            await p.FlushAsync();
            Check.True(await ClosedWithin(p, 3000), "a negative length closes the connection");
        }
        await using (var p = await Raw.ConnectAsync(rig.Pipe))
        {
            await p.WriteAsync(Header(200));
            await p.WriteAsync(new byte[50]);
            await p.FlushAsync();
        } // disconnect mid-frame

        await using var c = await Raw.OpenAsync(rig.Pipe, grant);
        // Invalid UTF-8 and malformed JSON → -32700, connection stays up.
        var parseErr = c.Expect(null);
        await c.SendBytes([0x7B, 0x22, 0xC3, 0x28, 0x22, 0x3A, 0x31, 0x7D]); // {"\xC3(":1}
        Check.Equal(-32700, (int)(await parseErr.WaitAsync(TimeSpan.FromSeconds(5)))["error"]!["code"]!, "invalid UTF-8 → parse error");
        // Invalid UTF-8 inside the method name (before V6 this parsed, then threw on the read loop and dropped the connection).
        var badMethod = c.Expect(null);
        var bytes = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":5,"method":"tools/XX","params":{}}""");
        int at = Array.IndexOf(bytes, (byte)'X');
        bytes[at] = 0xC3; bytes[at + 1] = 0x28;
        await c.SendBytes(bytes);
        Check.Equal(-32700, (int)(await badMethod.WaitAsync(TimeSpan.FromSeconds(5)))["error"]!["code"]!, "invalid UTF-8 in a string value → parse error, connection kept");
        // Not a JSON-RPC object.
        var invalid = c.Expect(null);
        await c.SendBytes(Encoding.UTF8.GetBytes("[1,2,3]"));
        Check.Equal(-32600, (int)(await invalid.WaitAsync(TimeSpan.FromSeconds(5)))["error"]!["code"]!, "a JSON array → invalid request");
        // Ids are matched by value: "é" and "é" are the same id, so the second is a duplicate while the first runs.
        var first = c.Expect(JsonValue.Create("é")!);
        await c.SendBytes(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":"é","method":"tools/call","params":{"name":"slow","arguments":{"ms":800}}}"""));
        await Task.Delay(100);
        var dupWait = c.Expect(JsonValue.Create("é")!);
        await c.SendBytes(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":"é","method":"tools/call","params":{"name":"slow","arguments":{"ms":1}}}"""));
        // Responses for one id arrive in order: the refusal of the duplicate first, then the original's result.
        var answers = new[] { await first.WaitAsync(TimeSpan.FromSeconds(5)), await dupWait.WaitAsync(TimeSpan.FromSeconds(5)) };
        Check.True(answers.Count(a => a["error"]?["message"]?.ToString().Contains("Duplicate request id", StringComparison.Ordinal) == true) == 1
            && answers.Count(a => a["result"] is not null) == 1, "same id by value while in flight → exactly one refusal, and the original still completes");
        // Post-auth oversized frame (over 16 MiB) closes this connection only.
        var ok = await c.Call(JsonValue.Create(1)!, "echo", new JsonObject { ["text"] = "still alive" });
        Check.True(ok["result"]!["content"]![0]!["text"]!.ToString() == "hostile:still alive", "the connection still works after the malformed frames");
        await c.Pipe.WriteAsync(Header(FrameIO.MaxFrameBytes + 1));
        await c.Pipe.FlushAsync();
        var sw = Stopwatch.StartNew();
        while (!c.Closed && sw.ElapsedMilliseconds < 3000) await Task.Delay(20);
        Check.True(c.Closed, "a frame over 16 MiB closes the connection");

        // The bus still serves a fresh client.
        await using var again = await Raw.OpenAsync(rig.Pipe, grant);
        var r = await again.Call(JsonValue.Create(2)!, "echo", new JsonObject { ["text"] = "fresh" });
        Check.True(r["result"] is not null, "the bus survives all of it");
    }

    /// <summary>The per-connection in-flight cap answers excess requests with an error instead of queueing them without bound.</summary>
    public static async Task InFlightCap()
    {
        await using var rig = await Start(new HeldPrompt(), maxInFlight: 8);
        var grant = rig.Jobs.Issue("cap", ["slow"], [], TimeSpan.FromMinutes(5));
        await using var c = await Raw.OpenAsync(rig.Pipe, grant);
        var calls = Enumerable.Range(1, 12).Select(i => c.Call(JsonValue.Create(i)!, "slow", new JsonObject { ["ms"] = 600 })).ToList();
        var results = await Task.WhenAll(calls);
        int refused = results.Count(x => x["error"]?["message"]?.ToString().Contains("Too many requests in progress", StringComparison.Ordinal) == true);
        int done = results.Count(x => x["result"] is not null);
        Check.True(refused == 4 && done == 8, $"8 run, 4 refused with a clear error (got {done} run, {refused} refused)");
        var later = await c.Call(JsonValue.Create(99)!, "slow", new JsonObject { ["ms"] = 1 });
        Check.True(later["result"] is not null, "capacity returns when calls finish");
    }

    /// <summary>A grant revoked while its Write call waits for approval never executes, even if approval comes after.</summary>
    public static async Task RevokedWhileAwaitingApproval()
    {
        var prompt = new HeldPrompt();
        await using var rig = await Start(prompt);
        var grant = rig.Jobs.Issue("revoke-race", ["write_it"], [], TimeSpan.FromMinutes(5));
        await using var c = await Raw.OpenAsync(rig.Pipe, grant);
        var call = c.Call(JsonValue.Create(1)!, "write_it", new JsonObject { ["n"] = 1 }, 10_000);
        var sw = Stopwatch.StartNew();
        while (prompt.Asked == 0 && sw.ElapsedMilliseconds < 5000) await Task.Delay(10);
        rig.Jobs.Revoke("revoke-race");
        prompt.Release.SetResult(); // the approval "arrives" after revocation
        try { await call; } catch (Exception) { /* the connection is closed by the revocation */ }
        await Task.Delay(300);
        Check.Equal(0, rig.Tools.Writes, "the write never ran");
        var ops = await rig.Bus.Operations.ListAsync("revoke-race");
        Check.True(ops.Single().State is OperationState.Cancelled, "operation recorded as not run: " + ops.Single().State);
        var audit = await rig.Audit.ListAsync("revoke-race");
        Check.True(audit.Any(a => a.Tool == "write_it" && a.Outcome == "not_run"), "audited as not run");
    }

    /// <summary>Several jobs, many concurrent calls, a revocation in the middle: every response reaches the right id and job; no deadlock.</summary>
    public static async Task ConcurrencyStress()
    {
        await using var rig = await Start(new HeldPrompt(), maxInFlight: 64);
        var grants = Enumerable.Range(0, 4).Select(i => rig.Jobs.Issue("stress-" + i, ["echo", "slow"], [], TimeSpan.FromMinutes(5))).ToList();
        var clients = new List<Raw>();
        foreach (var g in grants) clients.Add(await Raw.OpenAsync(rig.Pipe, g));
        try
        {
            var sw = Stopwatch.StartNew();
            var all = new List<Task<(int Job, int I, JsonNode? R, bool Threw)>>();
            for (int j = 0; j < clients.Count; j++)
                for (int i = 0; i < 40; i++)
                {
                    int jj = j, ii = i;
                    all.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var r = ii % 5 == 0
                                ? await clients[jj].Call(JsonValue.Create($"s{ii}")!, "slow", new JsonObject { ["ms"] = 50 + ii })
                                : await clients[jj].Call(JsonValue.Create(ii)!, "echo", new JsonObject { ["text"] = $"{jj}-{ii}" });
                            return (jj, ii, (JsonNode?)r, false);
                        }
                        catch (Exception) { return (jj, ii, (JsonNode?)null, true); }
                    }));
                    if (j == 3 && i == 20) rig.Jobs.Revoke("stress-3");
                }
            var results = await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(60));
            foreach (var (job, i, r, threw) in results.Where(x => x.Job < 3))
            {
                Check.True(!threw && r is not null, $"job {job} call {i} answered");
                if (i % 5 != 0) Check.Equal($"stress-{job}:{job}-{i}", r!["result"]!["content"]![0]!["text"]!.ToString(), "response matched to its id and job");
            }
            int revokedAnswered = results.Count(x => x.Job == 3 && !x.Threw && x.R?["result"] is not null);
            Check.True(revokedAnswered <= 21, $"the revoked job got no answers for calls after revocation ({revokedAnswered} answered)");
            Check.True(sw.ElapsedMilliseconds < 60_000, "no deadlock");
            Console.WriteLine($"      160 calls on 4 connections with a mid-run revocation: {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            foreach (var c in clients) await c.DisposeAsync();
        }
    }
}
