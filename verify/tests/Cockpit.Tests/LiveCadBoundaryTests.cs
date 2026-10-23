using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.CadCode;
using Cockpit.Core.Runtime;
using Cockpit.Core.Swx;
using Cockpit.Core.Tools;

namespace Cockpit.Tests;

/// <summary>
/// Live (needs COCKPIT_LIVE_PYTHON with build123d; runs in the real bubblewrap sandbox on Linux): every template at its
/// minimum and maximum parameters, plus seeded random valid sets, is generated, structurally validated and
/// re-imported; combinations the geometry rules refuse are refused before any code runs.
/// </summary>
static class LiveCadBoundaryTests
{
    static JsonObject Part(string key, string template, Dictionary<string, double> p) => new()
    {
        ["key"] = key, ["template"] = template,
        ["params"] = new JsonObject(p.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
        ["envelopeMm"] = new JsonArray(5000, 5000, 5000),
    };

    static bool Valid(JsonObject part)
    {
        try { CodeCadToolProvider.ParseParts(JsonDocument.Parse(new JsonObject { ["parts"] = new JsonArray(part.DeepClone()) }.ToJsonString()).RootElement); return true; }
        catch (FormatException) { return false; }
    }

    public static async Task BoundaryAndRandomParameters(string bridgeDll)
    {
        if (LiveTests.Python is null) throw new SkipException("COCKPIT_LIVE_PYTHON is not set");
        string folder = Path.Combine(Path.GetTempPath(), "cockpit-live-cad", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        await using var runtime = await CockpitRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new CockpitPaths(Path.Combine(folder, "data")),
            Bus = new BusOptions { PipeName = "cockpit-lcad-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            SwxBackend = new SimulatedSwxBackend(),
            Python = new LocalPythonRunner(LiveTests.Python, [], DefaultProcessRunner.Instance),
        });
        runtime.Approvals.Target = new AutoApprovalPrompt(ToolRisk.Destructive);
        var job = await runtime.StartJobAsync(JobSpecFactory.CodeCad(folder, "bounds", LaunchMode.None) with { JobId = "live-bounds" });
        await using var client = await runtime.ConnectClientAsync(job);

        var rng = new Random(20261023);
        var candidates = new List<JsonObject>();
        int refused = 0;
        foreach (var (template, specs) in CodeCadToolProvider.Templates)
        {
            var sets = new List<(string Tag, Dictionary<string, double> P)>
            {
                ("min", specs.ToDictionary(s => s.Name, s => s.Min)),
                ("max", specs.ToDictionary(s => s.Name, s => s.Max)),
            };
            for (int k = 0; k < 40 && sets.Count < 5; k++)
            {
                var p = specs.ToDictionary(s => s.Name, s => s.Integer ? Math.Round(s.Min + rng.NextDouble() * (s.Max - s.Min)) : Math.Round(s.Min + rng.NextDouble() * (s.Max - s.Min), 2));
                if (Valid(Part("probe", template, p))) sets.Add(($"r{k}", p));
            }
            foreach (var (tag, p) in sets)
            {
                var part = Part($"{template}_{tag}", template, p);
                if (Valid(part)) candidates.Add(part); else refused++;
            }
        }
        Check.True(candidates.Count >= 7 * 3, $"at least 3 valid sets per template ({candidates.Count} built, {refused} boundary combinations refused by the geometry rules)");

        int built = 0;
        var failures = new List<string>();
        foreach (var chunk in candidates.Chunk(12))
        {
            var r = await client.CallToolAsync(CodeCadToolProvider.GenerateTool, new JsonObject { ["outputFolder"] = folder, ["parts"] = new JsonArray(chunk.Select(c => (JsonNode)c.DeepClone()).ToArray()) }, TimeSpan.FromMinutes(15));
            Check.True(r.Structured is not null, "generator answered: " + r.Text);
            foreach (var made in r.Structured!["parts"]!.AsArray())
            {
                built++;
                var bbox = made!["bboxMm"]!.AsArray().Select(x => (double)x!).ToArray();
                Check.True((int)made["solids"]! >= 1 && (double)made["volumeMm3"]! > 0 && bbox.All(v => v > 0 && v <= 5000), $"{made["key"]}: solid, volume and box re-imported independently");
            }
            failures.AddRange(r.Structured["failed"]!.AsArray().Select(f => $"{f!["key"]}: {f["error"]}"));
        }
        Console.WriteLine($"      {built} parts built and re-imported in the sandbox; {refused} boundary combinations refused up front; {failures.Count} failed in generation");
        foreach (var f in failures) Console.WriteLine("        failed: " + f[..Math.Min(200, f.Length)]);
        Check.True(failures.Count == 0, $"every combination the rules accept produces a valid solid ({failures.Count} did not)");
    }
}
