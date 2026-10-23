using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Protocol;
using Cockpit.Protocol.Swx;

namespace Cockpit.Core.Swx;

/// <summary>
/// What <see cref="SwxToolProvider"/> talks to. In production this is <see cref="SwxWorkerClient"/> (the
/// out-of-process STA worker that drives SolidWorks over COM); <see cref="SimulatedSwxBackend"/> stands in
/// for tests, `cockpit doctor` and `cockpit assemble --simulate` on machines without SolidWorks.
/// </summary>
public interface ISwxBackend
{
    /// <summary>Methods: "health", "part_cards" {paths}, "build_assembly" {plan}. Errors surface as <see cref="SwxWorkerException"/>.</summary>
    Task<JsonNode?> CallAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// A pretend SolidWorks: every .SLDPRT gets a card with one bore (c1, r = 5 mm on +Z) and one end face (p1).
/// Builds never touch SolidWorks files: the plan is written to "&lt;output&gt;.simulated.json" and that path is
/// reported, so a simulated run can never be mistaken for a real assembly.
/// </summary>
public sealed class SimulatedSwxBackend : ISwxBackend
{
    public const string SimulatedSuffix = ".simulated.json";

    public ConcurrentQueue<ResolvedAssemblyPlan> Builds { get; } = new();
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Lets a test make a call fail with a given worker error code (e.g. MODAL_DIALOG_BLOCKING).</summary>
    public Func<string, SwxWorkerException?>? FailWith { get; set; }

    public Task<JsonNode?> CallAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Enqueue(method);
        if (FailWith?.Invoke(method) is { } failure) throw failure;
        return Task.FromResult<JsonNode?>(method switch
        {
            "health" => JsonValue.Create("SolidWorks (simulated)"),
            "part_cards" => PartCards(parameters),
            "build_assembly" => Build(parameters),
            "import_step" => ImportStep(parameters),
            _ => throw new SwxWorkerException("unknown_method", $"Unknown method '{method}'."),
        });
    }

    private static JsonNode PartCards(JsonObject parameters)
    {
        var cards = new List<PartCard>();
        var errors = new List<PathError>();
        int i = 0;
        foreach (var node in parameters["paths"]?.AsArray() ?? [])
        {
            string path = (string)node!;
            if (!File.Exists(path))
            {
                errors.Add(new PathError(path, "open_failed", "File not found."));
                continue;
            }
            double x = 0.05 * i++;
            cards.Add(new PartCard(path, "mm", [x - 0.01, -0.01, 0, x + 0.01, 0.01, 0.02], 0.1, ["Default"], [],
            [
                new FaceInfo("c1", FaceKind.Cylinder, 0.000314, [x, 0, 0], [0, 0, 1], 0.005, "U0lN", "bore", false),
                new FaceInfo("p1", FaceKind.Plane, 0.0004, [x, 0, 0.02], [0, 0, 1], null, "U0lN", "end", false),
            ], ["simulated: no SolidWorks was used"]));
        }
        return JsonSerializer.SerializeToNode(new PartCardBatch(cards, errors), SwxJsonContext.Default.PartCardBatch)!;
    }

    /// <summary>Writes a marker .SLDPRT per STEP file (so part_cards can be simulated on it); never overwrites.</summary>
    private static JsonNode ImportStep(JsonObject parameters)
    {
        string output = (string)parameters["outputFolder"]!;
        var results = new List<StepImportResult>();
        foreach (var node in parameters["paths"]?.AsArray() ?? [])
        {
            string source = (string)node!;
            string target = Path.Combine(output, Path.GetFileNameWithoutExtension(source) + ".SLDPRT");
            if (!File.Exists(source)) results.Add(new StepImportResult(source, null, "not_found: file does not exist"));
            else if (File.Exists(target)) results.Add(new StepImportResult(source, null, $"exists: {target} already exists; not overwritten"));
            else
            {
                File.WriteAllText(target, "simulated part imported from " + source, Utf8.NoBom);
                results.Add(new StepImportResult(source, target, null));
            }
        }
        return JsonSerializer.SerializeToNode(new StepImportBatch(results), SwxJsonContext.Default.StepImportBatch)!;
    }

    private JsonNode Build(JsonObject parameters)
    {
        var plan = parameters["plan"].Deserialize(SwxJsonContext.Default.ResolvedAssemblyPlan)
            ?? throw new SwxWorkerException("bad_request", "Missing plan.");
        Builds.Enqueue(plan);
        string output = plan.OutputPath + SimulatedSuffix;
        if (File.Exists(output)) throw new SwxWorkerException("output_exists", $"{output} already exists.");
        File.WriteAllText(output, JsonSerializer.Serialize(plan, SwxJsonContext.Default.ResolvedAssemblyPlan), Utf8.NoBom);

        var result = new BuildResult(output, true, "Cockpit: assembly",
            plan.Components.Select((c, n) => new ComponentResult(c.Key, $"{Path.GetFileNameWithoutExtension(c.PartPath)}-{n + 1}", c.Fixed)).ToList(),
            plan.Mates.Select(m => new MateResult(m.Mate.Key, MateStatus.Ok, "Cockpit_" + m.Mate.Key, null)).ToList(),
            [string.Create(CultureInfo.InvariantCulture, $"simulated build: {plan.Mates.Count} mates, nothing opened in SolidWorks")]);
        return JsonSerializer.SerializeToNode(result, SwxJsonContext.Default.BuildResult)!;
    }
}
