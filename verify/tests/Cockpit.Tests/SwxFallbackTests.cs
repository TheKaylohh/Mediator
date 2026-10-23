using System.Diagnostics;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Runtime;
using Cockpit.Core.Swx;
using Cockpit.SwxWorker;

namespace Cockpit.Tests;

/// <summary>
/// V8: building and running without SolidWorks. The worker must compile without the interop assemblies
/// (SOLIDWORKS_NOT_INSTALLED), the ship build must warn (COCKPIT001) rather than fail, and every SolidWorks call must
/// answer "SolidWorks assemblies not installed on this machine" with a code the Core turns into a do-not-retry message.
/// </summary>
static class SwxFallbackTests
{
    const string Expected = "SolidWorks assemblies not installed on this machine";

    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-swxfb", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    sealed class ApproveAll : IApprovalPrompt
    {
        public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct) => Task.FromResult(ApprovalResponse.ApproveAll);
    }

    /// <summary>The variant built by run-tests.sh from verify/stubs/SwxWorkerNoInterop (the worker's sources, no interop).</summary>
    public static string NoInteropWorkerDll()
    {
        string dll = Path.Combine(XamlChecker.RepoRoot(), "verify", "stubs", "SwxWorkerNoInterop", "bin", "Debug", "net8.0", "cockpit-swx-worker-nointerop.dll");
        if (!File.Exists(dll)) throw new Exception($"{dll} is missing: build verify/stubs/SwxWorkerNoInterop with -p:CockpitVerify=true (verify/run-tests.sh does)");
        return dll;
    }

    /// <summary>In-process: the unavailable host fails every call with the code; the stub build still gets the COM host.</summary>
    public static async Task UnavailableHostInProcess()
    {
        using ISwxHost host = new SwxUnavailableHost();
        async Task Fails(Func<Task> call, string what)
        {
            try { await call(); throw new Exception($"{what} should have failed"); }
            catch (SwxException ex) { Check.True(ex.Code == SwxUnavailableHost.Code && ex.Message.StartsWith(Expected, StringComparison.Ordinal), $"{what}: {ex.Code}: {ex.Message}"); }
        }
        await Fails(() => host.HealthAsync(CancellationToken.None), "health");
        await Fails(() => host.ExtractPartCardsAsync(["a.sldprt"], TimeSpan.FromSeconds(5), CancellationToken.None), "part_cards");
        await Fails(() => host.ImportStepAsync(["a.step"], "/tmp", TimeSpan.FromSeconds(5), CancellationToken.None), "import_step");
        await host.ShutdownAsync(TimeSpan.FromSeconds(1)); // never throws
        Check.Equal(SwxWorkerException.SolidWorksNotInstalled, SwxUnavailableHost.Code, "worker and Core agree on the code");

        // This test binary references the worker built against the verify stub: the factory must pick the COM host.
        Check.True(SwxHostFactory.HasSolidWorksInterop, "stub build reports the interop as present");
        using var real = SwxHostFactory.Create(new SwxHostOptions { UseStaApartment = false });
        Check.True(real is SwxComHost, "stub build creates SwxComHost");
    }

    /// <summary>The no-interop worker as a real child process, through the bus and the tool provider.</summary>
    public static async Task NoInteropWorkerThroughTheBus(string bridgeDll)
    {
        string worker = NoInteropWorkerDll();
        string folder = TempDir("parts");
        File.WriteAllText(Path.Combine(folder, "bracket.SLDPRT"), "not a real part");

        await using var runtime = await CockpitRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new CockpitPaths(TempDir("data")),
            Bus = new BusOptions { PipeName = "cockpit-swxfb-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            SwxWorker = new ComponentCommand(ComponentLocator.DotnetHost(), [worker]),
            Python = new FakePythonRunner((_, _) => new Cockpit.Core.Tools.ProcessResult(1, "", "no python", false), available: false),
            Blender = new FakeBlender(),
            Ffmpeg = null,
        });
        runtime.Approvals.Target = new ApproveAll();

        try
        {
            await runtime.Swx.CallAsync("health", new JsonObject(), TimeSpan.FromSeconds(30), CancellationToken.None);
            throw new Exception("health should fail without SolidWorks");
        }
        catch (SwxWorkerException ex)
        {
            Check.True(ex.Code == SwxWorkerException.SolidWorksNotInstalled && ex.Message.StartsWith(Expected, StringComparison.Ordinal), $"health: {ex.Code}: {ex.Message}");
        }

        var job = await runtime.StartJobAsync(JobSpecFactory.Assemble(folder, LaunchMode.None) with { JobId = "swx-none-1" });
        await using var client = await runtime.ConnectClientAsync(job);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var cards = await client.CallToolAsync(SwxToolProvider.PartCardsTool, new JsonObject { ["folder"] = folder }, TimeSpan.FromSeconds(30));
            Check.True(cards.IsError && cards.Text.Contains(Expected, StringComparison.Ordinal) && cards.Text.Contains("Do not retry", StringComparison.Ordinal),
                $"part cards (attempt {attempt}) explain the missing SolidWorks: {cards.Text}");
        }
        var audited = await runtime.Audit.ListAsync("swx-none-1");
        Check.True(audited.Count(a => a.Tool == SwxToolProvider.PartCardsTool) == 2, "both calls audited");
        await runtime.EndJobAsync(job.JobId);
    }

    /// <summary>
    /// A real ship build (CockpitVerify=false) of the worker with no interop: succeeds with warning COCKPIT001 even with
    /// -warnaserror; CockpitRequireSolidWorks=true turns it into error COCKPIT002; the built worker answers the message.
    /// Built in a scratch copy (own obj/), restored from the empty local feed. Off Windows the copy targets net8.0
    /// instead of net8.0-windows, because the Windows desktop reference pack is a NuGet download.
    /// </summary>
    public static async Task ShipBuildWithoutSolidWorks()
    {
        string root = XamlChecker.RepoRoot();
        string copy = TempDir("ship");
        foreach (var f in new[] { "Directory.Build.props", "Directory.Build.targets" }) File.Copy(Path.Combine(root, f), Path.Combine(copy, f));
        Directory.CreateDirectory(Path.Combine(copy, "verify", "local-feed"));
        foreach (var project in new[] { "Cockpit.Protocol", "Cockpit.SwxWorker" })
        {
            string from = Path.Combine(root, "src", project);
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
                         .Where(f => !Path.GetRelativePath(from, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")))
            {
                string to = Path.Combine(copy, "src", project, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to);
            }
        }
        string csproj = Path.Combine(copy, "src", "Cockpit.SwxWorker", "Cockpit.SwxWorker.csproj");
        if (!OperatingSystem.IsWindows())
        {
            string text = File.ReadAllText(csproj);
            text = text.Replace("<TargetFramework>net8.0-windows</TargetFramework>", "<TargetFramework>net8.0</TargetFramework>", StringComparison.Ordinal);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<EnableWindowsTargeting[^>]*>true</EnableWindowsTargeting>", "");
            File.WriteAllText(csproj, text);
        }

        async Task<(int Exit, string Output)> Build(string output, params string[] extra)
        {
            var psi = new ProcessStartInfo(ComponentLocator.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = copy };
            foreach (var a in new[] { "build", csproj, "-nologo", "-v", "m", "-nodeReuse:false", "-c", "Release", "-o", output,
                         "-p:CockpitVerify=false", "-p:UseSharedCompilation=false", "-p:IsAotCompatible=false",
                         "-p:RestoreSources=" + Path.Combine(copy, "verify", "local-feed"),
                         "-p:SolidWorksInteropDir=" + Path.Combine(copy, "no-solidworks", "api", "redist") }.Concat(extra))
                psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            psi.Environment["DOTNET_NOLOGO"] = "1";
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(100));
            return (p.ExitCode, await stdout + await stderr);
        }

        string outDir = Path.Combine(copy, "out");
        var ok = await Build(outDir, "-warnaserror", "-p:TreatWarningsAsErrors=true");
        Check.True(ok.Exit == 0, $"ship build without SolidWorks succeeds even with -warnaserror:\n{ok.Output}");
        Check.True(ok.Output.Contains("warning COCKPIT001", StringComparison.Ordinal) && ok.Output.Contains(Expected, StringComparison.Ordinal), $"warning COCKPIT001 explains it:\n{ok.Output}");
        Check.True(!ok.Output.Contains(": error ", StringComparison.Ordinal), "no errors");

        // The built worker (real ship configuration, no stub anywhere) answers over its stdio protocol.
        var run = new ProcessStartInfo(ComponentLocator.DotnetHost()) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        run.ArgumentList.Add(Path.Combine(outDir, "cockpit-swx-worker.dll"));
        using (var w = Process.Start(run)!)
        {
            await w.StandardInput.WriteLineAsync("{\"id\":7,\"method\":\"health\"}");
            string? line = await w.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            w.StandardInput.Close();
            await w.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var reply = JsonNode.Parse(line ?? "{}")!;
            Check.True((long?)reply["id"] == 7 && (string?)reply["error"]?["code"] == SwxUnavailableHost.Code && ((string?)reply["error"]?["message"] ?? "").StartsWith(Expected, StringComparison.Ordinal),
                $"shipped no-interop worker answers solidworks_not_installed: {line}");
            Check.Equal(0, w.ExitCode, "worker exits normally when stdin closes");
        }

        var strict = await Build(Path.Combine(copy, "out-strict"), "-p:CockpitRequireSolidWorks=true");
        Check.True(strict.Exit != 0 && strict.Output.Contains("error COCKPIT002", StringComparison.Ordinal), $"CockpitRequireSolidWorks=true fails the build:\n{strict.Output}");
    }
}
