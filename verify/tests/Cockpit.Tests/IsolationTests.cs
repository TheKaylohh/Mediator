using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cockpit.Core.Bus;
using Cockpit.Core.CadCode;
using Cockpit.Core.Isolation;
using Cockpit.Core.Jobs;
using Cockpit.Core.Runtime;
using Cockpit.Core.Tools;

namespace Cockpit.Tests;

/// <summary>
/// Stand-in for build123d used by offline tests: the "generator" writes a minimal Part 21 file with one solid entity
/// and its size in a comment; the "validator" reads that comment back. Real build123d runs in the live tests.
/// </summary>
static class FakeCad
{
    public static int ValidatorRuns;

    public static string Step(double x, double y, double z, bool solid = true, bool truncated = false) =>
        "ISO-10303-21;\nHEADER;\nFILE_SCHEMA(('AUTOMOTIVE_DESIGN'));\nENDSEC;\nDATA;\n"
        + string.Create(CultureInfo.InvariantCulture, $"/* bbox {x} {y} {z} */\n")
        + (solid ? "#1=MANIFOLD_SOLID_BREP('',#2);\n" : "#1=SHELL_BASED_SURFACE_MODEL('',(#2));\n")
        + (truncated ? "" : "ENDSEC;\nEND-ISO-10303-21;\n");

    public static ProcessResult Run(string script, IReadOnlyList<string> args) => Run(script, args, p => p);

    public static ProcessResult Run(string script, IReadOnlyList<string> args, Func<string, string> map)
    {
        if (script.Contains("COCKPIT_STEP_VALIDATOR", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref ValidatorRuns);
            var files = new JsonArray();
            foreach (var a in args)
            {
                string text = File.ReadAllText(map(a));
                var m = Regex.Match(text, @"/\* bbox (\S+) (\S+) (\S+) \*/");
                bool solid = text.Contains("MANIFOLD_SOLID_BREP", StringComparison.Ordinal);
                files.Add(new JsonObject
                {
                    ["name"] = a, ["ok"] = solid, ["solids"] = solid ? 1 : 0, ["volume"] = solid ? 1000.0 : 0,
                    ["bbox"] = new JsonArray(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value).Select(v => (JsonNode)double.Parse((string)v!, CultureInfo.InvariantCulture)).Aggregate(new JsonArray(), (arr, v) => { arr.Add(v); return arr; }),
                });
            }
            return new ProcessResult(0, new JsonObject { ["files"] = files }.ToJsonString(), "", false);
        }
        var config = JsonNode.Parse(JsonSerializer.Deserialize<string>(Regex.Match(script, @"CONFIG = json\.loads\((""(?:[^""\\]|\\.)*"")\)").Groups[1].Value)!)!;
        var results = new JsonArray();
        foreach (var part in config["parts"]!.AsArray())
        {
            var env = part!["envelope"]!.AsArray();
            double x = (double)env[0]! - 1, y = (double)env[1]! - 1, z = (double)env[2]! - 1;
            File.WriteAllText(map((string)part["step"]!), Step(x, y, z));
            results.Add(new JsonObject { ["key"] = (string)part["key"]!, ["ok"] = true, ["bbox"] = new JsonArray(x, y, z), ["volume"] = 1000.0 });
        }
        return new ProcessResult(0, new JsonObject { ["results"] = results }.ToJsonString(), "", false);
    }
}

/// <summary>A sandbox stand-in that maps /in and /out like bubblewrap would and runs <see cref="FakeCad"/>; optionally misbehaves.</summary>
sealed class FakeSandbox(Action<SandboxRequest>? misbehave = null) : ISandboxRunner
{
    public readonly List<SandboxRequest> Requests = new();
    public string Id => "fake-sandbox";
    public string? Unavailable => null;
    public string Describe => "fake sandbox";

    public Task<ProcessResult> RunAsync(SandboxRequest r, CancellationToken ct)
    {
        lock (Requests) Requests.Add(r);
        string Map(string p) => p.StartsWith("/in/", StringComparison.Ordinal) ? Path.Combine(r.InputDirectory, p[4..])
            : p.StartsWith("/out/", StringComparison.Ordinal) ? Path.Combine(r.OutputDirectory, p[5..]) : p;
        string script = File.ReadAllText(Map(r.Arguments[1]));
        var result = FakeCad.Run(script, r.Arguments.Skip(2).ToList(), Map);
        if (!script.Contains("COCKPIT_STEP_VALIDATOR", StringComparison.Ordinal)) misbehave?.Invoke(r);
        return Task.FromResult(result);
    }
}

