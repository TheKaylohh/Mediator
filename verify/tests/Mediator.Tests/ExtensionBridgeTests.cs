using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Mediator.Core.Browser;
using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Swx;

namespace Mediator.Tests;

/// <summary>V13: the Mediator for Chrome bridge, its pairing, the browser_* tools through the real bus, and packaging.</summary>
static class ExtensionBridgeTests
{
    const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A stand-in for background.js: same upgrade headers, same pairing exchange, answers requests with <see cref="Answer"/>.</summary>
    sealed class FakeExtension : IAsyncDisposable
    {
        public readonly ClientWebSocket Ws = new();
        public readonly ConcurrentQueue<JsonObject> Requests = new();
        public readonly ConcurrentQueue<JsonObject> Notes = new();
        public Func<string, JsonObject, JsonNode?> Answer = (_, _) => new JsonObject { ["ok"] = true };
        Task? _loop;

        public static async Task<FakeExtension> ConnectAsync(int port, string token = Token, string origin = BrowserExtension.Origin, bool badClientProof = false)
        {
            var f = new FakeExtension();
            f.Ws.Options.SetRequestHeader("Origin", origin);
            await f.Ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{ChromeBridgeServer.PathPrefix}"), CancellationToken.None);
            string cn = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();
            await f.SendAsync(new JsonObject { ["type"] = "hello", ["nonce"] = cn, ["version"] = "1.0.0-test" });
            var challenge = await f.ReceiveAsync() ?? throw new InvalidOperationException("no challenge");
            string sn = (string)challenge["nonce"]!;
            if ((string?)challenge["proof"] != ChromeBridgeServer.Proof(token, $"server|{cn}|{sn}")) throw new InvalidOperationException("server proof wrong");
            await f.SendAsync(new JsonObject { ["type"] = "auth", ["proof"] = ChromeBridgeServer.Proof(badClientProof ? token + "x" : token, $"client|{sn}|{cn}") });
            var ready = await f.ReceiveAsync();
            if ((string?)ready?["type"] != "ready") throw new InvalidOperationException("not admitted");
            f._loop = Task.Run(f.ServeAsync);
            return f;
        }

        async Task ServeAsync()
        {
            while (Ws.State == WebSocketState.Open)
            {
                JsonNode? msg;
                try { msg = await ReceiveAsync(); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { return; }
                if (msg is JsonObject note && note["method"] is null && note["type"] is not null) { Notes.Enqueue(note); continue; }
                if (msg is not JsonObject o || o["method"] is null) continue;
                Requests.Enqueue(o);
                var result = Answer((string)o["method"]!, (JsonObject)o["params"]!);
                var reply = result is JsonObject { } r && r["__error"] is { } err
                    ? new JsonObject { ["id"] = o["id"]!.DeepClone(), ["error"] = new JsonObject { ["message"] = err.DeepClone() } }
                    : new JsonObject { ["id"] = o["id"]!.DeepClone(), ["result"] = result?.DeepClone() };
                try { await SendAsync(reply); } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { return; }
            }
        }

        public Task SendAsync(JsonNode msg) =>
            Ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonNode?> ReceiveAsync()
        {
            var buf = new byte[16 * 1024];
            using var ms = new MemoryStream();
            while (true)
            {
                var r = await Ws.ReceiveAsync(buf, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, r.Count);
                if (r.EndOfMessage) return JsonNode.Parse(ms.ToArray());
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { Ws.Abort(); } catch (ObjectDisposedException) { }
            Ws.Dispose();
            if (_loop is not null) await Task.WhenAny(_loop, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        }
    }

    static async Task<bool> Refused(Func<Task> connect)
    {
        try { await connect().WaitAsync(TimeSpan.FromSeconds(15)); return false; }
        catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or IOException) { return true; }
    }

    public static async Task PairingAndCalls()
    {
        await using var server = new ChromeBridgeServer(Token, BrowserExtension.Origin, firstPort: 0);
        server.Start();
        Check.True(server.Port > 0 && !server.IsConnected, "listening on a loopback port, nothing connected");

        Check.True(await Refused(() => FakeExtension.ConnectAsync(server.Port, origin: "https://evil.example")), "a web page's origin is refused at the upgrade");
        Check.True(await Refused(() => FakeExtension.ConnectAsync(server.Port, origin: "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")), "another extension is refused");
        Check.True(await Refused(() => FakeExtension.ConnectAsync(server.Port, token: "f" + Token[1..])), "an extension with another secret rejects the server's proof");
        Check.True(await Refused(() => FakeExtension.ConnectAsync(server.Port, badClientProof: true)), "a client without the secret is not admitted");
        Check.True(!server.IsConnected, "none of them became the connection");

        try { await server.CallAsync("get_tabs", null, TimeSpan.FromSeconds(2)); Check.True(false, "calling with no extension must fail"); }
        catch (BrowserBridgeException ex) { Check.True(ex.Message.Contains("Set up", StringComparison.Ordinal), "not connected says how to fix it: " + ex.Message); }

        var connected = server.WaitForConnectionAsync(TimeSpan.FromSeconds(10));
        await using var ext = await FakeExtension.ConnectAsync(server.Port);
        Check.True(await connected && server.IsConnected && server.ExtensionVersion == "1.0.0-test", "the paired extension is the connection");

        string big = new('x', 3 * 1024 * 1024);
        ext.Answer = (method, p) => method switch
        {
            "read_page" => new JsonObject { ["text"] = big, ["echo"] = (string?)p["query"] },
            "click" => new JsonObject { ["__error"] = "ref_9 is no longer on the page" },
            _ => new JsonObject { ["method"] = method },
        };
        var page = await server.CallAsync("read_page", new JsonObject { ["query"] = new string('q', 200_000) }, TimeSpan.FromSeconds(20));
        Check.True(((string?)page?["text"])?.Length == big.Length && ((string?)page?["echo"])?.Length == 200_000,
            "multi-frame messages both ways (3 MB answer, 200 KB request)");
        try { await server.CallAsync("click", new JsonObject { ["ref"] = "ref_9" }, TimeSpan.FromSeconds(5)); Check.True(false, "an extension error must throw"); }
        catch (BrowserBridgeException ex) { Check.True(ex.Message.Contains("ref_9", StringComparison.Ordinal), "the extension's error reaches the caller"); }

        var many = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => server.CallAsync("m" + i, null, TimeSpan.FromSeconds(10))));
        Check.True(many.Select((r, i) => (string?)r?["method"] == "m" + i).All(x => x), "20 concurrent requests each get their own answer");

