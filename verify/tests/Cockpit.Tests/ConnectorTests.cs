using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Connectors;
using Cockpit.Core.Runtime;
using Cockpit.Core.Tools;

namespace Cockpit.Tests;

/// <summary>V7: custom MCP servers (registry, health ping, gate) and the connector probe.</summary>
static class ConnectorTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-conn", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// A stdio MCP server for tests. Tools: shout(text) → upper case + a PNG image; env(names) → which of the named
    /// environment variables it can see; touch(path) → creates a file (to prove a denied call never ran).
    /// Modes: "die" exits at once; "hang" never answers.
    /// </summary>
    public static async Task<int> FakeMcpServerAsync(string[] args)
    {
        string mode = args.FirstOrDefault() ?? "";
        if (mode == "die") return 3;
        var stdout = Console.OpenStandardOutput();
        async Task Send(JsonObject o)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(o.ToJsonString() + "\n");
            await stdout.WriteAsync(bytes);
            await stdout.FlushAsync();
        }
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (mode == "hang") continue;
            JsonNode? msg;
            try { msg = JsonNode.Parse(line); } catch (JsonException) { continue; }
            if (msg?["id"] is not JsonValue id) continue; // notification
            string method = (string?)msg["method"] ?? "";
            JsonNode result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake-mcp", ["version"] = "1.0" },
                },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(
                        new JsonObject { ["name"] = "shout", ["description"] = "Upper-cases text.", ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""") },
                        new JsonObject { ["name"] = "env", ["description"] = "Reports variables.", ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"names":{"type":"array"}}}""") },
                        new JsonObject { ["name"] = "touch", ["description"] = "Creates a file.", ["inputSchema"] = JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""") },
                        new JsonObject { ["name"] = "bad name!", ["description"] = "Sanitized." }),
                },
                "tools/call" => Call(msg["params"]!),
                _ => new JsonObject(),
            };
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });
        }
        return 0;

        static JsonObject Call(JsonNode p)
        {
            string name = (string?)p["name"] ?? "";
            var a = p["arguments"] as JsonObject ?? [];
            JsonObject Text(string t, params JsonObject[] more) => new() { ["content"] = new JsonArray([new JsonObject { ["type"] = "text", ["text"] = t }, .. more]), ["isError"] = false };
            switch (name)
            {
                case "shout":
                    return Text(((string?)a["text"] ?? "").ToUpperInvariant(),
                        new JsonObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = Convert.ToBase64String(AttachmentTests.TinyPng) });
                case "env":
                    var seen = (a["names"] as JsonArray ?? []).Select(n => (string?)n ?? "").Where(n => Environment.GetEnvironmentVariable(n) is not null);
                    return Text("seen: " + string.Join(",", seen) + "; cwd: " + Environment.CurrentDirectory);
                case "touch":
                    File.WriteAllText((string?)a["path"] ?? "touched", "ran");
                    return Text("touched");
                default:
                    return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "unknown tool" }), ["isError"] = true };
            }
        }
    }

    static (string Command, string[] Args) Fake(string testsDll, string mode = "") =>
        (ComponentLocator.DotnetHost() is var host && Path.IsPathFullyQualified(host) ? host : CliLaunchBuilderResolve(host),
         mode.Length == 0 ? [testsDll, "--fake-mcp"] : [testsDll, "--fake-mcp", mode]);

    static string CliLaunchBuilderResolve(string name) => Cockpit.Core.Jobs.CliLaunchBuilder.ResolveOnPath(name) ?? throw new SkipException($"{name} not on PATH");

    public static async Task CustomMcpRegistry(string testsDll)
    {
        string data = TempDir("mcp");
        string file = Path.Combine(data, "cockpit.mcp.json");
        var tools = new ToolRegistry();
        var svc = new CustomMcpService(file, tools);
        var (cmd, args) = Fake(testsDll);

        // Validation before anything starts.
        async Task Refused(Func<Task> add, string contains, string what)
        {
            try { await add(); throw new Exception("Assertion failed: " + what); }
            catch (CustomMcpException ex) { Check.True(ex.Message.Contains(contains, StringComparison.Ordinal), $"{what}: '{ex.Message}' mentions '{contains}'"); }
        }
        await Refused(() => svc.AddAsync("Bad Name", cmd, args), "Name", "upper case / space in the name refused");
        await Refused(() => svc.AddAsync("ok", "definitely-not-a-command-xyz", []), "not found on PATH", "unknown command refused");
        await Refused(() => svc.AddAsync("ok", "/no/such/server", []), "does not exist", "missing full path refused");
        await Refused(() => svc.AddAsync("ok", "./relative/server", []), "full path", "relative path refused");
        Check.True(!File.Exists(file), "nothing saved for refused servers");

        // Healthy server: tools registered under ext_<server>_<tool>, all Destructive.
        var clock = Stopwatch.StartNew();
        var s = await svc.AddAsync("fake", cmd, args);
        Check.True(s.Enabled && s.LastPing is { Ok: true }, $"health ping ok ({s.LastPing?.Error})");
        Check.True(clock.Elapsed < CustomMcpService.PingTimeout, "ping well within 15 s");
        Check.Equal(4, s.LastPing!.Tools.Count, "four tools listed");
        foreach (var n in new[] { "ext_fake_shout", "ext_fake_env", "ext_fake_touch", "ext_fake_bad_name_" })
        {
            Check.True(tools.TryGet(n, out _, out var d), $"{n} registered");
            Check.Equal(ToolRisk.Destructive, d.Risk, $"{n} is Destructive");
            Check.True(d.Description.Contains("approval", StringComparison.Ordinal), "description tells the model approval is needed");
        }
        Check.True(svc.EnabledToolNames.Count == 4, "enabled tool names");
        await Refused(() => svc.AddAsync("fake", cmd, args), "already exists", "duplicate name refused");

        // The file: command and args only, never environment variables.
        var json = JsonNode.Parse(File.ReadAllText(file))!;
        var entry = json["servers"]![0]!.AsObject();
        Check.True(entry.ContainsKey("command") && entry.ContainsKey("args") && !entry.ContainsKey("env"), "no env in cockpit.mcp.json");
        Check.True(!Directory.EnumerateFiles(data).Any(f => f.EndsWith(".tmp", StringComparison.Ordinal)), "atomic write left no temp file");

        // Reload from disk registers the same tools without starting anything.
        var tools2 = new ToolRegistry();
        var svc2 = new CustomMcpService(file, tools2);
        Check.True(svc2.LoadError is null && tools2.TryGet("ext_fake_shout", out _, out _), "reloaded from cockpit.mcp.json");

        // Disable → unregistered; enable → back; remove → gone and saved.
        svc.SetEnabled("fake", false);
        Check.True(!tools.TryGet("ext_fake_shout", out _, out _) && svc.EnabledToolNames.Count == 0, "disabled server's tools unregistered");
        svc.SetEnabled("fake", true);
        Check.True(tools.TryGet("ext_fake_shout", out _, out _), "re-enabled");
        svc.Remove("fake");
        Check.True(!tools.TryGet("ext_fake_shout", out _, out _) && svc.Servers.Count == 0, "removed");
        Check.Equal(0, JsonNode.Parse(File.ReadAllText(file))!["servers"]!.AsArray().Count, "removal saved");

        // A server that dies, and one that never answers: saved disabled, with the reason; nothing registered.
        var (dc, da) = Fake(testsDll, "die");
        var dead = await svc.AddAsync("dead", dc, da);
        Check.True(!dead.Enabled && dead.LastPing is { Ok: false } && !string.IsNullOrEmpty(dead.LastPing.Error), $"dead server reported ({dead.LastPing?.Error})");
        var (hc, ha) = Fake(testsDll, "hang");
        clock.Restart();
        var hung = await svc.AddAsync("hung", hc, ha);
        Check.True(!hung.Enabled && hung.LastPing!.Error!.Contains("15 s", StringComparison.Ordinal), $"hung server times out ({hung.LastPing?.Error})");
        Check.True(clock.Elapsed < TimeSpan.FromSeconds(25), "the ping gives up after about 15 s");
        Check.True(tools.VisibleTo(new JobGrant
        {
            JobId = "j", Token = "t", AllowedTools = new HashSet<string>(svc.EnabledToolNames), FileRoots = [], ExpiresAt = DateTimeOffset.MaxValue,
            Paths = new Cockpit.Core.Security.PathAuthorizer([data]),
        }).Count() == 0, "no tools from unhealthy servers");
        Check.True(!Directory.Exists(Path.Combine(data, "mcp-runs")) || !Directory.EnumerateDirectories(Path.Combine(data, "mcp-runs")).Any(), "run folders cleaned up");

        // A malformed file is reported and left alone.
        string badFile = Path.Combine(TempDir("bad"), "cockpit.mcp.json");
        File.WriteAllText(badFile, "{ not json");
        var badSvc = new CustomMcpService(badFile, new ToolRegistry());
        Check.True(badSvc.LoadError is not null, "malformed file reported");
        await Refused(() => badSvc.AddAsync("x", cmd, args), "could not be read", "no add over a malformed file");
        Check.Equal("{ not json", File.ReadAllText(badFile), "malformed file not overwritten");
    }

    sealed class RecordingPrompt(Func<ApprovalRequest, bool> approve) : IApprovalPrompt
    {
        public readonly List<ApprovalRequest> Requests = new();
        public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(approve(request) ? ApprovalResponse.ApproveAll : ApprovalResponse.Deny);
        }
    }

    public static async Task CustomMcpThroughGate(string bridgeDll, string testsDll)
    {
        await using var rt = await OperationTests.Runtime(bridgeDll);
        var prompt = new RecordingPrompt(r => r.Tool != "ext_fake_touch");
        rt.Approvals.Target = prompt;
        var (cmd, args) = Fake(testsDll);
        var server = await rt.CustomMcp.AddAsync("fake", cmd, args);
        Check.True(server.LastPing is { Ok: true }, "registered");

        string work = TempDir("gate-work");
        string marker = Path.Combine(work, "touched.txt");
        Environment.SetEnvironmentVariable("COCKPIT_TEST_SERVICE_TOKEN", "tok-should-not-leak");
        Environment.SetEnvironmentVariable("COCKPIT_HARMLESS", null);
        Environment.SetEnvironmentVariable("HARMLESS_SETTING_V7", "1");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test-not-real");
        try
        {
            // The grant asks for Auto on every custom tool; Destructive cannot be auto-approved.
            var modes = rt.CustomMcp.EnabledToolNames.ToDictionary(n => n, _ => ApprovalMode.Auto);
            var job = await rt.StartJobAsync(OperationTests.Spec("mcp-1", work) with { Tools = [], ToolModes = modes });
            Check.True(job.Grant.AllowedTools.Contains("ext_fake_shout"), "enabled custom tools are on new jobs' allow-lists");
            await using var client = await rt.ConnectClientAsync(job);

            var shout = await client.CallToolAsync("ext_fake_shout", new JsonObject { ["text"] = "hello" }, TimeSpan.FromSeconds(60));
            Check.True(!shout.IsError && shout.Text == "HELLO", $"proxied call ran ({shout.Text})");
            Check.True(shout.Images.Count == 1 && shout.Images[0].Data.AsSpan().SequenceEqual(AttachmentTests.TinyPng), "image content passed through");
            var asked = prompt.Requests.Single(r => r.Tool == "ext_fake_shout");
            Check.Equal(ToolRisk.Destructive, asked.Risk, "asked as Destructive although the grant said Auto");
            Check.True(asked.Preview?["summary"]?.ToString().Contains("outside the Cockpit's file rules", StringComparison.Ordinal) == true, "preview says it runs outside the file rules");

            var env = await client.CallToolAsync("ext_fake_env", new JsonObject
            {
                ["names"] = new JsonArray("COCKPIT_TEST_SERVICE_TOKEN", "ANTHROPIC_API_KEY", "HARMLESS_SETTING_V7"),
            }, TimeSpan.FromSeconds(60));
            Check.True(env.Text.StartsWith("seen: HARMLESS_SETTING_V7;", StringComparison.Ordinal), $"token and API key scrubbed, ordinary variable kept ({env.Text})");
            Check.True(env.Text.Contains("mcp-runs", StringComparison.Ordinal), "runs in its own empty folder");

            var denied = await client.CallToolAsync("ext_fake_touch", new JsonObject { ["path"] = marker }, TimeSpan.FromSeconds(60));
            Check.True(denied.IsError && !File.Exists(marker), "a denied call never starts the server");

            var audit = await rt.Audit.ListAsync("mcp-1");
            Check.True(audit.Any(r => r.Tool == "ext_fake_shout" && r.Risk == "Destructive" && r.Outcome == "ok"), "approved call audited");
            Check.True(audit.Any(r => r.Tool == "ext_fake_touch" && r.Decision == "denied"), "denied call audited");
            Check.True(!(await rt.Audit.ListAsync("mcp-1")).Any(r => r.ArgumentsJson.Contains("tok-should-not-leak", StringComparison.Ordinal)), "no secret in the audit");

            // Removing the server takes the tools away from the running job too.
            rt.CustomMcp.Remove("fake");
            var gone = await client.CallToolAsync("ext_fake_shout", new JsonObject { ["text"] = "x" }, TimeSpan.FromSeconds(30)).ContinueWith(t => t.Exception is null ? (McpToolCallResult?)t.Result : null);
            Check.True(gone is null || gone.IsError, "a removed server's tool no longer runs");
        }
        finally
        {
            Environment.SetEnvironmentVariable("COCKPIT_TEST_SERVICE_TOKEN", null);
            Environment.SetEnvironmentVariable("HARMLESS_SETTING_V7", null);
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    public static async Task ProbeOnLinux()
    {
        if (!OperatingSystem.IsLinux()) throw new SkipException("uses shell scripts as stand-in CLIs");
        string home = TempDir("home");
        string bin = TempDir("bin");
        string Script(string name, string body)
        {
            string p = Path.Combine(bin, name);
            File.WriteAllText(p, "#!/bin/sh\n" + body + "\n");
            File.SetUnixFileMode(p, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return p;
        }
        var probe = new ConnectorProbe(DefaultProcessRunner.Instance, home, new Dictionary<string, string?>
        {
            ["claude"] = Script("claude", "echo '2.1.309 (Claude Code)'"),
            ["agy"] = Script("agy", "echo 'oops' >&2; exit 2"),
            ["python"] = Script("python", "echo 'Python 3.13.17'"),
            ["git"] = null,
            ["blender"] = null,
        });
        var claude = await probe.ProbeToolAsync("claude");
        Check.True(claude.Found && claude.Version == "2.1.309", $"claude version parsed ({claude.Version})");
        var gemini = await probe.ProbeToolAsync("agy");
        Check.True(!gemini.Found && gemini.Problem!.Contains("exited with 2", StringComparison.Ordinal), "failing --version reported");
        Check.Equal("3.13.17", (await probe.ProbeToolAsync("python")).Version, "python version");
        Check.True(!(await probe.ProbeToolAsync("git")).Found, "missing git reported");

        Check.Equal(CredentialState.NotFound, probe.Credentials("claude"), "no credential file");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(probe.CredentialFile("claude"), "{\"secret\":\"never-read\"}");
        File.SetUnixFileMode(probe.CredentialFile("claude"), UnixFileMode.None); // unreadable: existence is all the probe needs
        Check.Equal(CredentialState.FilePresent, probe.Credentials("claude"), "credential file found without reading it");
        Check.True(probe.CredentialFile("agy").EndsWith(Path.Combine(".gemini", "antigravity-cli", "cache", "onboarding.json"), StringComparison.Ordinal), "agy sign-in record path");

        Check.True(!probe.SolidWorks().Available && !probe.Resolve().Available, "Windows-only apps unavailable on Linux");
        Check.True(!probe.Blender().Available, "blender missing");

        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test-not-real");
        try
        {
            var (psi, problem) = probe.SignIn("claude");
            if (psi is not null)
                Check.True(!psi.Environment.ContainsKey("ANTHROPIC_API_KEY"), "sign-in terminal has no API key (subscription login)");
            else
                Check.True(problem!.Contains("terminal", StringComparison.Ordinal), "no terminal: tells the user to open one");
            var (none, why) = probe.SignIn("git");
            Check.True(none is null && why!.Contains("not installed", StringComparison.Ordinal), "sign-in for a missing CLI explains how to install");
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null); }
    }
}