/// <summary>Approves, but changes the binding the user saw: models an approval that was for different content.</summary>
sealed class TamperingPrompt : IApprovalPrompt
{
    public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        if (request.Preview?["binding"] is JsonObject b) b["scriptSha256"] = new string('0', 64);
        return Task.FromResult(ApprovalResponse.ApproveAll);
    }
}

static class IsolationTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-iso", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static async Task<CockpitRuntime> Runtime(string bridgeDll, IApprovalPrompt prompt, ISandboxRunner sandbox, SandboxPython? sp, IPythonRunner? host = null)
    {
        var rt = await CockpitRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new CockpitPaths(TempDir("data")),
            Bus = new BusOptions { PipeName = "cockpit-iso-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            SwxBackend = new Cockpit.Core.Swx.SimulatedSwxBackend(),
            MediaProbe = new FakeMediaProbe(new()),
            Python = host ?? new FakePythonRunner(FakeCad.Run),
            Blender = new FakeBlender(),
            CadSandbox = sandbox,
            CadSandboxPython = sp,
        });
        rt.Approvals.Target = prompt;
        return rt;
    }

    static JsonObject Custom(string folder, string key, string script, params double[] env) => new()
    {
        ["outputFolder"] = folder, ["key"] = key, ["script"] = script, ["envelopeMm"] = new JsonArray(env.Select(v => (JsonNode)v).ToArray()),
    };

    /// <summary>Approval is bound to the exact script/arguments/folder/policy; a changed binding is refused; outputs are inspected before use.</summary>
    public static async Task ApprovalBindingAndOutputChecks(string bridgeDll)
    {
        string folder = TempDir("out");
        var sandbox = new FakeSandbox();
        var prompt = new UntickPrompt();
        await using (var runtime = await Runtime(bridgeDll, prompt, sandbox, new SandboxPython("/usr/bin/python3", [])))
        {
            var job = await runtime.StartJobAsync(JobSpecFactory.CodeCad(folder, "bracket", LaunchMode.None) with { JobId = "iso-1" });
            await using var client = await runtime.ConnectClientAsync(job);
            var r = await client.CallToolAsync(CodeCadToolProvider.CustomTool, Custom(folder, "br", "result = Box(10, 20, 30)", 11, 21, 31), TimeSpan.FromSeconds(30));
            Check.True(!r.IsError && (bool)r.Structured!["isolated"]!, "custom code ran in the sandbox: " + r.Text);
            var gen = sandbox.Requests[0];
            Check.True(gen.Arguments.SequenceEqual(["-I", "/in/generate_parts.py"]) && gen.InputDirectory != folder && gen.OutputDirectory != folder,
                "the sandbox got staged folders, never the user's folder");
            Check.True(!Directory.Exists(Path.GetDirectoryName(gen.InputDirectory)), "staging removed afterwards");
            var binding = prompt.Seen.Single().Preview!["binding"]!;
            Check.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("result = Box(10, 20, 30)"u8)).ToLowerInvariant(), (string?)binding["scriptSha256"], "approval shows the script hash");
            Check.Equal(CodeCadToolProvider.CustomPolicy, (string?)binding["policy"], "approval names the policy");
            Check.True(((string)binding["limits"]!).Contains("net=none", StringComparison.Ordinal), "approval states the limits");
            Check.True(File.Exists(Path.Combine(folder, "br.step")) && File.ReadAllText(Path.Combine(folder, "br_custom.py")).Contains("result = Box(10, 20, 30)", StringComparison.Ordinal),
                "accepted STEP copied in; exact approved script kept as a record");

            var p = await client.CallToolAsync(CodeCadToolProvider.GenerateTool, new JsonObject
            {
                ["outputFolder"] = folder, ["parts"] = new JsonArray(new JsonObject { ["key"] = "s1", ["template"] = "shaft", ["envelopeMm"] = new JsonArray(30, 30, 210) }),
            }, TimeSpan.FromSeconds(30));
            Check.True(!p.IsError && (bool)p.Structured!["isolated"]!, "templates also use the sandbox when one is available");
        }

        // The approval the gate returns no longer matches what would run: refused, nothing executes.
        var sandbox2 = new FakeSandbox();
        await using (var runtime = await Runtime(bridgeDll, new TamperingPrompt(), sandbox2, new SandboxPython("/usr/bin/python3", [])))
        {
            var job = await runtime.StartJobAsync(JobSpecFactory.CodeCad(folder, "bracket", LaunchMode.None) with { JobId = "iso-2" });
            await using var client = await runtime.ConnectClientAsync(job);
            var r = await client.CallToolAsync(CodeCadToolProvider.CustomTool, Custom(folder, "br2", "result = Box(1, 2, 3)", 5, 5, 5), TimeSpan.FromSeconds(30));
            Check.True(r.IsError && r.Text.Contains("not exactly what was approved", StringComparison.Ordinal) && sandbox2.Requests.Count == 0, "binding mismatch refused before anything ran: " + r.Text);
        }

        // Outputs that are links, surface-only or truncated are refused; nothing reaches the user's folder.
        string outside = TempDir("victim");
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        foreach (var (name, act) in new (string, Action<SandboxRequest>)[]
        {
            ("link", r => { File.Delete(Path.Combine(r.OutputDirectory, "bad.step")); File.CreateSymbolicLink(Path.Combine(r.OutputDirectory, "bad.step"), Path.Combine(outside, "keep.txt"));
                            Directory.CreateSymbolicLink(Path.Combine(r.OutputDirectory, "victim"), outside); }),
            ("surface", r => File.WriteAllText(Path.Combine(r.OutputDirectory, "bad.step"), FakeCad.Step(4, 4, 4, solid: false))),
            ("truncated", r => File.WriteAllText(Path.Combine(r.OutputDirectory, "bad.step"), FakeCad.Step(4, 4, 4, truncated: true))),
            ("extra", r => File.WriteAllText(Path.Combine(r.OutputDirectory, "payload.exe"), "MZ")),
        })
        {
            var sb = new FakeSandbox(act);
            await using var runtime = await Runtime(bridgeDll, new UntickPrompt(), sb, new SandboxPython("/usr/bin/python3", []));
            string f = TempDir("out-" + name);
            var job = await runtime.StartJobAsync(JobSpecFactory.CodeCad(f, "x", LaunchMode.None) with { JobId = "iso-" + name });
            await using var client = await runtime.ConnectClientAsync(job);
            var r = await client.CallToolAsync(CodeCadToolProvider.CustomTool, Custom(f, "bad", "result = Box(4, 4, 4)", 5, 5, 5), TimeSpan.FromSeconds(30));
            if (name == "extra")
                Check.True(!r.IsError && !File.Exists(Path.Combine(f, "payload.exe")) && r.Text.Contains("unexpected output name", StringComparison.Ordinal), "unexpected extra output ignored: " + r.Text);
            else
                Check.True(r.IsError && !File.Exists(Path.Combine(f, "bad.step")), $"{name} output refused: {r.Text}");
            Check.True(File.Exists(Path.Combine(outside, "keep.txt")), $"{name}: cleaning the staging folder did not follow the planted link");
        }
    }

    // ── Real bubblewrap ────────────────────────────────────────────────────────────────────────────

    static (BubblewrapSandbox Box, string Python) RealSandbox()
    {
        if (!OperatingSystem.IsLinux()) throw new SkipException("bubblewrap sandbox is Linux/WSL only; Windows uses the WSL sandbox (not run here)");
        var box = new BubblewrapSandbox();
        if (box.Unavailable is string why) throw new SkipException(why);
        string? py = CliLaunchBuilder.ResolveOnPath("python3");
        if (py is null) throw new SkipException("no python3 to run the hostile scripts");
        return (box, new FileInfo(py).ResolveLinkTarget(true)?.FullName ?? py);
    }

    static async Task<(ProcessResult Result, JsonNode? Report, long Ms, string Out)> Hostile(string script, SandboxLimits? limits = null)
    {
        var (box, py) = RealSandbox();
        string stage = TempDir("stage"), input = Path.Combine(stage, "in"), output = Path.Combine(stage, "out");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(input, "s.py"), script);
        var sw = Stopwatch.StartNew();
        var r = await box.RunAsync(new SandboxRequest(py, ["-I", "/in/s.py"], input, output, [], new Dictionary<string, string>(),
            limits ?? new SandboxLimits { Wall = TimeSpan.FromSeconds(30) }), CancellationToken.None);
        JsonNode? report = null;
        foreach (var line in r.Stdout.Split('\n').Reverse())
            if (line.StartsWith('{')) { try { report = JsonNode.Parse(line); break; } catch (JsonException) { } }
        return (r, report, sw.ElapsedMilliseconds, output);
    }

    /// <summary>Files, environment, privileges and the process view inside the real sandbox.</summary>
    public static async Task RealSandboxConfinement()
    {
        string secretDir = TempDir("secret");
        string secret = Path.Combine(secretDir, "id_rsa");
        File.WriteAllText(secret, "PRIVATE");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test-should-not-leak");
        Environment.SetEnvironmentVariable("COCKPIT_JOB_TOKEN", "tok-should-not-leak");
        try
        {
            var (r, rep, _, _) = await Hostile($$"""
import json, os, subprocess, ctypes
def attempt(f):
    try: return "OK:" + str(f())[:60]
    except Exception as e: return "DENIED:" + type(e).__name__
out = {
  "root": sorted(os.listdir("/")),
  "secret": attempt(lambda: open({{JsonSerializer.Serialize(secret)}}).read()),
  "shadow": attempt(lambda: open("/etc/shadow").read()),
  "passwd": attempt(lambda: open("/etc/passwd").read()),
  "home": attempt(lambda: os.listdir("/home")),
  "rootdir": attempt(lambda: os.listdir("/root")),
  "write_usr": attempt(lambda: open("/usr/cockpit-evil", "w").write("x")),
  "write_in": attempt(lambda: open("/in/evil", "w").write("x")),
  "write_out": attempt(lambda: open("/out/fine.txt", "w").write("x")),
  "env": sorted(os.environ.keys()),
  "uid": os.getuid(),
  "pids": sorted(int(p) for p in os.listdir("/proc") if p.isdigit()),
  "nested_userns": subprocess.run(["/usr/bin/unshare", "-U", "true"], capture_output=True).returncode,
  "mount": ctypes.CDLL(None, use_errno=True).mount(b"none", b"/tmp", b"tmpfs", 0, None),
}
print(json.dumps(out))
""");
            Check.True(rep is not null, "confinement script reported: " + r.Tail());
            var root = rep!["root"]!.AsArray().Select(x => (string)x!).ToList();
            Check.True(root.All(x => x is "bin" or "dev" or "etc" or "in" or "lib" or "lib64" or "lib32" or "out" or "proc" or "sbin" or "tmp" or "usr"),
                "only the expected top-level folders exist: " + string.Join(",", root));
            foreach (var k in new[] { "secret", "shadow", "passwd", "home", "rootdir", "write_usr", "write_in" })
                Check.True(((string)rep[k]!).StartsWith("DENIED", StringComparison.Ordinal), $"{k} denied inside the sandbox (got {rep[k]})");
            Check.True(((string)rep["write_out"]!).StartsWith("OK", StringComparison.Ordinal), "the private /out is writable");
            var env = rep["env"]!.AsArray().Select(x => (string)x!).ToList();
            Check.True(!env.Any(e => e.Contains("KEY", StringComparison.Ordinal) || e.Contains("TOKEN", StringComparison.Ordinal) || e.Contains("PROXY", StringComparison.OrdinalIgnoreCase)),
                "no keys, tokens or proxies in the sandbox environment: " + string.Join(",", env));
            if (BubblewrapSandbox.RunningAsRoot) Check.Equal(BubblewrapSandbox.NobodyId, (int)rep["uid"]!, "runs as uid 65534 when Cockpit is root");
            Check.True(rep["pids"]!.AsArray().Count <= 4, "own PID namespace: only the sandbox's processes are visible: " + rep["pids"]!.ToJsonString());
            Check.True((int)rep["nested_userns"]! != 0, "nested user namespaces are disabled");
            Check.True((int)rep["mount"]! != 0, "mount(2) fails (no capabilities)");
            Check.True(File.Exists(secret) && !File.Exists("/usr/cockpit-evil"), "host untouched");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
            Environment.SetEnvironmentVariable("COCKPIT_JOB_TOKEN", null);
        }
    }

    /// <summary>No network: a host loopback listener and an external address are both unreachable.</summary>
    public static async Task RealSandboxNoNetwork()
    {
        RealSandbox();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        try
        {
            var (r, rep, _, _) = await Hostile($$"""
import json, socket
def tcp(host, port):
    try:
        s = socket.create_connection((host, port), timeout=3); s.close(); return "CONNECTED"
    except Exception as e: return "FAILED:" + type(e).__name__
def dns():
    try: socket.getaddrinfo("example.com", 443); return "RESOLVED"
    except Exception as e: return "FAILED:" + type(e).__name__
print(json.dumps({"host_loopback": tcp("127.0.0.1", {{port}}), "external": tcp("1.1.1.1", 443), "dns": dns(),
                  "ifaces": [n for _, n in socket.if_nameindex()]}))
""");
            Check.True(rep is not null, "network script reported: " + r.Tail());
            foreach (var k in new[] { "host_loopback", "external", "dns" })
                Check.True(((string)rep![k]!).StartsWith("FAILED", StringComparison.Ordinal), $"{k} unreachable (got {rep[k]})");
            Check.True(rep!["ifaces"]!.AsArray().All(n => (string)n! == "lo"), "only a loopback interface exists: " + rep["ifaces"]!.ToJsonString());
            Check.True(!accept.IsCompleted, "the host listener never received a connection");
        }
        finally { listener.Stop(); }
    }

    static bool AnyProcessWith(string marker) => Directory.EnumerateDirectories("/proc").Where(d => Path.GetFileName(d).All(char.IsDigit)).Any(d =>
    {
        try { return File.ReadAllText(Path.Combine(d, "cmdline")).Replace('\0', ' ').Contains(marker, StringComparison.Ordinal); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    });

    /// <summary>Resource limits and cleanup: memory, CPU, processes, file size, wall-clock kill, no surviving grandchildren.</summary>
    public static async Task RealSandboxLimitsAndCleanup()
    {
        RealSandbox();
        // Memory: 1 GiB address space; a 3 GiB allocation fails inside, the host is unaffected.
        var (_, mem, _, _) = await Hostile("""
import json
try:
    b = bytearray(3 * 1024**3); r = "ALLOCATED"
except MemoryError: r = "MemoryError"
print(json.dumps({"r": r}))
""", new SandboxLimits { AddressSpaceBytes = 1L << 30, Wall = TimeSpan.FromSeconds(30) });
        Check.Equal("MemoryError", (string?)mem?["r"], "address-space limit enforced");

        // CPU: 1 s of CPU, then the kernel stops the spinning process (SIGXCPU/SIGKILL), well before the wall limit.
        var (cpu, _, cpuMs, _) = await Hostile("while True: pass\n", new SandboxLimits { CpuSeconds = 1, Wall = TimeSpan.FromSeconds(30) });
        Check.True(!cpu.TimedOut && cpu.ExitCode != 0 && cpuMs < 15_000, $"CPU limit stopped the loop (exit {cpu.ExitCode}, {cpuMs} ms)");

        // Processes: fork until refused; never more than the limit.
        var (_, fork, _, _) = await Hostile("""
import json, os, time
n = 0; err = None
for i in range(200):
    try:
        pid = os.fork()
        if pid == 0:
            time.sleep(20); os._exit(0)
        n += 1
    except OSError as e:
        err = type(e).__name__; break
print(json.dumps({"forked": n, "error": err}), flush=True)
os._exit(0)
""", new SandboxLimits { Processes = 16, Wall = TimeSpan.FromSeconds(30) });
        Check.True(fork is not null && (int)fork["forked"]! < 16 && fork["error"] is not null, "process limit enforced: " + fork?.ToJsonString());

        // File size: 1 MiB cap; a 10 MiB write is cut off.
        var (_, _, _, outDir) = await Hostile("""
import json
try:
    with open("/out/big.bin", "wb") as f:
        for i in range(10): f.write(b"\0" * (1024 * 1024))
    r = "WROTE"
except OSError as e: r = type(e).__name__
print(json.dumps({"r": r}))
""", new SandboxLimits { FileSizeBytes = 1L << 20, Wall = TimeSpan.FromSeconds(30) });
        long size = new FileInfo(Path.Combine(outDir, "big.bin")).Length;
        Check.True(size <= 1L << 20, $"file-size limit enforced ({size} bytes)");

        // A detached grandchild does not outlive a normal exit...
        string marker1 = "299." + Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
        await Hostile($$"""
import subprocess
subprocess.Popen(["/usr/bin/sleep", "{{marker1}}"], start_new_session=True)
print('{"spawned": true}')
""");
        await Task.Delay(300);
        Check.True(!AnyProcessWith("sleep " + marker1), "a detached grandchild died with the sandbox (normal exit)");

        // ...nor a wall-clock kill.
        string marker2 = "298." + Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
        var (wall, _, wallMs, _) = await Hostile($$"""
import subprocess, time
subprocess.Popen(["/usr/bin/sleep", "{{marker2}}"], start_new_session=True)
time.sleep(600)
""", new SandboxLimits { Wall = TimeSpan.FromSeconds(2) });
        await Task.Delay(300);
        Check.True(wall.TimedOut && wallMs < 10_000, $"wall-clock limit killed the run ({wallMs} ms)");
        Check.True(!AnyProcessWith("sleep " + marker2), "nothing survives the kill (PID namespace torn down)");
    }

    /// <summary>Live: real build123d inside the real sandbox through the bus; a hostile custom script is contained and its outputs refused.</summary>
    public static async Task LiveCustomCodeInSandbox(string bridgeDll)
    {
        if (LiveTests.Python is null) throw new SkipException("COCKPIT_LIVE_PYTHON is not set");
        if (!OperatingSystem.IsLinux() || new BubblewrapSandbox().Unavailable is string why) throw new SkipException("bubblewrap not available");
        var sp = SandboxPython.Probe(LiveTests.Python) ?? throw new Exception("could not probe the live Python");
        string folder = TempDir("live-out");
        string secretDir = TempDir("live-secret");
        File.WriteAllText(Path.Combine(secretDir, "secret.txt"), "PRIVATE");
        await using var runtime = await Runtime(bridgeDll, new UntickPrompt(), new BubblewrapSandbox(), sp,
            new LocalPythonRunner(LiveTests.Python, [], DefaultProcessRunner.Instance));
        var job = await runtime.StartJobAsync(JobSpecFactory.CodeCad(folder, "bracket", LaunchMode.None) with { JobId = "iso-live" });
        await using var client = await runtime.ConnectClientAsync(job);

        var ok = await client.CallToolAsync(CodeCadToolProvider.CustomTool, Custom(folder, "plate",
            "result = Box(40, 30, 6) - Cylinder(5, 6)", 41, 31, 7), TimeSpan.FromMinutes(5));
        Check.True(!ok.IsError && (bool)ok.Structured!["isolated"]!, "build123d ran inside bubblewrap: " + ok.Text);
        var made = ok.Structured!["parts"]!.AsArray().Single()!;
        Check.True((int)made["solids"]! == 1 && Math.Abs((double)made["bboxMm"]![0]! - 40) < 0.01, "re-import found 1 solid, 40 mm wide: " + made.ToJsonString());
        Check.True(StepValidator.Check(File.OpenRead((string)made["step"]!)).Ok, "the copied STEP passes the structural check");

        var hostile = await client.CallToolAsync(CodeCadToolProvider.CustomTool, Custom(folder, "evil", $$"""
import os, socket
leak = ""
try: leak = open({{JsonSerializer.Serialize(Path.Combine(secretDir, "secret.txt"))}}).read()
except Exception as e: leak = type(e).__name__
try: socket.create_connection(("1.1.1.1", 443), timeout=2); net = "open"
except Exception as e: net = type(e).__name__
os.symlink("/etc/passwd", "/out/evil.step")
result = Box(1, 1, 1)
print("LEAK=" + leak + " NET=" + net)
""", 2, 2, 2), TimeSpan.FromMinutes(5));
        Check.True(hostile.IsError, "the hostile script produced nothing usable: " + hostile.Text);
        Check.True(!File.Exists(Path.Combine(folder, "evil.step")), "no file copied out");
        Check.True(File.ReadAllText(Path.Combine(secretDir, "secret.txt")) == "PRIVATE", "host secret untouched");
    }
}