        ext.Answer = (_, _) => { Thread.Sleep(2000); return null; };
        var hanging = server.CallAsync("slow", null, TimeSpan.FromSeconds(30));
        await Task.Delay(200);
        ext.Ws.Abort();
        try { await hanging.WaitAsync(TimeSpan.FromSeconds(5)); Check.True(false, "a pending call must fail on disconnect"); }
        catch (BrowserBridgeException) { }
        await Task.Delay(200);
        Check.True(!server.IsConnected, "a dropped extension is no longer the connection");
    }

    public static void UpgradeChecks()
    {
        static ChromeBridgeServer.HttpHead Head(string origin = BrowserExtension.Origin, string host = "127.0.0.1:19845", string path = "/chrome-bridge") =>
            ChromeBridgeServer.ParseHttpHead($"GET {path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\n" +
                $"Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nOrigin: {origin}\r\n\r\n")!;
        Check.True(ChromeBridgeServer.CheckUpgrade(Head(), 19845, BrowserExtension.Origin, out var key) is null && key == "dGhlIHNhbXBsZSBub25jZQ==", "the extension's upgrade is accepted");
        Check.True(ChromeBridgeServer.CheckUpgrade(Head(host: "localhost:19845"), 19845, BrowserExtension.Origin, out _) is null, "localhost is the same host");
        Check.True(ChromeBridgeServer.CheckUpgrade(Head(host: "attacker.example:19845"), 19845, BrowserExtension.Origin, out _) is not null, "a rebinding Host is refused");
        Check.True(ChromeBridgeServer.CheckUpgrade(Head(path: "/other"), 19845, BrowserExtension.Origin, out _) is not null, "another path is refused");
        Check.True(ChromeBridgeServer.CheckUpgrade(Head(origin: "null"), 19845, BrowserExtension.Origin, out _) is not null, "a sandboxed page (Origin: null) is refused");
        Check.True(ChromeBridgeServer.CheckUpgrade(ChromeBridgeServer.ParseHttpHead("GET /chrome-bridge HTTP/1.1\r\nHost: 127.0.0.1:19845\r\n\r\n"), 19845, BrowserExtension.Origin, out _) is not null,
            "a plain GET is refused");
        Check.Equal(ChromeBridgeServer.Proof("k", "m"), ChromeBridgeServer.Proof("k", "m"), "proofs are deterministic");
        Check.True(ChromeBridgeServer.Proof("k", "server|a|b") != ChromeBridgeServer.Proof("k", "client|b|a"), "server and client proofs differ");
    }

    public static void PackagingAndPairingFiles()
    {
        string bundled = BrowserExtension.BundledDirectory() ?? "";
        Check.True(bundled.Length > 0, "the repository's chrome-extension folder is found from the test binaries");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(bundled!, "manifest.json")))!;
        Check.Equal(3, (int)manifest["manifest_version"]!, "Manifest V3");
        var perms = manifest["permissions"]!.AsArray().Select(p => (string)p!).ToList();
        Check.True(new[] { "debugger", "tabs", "activeTab", "scripting", "alarms" }.All(perms.Contains), "the permissions it needs: " + string.Join(",", perms));
        Check.True(manifest["host_permissions"]!.AsArray().Any(p => (string?)p == "<all_urls>"), "all sites");
        Check.True(manifest["web_accessible_resources"] is null, "nothing (pairing.json above all) is readable by web pages");

        // The id Chrome derives from the manifest key must be the one the bridge admits.
        byte[] key = Convert.FromBase64String((string)manifest["key"]!);
        string id = new(System.Security.Cryptography.SHA256.HashData(key).Take(16)
            .SelectMany(b => new[] { (char)('a' + (b >> 4)), (char)('a' + (b & 15)) }).ToArray());
        Check.Equal(BrowserExtension.Id, id, "the manifest key gives the extension id the bridge admits");

        string bg = File.ReadAllText(Path.Combine(bundled!, "background.js"));
        foreach (var m in new[] { "get_tabs", "read_page", "read_console", "click_coordinates", "type_text", "eval_js", "screenshot", "serializePageForAssistant", "ws://127.0.0.1:", "HEARTBEAT_MS = 20000" })
            Check.True(bg.Contains(m, StringComparison.Ordinal), "background.js has " + m);

        string data = RuntimeTests.TempDir();
        string t1 = BrowserExtension.LoadOrCreateToken(data), t2 = BrowserExtension.LoadOrCreateToken(data);
        Check.True(t1 == t2 && t1.Length == 64, "one pairing secret per user, kept");
        string install = BrowserExtension.InstallDirectory(data);
        Check.True(BrowserExtension.Install(bundled, install) && !BrowserExtension.Install(bundled, install), "installed once; an unchanged copy is left alone");
        File.WriteAllText(Path.Combine(install, "old.js"), "stale");
        BrowserExtension.WritePairing(install, 19846, t1);
        Check.True(BrowserExtension.Install(bundled, install) && !File.Exists(Path.Combine(install, "old.js")), "files no longer shipped are removed");
        var pairing = JsonNode.Parse(File.ReadAllText(Path.Combine(install, BrowserExtension.PairingFile)))!;
        Check.True((int)pairing["port"]! == 19846 && (string?)pairing["token"] == t1, "pairing.json survives an update and has port + secret");
        Check.True(!BrowserExtension.EverConnected(data), "not connected yet");
        BrowserExtension.MarkConnected(data);
        Check.True(BrowserExtension.EverConnected(data), "remembered after the first pairing");
    }

    /// <summary>V14.1: the side panel chat's messages both ways over the paired connection, and how a panel message becomes a job.</summary>
    public static async Task PanelChatMessages()
    {
        await using var server = new ChromeBridgeServer(Token, BrowserExtension.Origin, firstPort: 0);
        server.Start();
        var got = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ExtensionMessage += m => got.TrySetResult(m);
        Check.True(!await server.NotifyAsync(new JsonObject { ["type"] = "panel_event" }), "nothing connected: a notice is not sent");
        await using var ext = await FakeExtension.ConnectAsync(server.Port);
        await ext.SendAsync(new JsonObject { ["type"] = "panel_chat", ["chatId"] = "c1", ["text"] = "teach me this" });
        var msg = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check.True((string?)msg["type"] == "panel_chat" && (string?)msg["text"] == "teach me this", "a message the extension starts reaches Mediator");
        Check.True(await server.NotifyAsync(new JsonObject { ["type"] = "panel_event", ["chatId"] = "c1", ["kind"] = "done", ["text"] = "ok" }), "Mediator sends a notice");
        for (int i = 0; i < 100 && ext.Notes.IsEmpty; i++) await Task.Delay(20);
        Check.True(ext.Notes.TryDequeue(out var note) && (string?)note["kind"] == "done", "the extension receives it");

        var tab = new JsonObject { ["id"] = 42, ["url"] = "https://www.w3schools.com/quiztest/", ["title"] = "W3Schools \"Quiz\"" };
        var first = BrowserPanelChats.SpecFor("Teach me this", tab, null, [], Path.GetTempPath());
        Check.True(first.Kind == BrowserPanelChats.Kind && first.Category == TaskCategory.Browser && first.Roots.Count == 0 && first.Tools.Count == 0
            && first.UserText == "Teach me this" && first.Prompt.Contains(ClaudeInChrome.Guidance, StringComparison.Ordinal)
            && first.Prompt.Contains("tab 42", StringComparison.Ordinal) && first.Prompt.Contains("w3schools.com/quiztest", StringComparison.Ordinal)
            && first.Prompt.Contains("do not pick or submit answers", StringComparison.Ordinal), "a panel message: a Browser job about the tab beside the panel, teach mode rules");
        var session = new ContinuableSession("turn-x", AiProvider.GeminiCli, first.WorkingDirectory, "chrome-panel-1", first, DateTimeOffset.Now) { LastResponseText = "A loop repeats code." };
        var next = BrowserPanelChats.SpecFor("now quiz me", tab, session, [new JobSpecFactory.ConversationTurn("Teach me this", "A loop repeats code.")], Path.GetTempPath());
        Check.True(next.Kind == BrowserPanelChats.Kind && next.Category == TaskCategory.Browser && next.UserText == "now quiz me"
            && next.Prompt.Contains("A loop repeats code.", StringComparison.Ordinal) && next.Prompt.Contains(ClaudeInChrome.Guidance, StringComparison.Ordinal), "a follow-up carries the chat so far");
        Check.True(BrowserPanelChats.StepName("mcp__mediator__browser_click") == "click" && BrowserPanelChats.StepName("navigate") == "navigate", "step names");
    }

    /// <summary>Chrome's profiles say where the extension was loaded from; a copy outside the install folder is flagged.</summary>
    public static void WrongFolderDetected()
    {
        string userData = RuntimeTests.TempDir();
        string install = BrowserExtension.InstallDirectory(RuntimeTests.TempDir());
        string repo = Path.Combine(RuntimeTests.TempDir(), "chrome-extension");
        Check.Equal(0, BrowserExtension.LoadedFrom(Path.Combine(userData, "missing")).Count, "no Chrome: nothing loaded");

        void Profile(string name, string file, string? path)
        {
            Directory.CreateDirectory(Path.Combine(userData, name));
            var settings = new JsonObject { ["someotherextensionidaaaaaaaaaaaa"] = new JsonObject { ["path"] = @"C:\elsewhere" } };
            if (path is not null) settings[BrowserExtension.Id] = new JsonObject { ["path"] = path, ["location"] = 4 };
            File.WriteAllText(Path.Combine(userData, name, file), new JsonObject { ["extensions"] = new JsonObject { ["settings"] = settings } }.ToJsonString());
        }
        Profile("Default", "Secure Preferences", install + Path.DirectorySeparatorChar);
        Profile("Profile 1", "Preferences", null);
        Directory.CreateDirectory(Path.Combine(userData, "Profile 2"));
        File.WriteAllText(Path.Combine(userData, "Profile 2", "Secure Preferences"), "{ not json");
        var loaded = BrowserExtension.LoadedFrom(userData);
        Check.Equal(1, loaded.Count, "only the profile that has Mediator for Chrome; broken files skipped");
        Check.True(BrowserExtension.WrongFolder(install, loaded) is null, "the install folder (trailing slash, any case) is right");

        Profile("Profile 3", "Secure Preferences", repo);
        loaded = BrowserExtension.LoadedFrom(userData);
        Check.Equal(repo, BrowserExtension.WrongFolder(install, loaded), "the repository copy is flagged");
        string steps = BrowserExtension.WrongFolderSteps(repo, install);
        Check.True(steps.Contains(repo) && steps.Contains(install) && steps.Contains("chrome://extensions"), "the card names both folders and the page to type");
        Check.True(BrowserExtension.SetupSteps(install).Contains("type chrome://extensions"), "Set up tells the user to type the page (Chrome ignores it from apps)");
    }

    public static async Task ToolsThroughTheBus(string bridgeDll)
    {
        await using var server = new ChromeBridgeServer(Token, BrowserExtension.Origin, firstPort: 0);
        server.Start();
        await using var ext = await FakeExtension.ConnectAsync(server.Port);
        int clicks = 0;
        ext.Answer = (method, p) => method switch
        {
            "read_page" => new JsonObject { ["text"] = "Page: Quiz\n[ref_1] radio \"4\" (unchecked) @(10,20)\n[ref_2] button \"Next\" @(30,40)" },
            "click" => new JsonObject { ["clicked"] = (string?)p["ref"], ["n"] = Interlocked.Increment(ref clicks) },
            "screenshot" => new JsonObject { ["mediaType"] = "image/jpeg", ["data"] = Convert.ToBase64String([0xFF, 0xD8, 0xFF]), ["width"] = 800, ["height"] = 600, ["tabId"] = 7 },
            _ => new JsonObject(),
        };

        await using var runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(RuntimeTests.TempDir()),
            Bus = new BusOptions { PipeName = "mediator-ext-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            SwxBackend = new SimulatedSwxBackend(),
            BrowserBridge = server,
        });
        var prompt = new CountingPrompt();
        runtime.Approvals.Target = prompt;
        var spec = JobSpecFactory.Assistant(null, "open the quiz in chrome", LaunchMode.None, BrowserToolProvider.JobTools, TaskCategory.Browser) with
        {
            ToolModes = BrowserToolProvider.AutoInBrowserJobs.ToDictionary(t => t, _ => ApprovalMode.Auto),
        };
        Check.True(JobSpecFactory.UsesExtension(spec) && !JobSpecFactory.UsesClaudeInChrome(spec), "a job with the browser tools uses the extension, not --chrome");
        Check.True(MediatorRuntime.NoLocalAccess(spec), "browser tools alone still mean no local file tools for the CLI");
        var job = await runtime.StartJobAsync(spec);
        await using var client = await runtime.ConnectClientAsync(job);

        var tools = await client.ListToolsAsync();
        Check.True(BrowserToolProvider.JobTools.All(t => tools.Contains(t)) && !tools.Contains(BrowserToolProvider.EvalTool), "the job sees the browser tools but not browser_eval");
        var read = await client.CallToolAsync(BrowserToolProvider.ReadTool, new JsonObject { ["query"] = "radio" }, TimeSpan.FromSeconds(20));
        Check.True(!read.IsError && read.Text.Contains("[ref_1] radio \"4\"", StringComparison.Ordinal), "browser_read returns the compact page text: " + read.Text);
        var c1 = await client.CallToolAsync(BrowserToolProvider.ClickTool, new JsonObject { ["ref"] = "ref_2" }, TimeSpan.FromSeconds(20));
        var c2 = await client.CallToolAsync(BrowserToolProvider.ClickTool, new JsonObject { ["ref"] = "ref_2" }, TimeSpan.FromSeconds(20));
        Check.True(!c1.IsError && !c2.IsError && clicks == 2, "the same click twice runs twice (Repeatable, not de-duplicated): " + c2.Text);
        Check.Equal(0, prompt.Asked, "clicks in a Browser job do not ask");
        var shot = await client.CallToolAsync(BrowserToolProvider.ScreenshotTool, new JsonObject(), TimeSpan.FromSeconds(20));
        Check.True(!shot.IsError && shot.Text.Contains("800x600", StringComparison.Ordinal), "a screenshot comes back (as an image): " + shot.Text);
        var audit = await runtime.Audit.ListAsync(job.JobId);
        Check.True(audit.Count(r => r.Tool == BrowserToolProvider.ClickTool && r.Outcome == "ok") >= 2, "every click is audited");

        var other = await runtime.StartJobAsync(JobSpecFactory.Assistant(null, "click it", LaunchMode.None, [BrowserToolProvider.ClickTool, BrowserToolProvider.EvalTool], TaskCategory.Chat));
        await using var client2 = await runtime.ConnectClientAsync(other);
        await client2.CallToolAsync(BrowserToolProvider.ClickTool, new JsonObject { ["ref"] = "ref_2" }, TimeSpan.FromSeconds(20));
        await client2.CallToolAsync(BrowserToolProvider.EvalTool, new JsonObject { ["expression"] = "document.title" }, TimeSpan.FromSeconds(20));
        Check.Equal(2, prompt.Asked, "outside a Browser job a click asks, and browser_eval always asks");

        var noBridge = new BrowserToolProvider(null);
        var r = await noBridge.ExecuteAsync(new ToolCallContext(job.Grant, noBridge.Tools[0], JsonDocumentRoot("{}"), "c"), CancellationToken.None);
        Check.True(r.IsError && r.Text.Contains("overlay", StringComparison.Ordinal), "mediator-cli (no bridge) explains the tools need the app");
        Check.True(BrowserToolProvider.Format(BrowserToolProvider.ScreenshotTool, new JsonObject { ["data"] = Convert.ToBase64String([1, 2, 3]), ["mediaType"] = "image/jpeg" }).Images.Count == 1,
            "screenshots are MCP image content");
    }

    static System.Text.Json.JsonElement JsonDocumentRoot(string json) => System.Text.Json.JsonDocument.Parse(json).RootElement;

    sealed class CountingPrompt : IApprovalPrompt
    {
        public int Asked;
        public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            return Task.FromResult(ApprovalResponse.ApproveAll);
        }
    }
}
