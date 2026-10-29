using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Security;
using Cockpit.Protocol.Swx;

namespace Cockpit.Core.Swx;

/// <summary>
/// MCP tools for SolidWorks. The model sees short face IDs ("c1", "p2"); persist references and geometry
/// stay in the Core's per-job card cache, so the model cannot fabricate them and cards stay small.
/// </summary>
public sealed class SwxToolProvider : IToolProvider
{
    public const string Lease = "solidworks";
    public const string PartCardsTool = "swx_part_cards";
    public const string BuildTool = "swx_build_assembly";
    public const string ImportStepTool = "swx_import_step";
    private const int MaxParts = 200;

    private readonly ISwxBackend _worker;
    private readonly ConcurrentDictionary<(string JobId, string Path), PartCard> _cards = new();

    public SwxToolProvider(ISwxBackend worker, JobRegistry jobs)
    {
        _worker = worker;
        jobs.JobRevoked += jobId =>
        {
            foreach (var key in _cards.Keys.Where(k => k.JobId == jobId).ToList()) _cards.TryRemove(key, out _);
        };
    }

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new ToolDescriptor(PartCardsTool,
            "Read SolidWorks parts (.SLDPRT) and return compact cards: units, bounding box (m), mass, configurations, " +
            "Mate References and candidate mating faces with IDs (c1.. cylinders, p1.. planes). Read-only. " +
            "Call this before swx_build_assembly; mates must refer to these face IDs.",
            (JsonObject)JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "paths":  { "type": "array", "items": { "type": "string" }, "description": "Absolute .SLDPRT paths." },
                "folder": { "type": "string", "description": "Folder whose top-level .SLDPRT files to read (instead of paths)." }
              },
              "additionalProperties": false
            }
            """)!,
            ToolRisk.ReadOnly, Lease, TimeSpan.FromMinutes(10)),

        new ToolDescriptor(BuildTool,
            "Create a NEW SolidWorks assembly from parts already read with swx_part_cards. Inserts components at given " +
            "positions (m), fixes those marked fixed, and adds mates between face IDs. Never overwrites files and never " +
            "edits the parts. The user reviews the mate plan before anything runs. Mates that fail are left out and reported.",
            (JsonObject)JsonNode.Parse("""
            {
              "type": "object",
              "required": ["outputPath", "components"],
              "properties": {
                "outputPath":   { "type": "string", "description": "Absolute path of the new .SLDASM (must not exist)." },
                "templatePath": { "type": "string", "description": "Optional .ASMDOT; default is SolidWorks' default assembly template." },
                "components": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["key", "partPath"],
                    "properties": {
                      "key": { "type": "string" }, "partPath": { "type": "string" }, "configuration": { "type": "string" },
                      "positionM": { "type": "array", "items": { "type": "number" } }, "fixed": { "type": "boolean" }
                    }
                  }
                },
                "mates": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "required": ["key", "type", "componentA", "faceA", "componentB", "faceB"],
                    "properties": {
                      "key": { "type": "string" },
                      "type": { "type": "string", "enum": ["Coincident", "Concentric", "Parallel", "Distance"] },
                      "componentA": { "type": "string" }, "faceA": { "type": "string" },
                      "componentB": { "type": "string" }, "faceB": { "type": "string" },
                      "alignment": { "type": "string", "enum": ["Closest", "Aligned", "AntiAligned"] },
                      "flip": { "type": "boolean" }, "distanceM": { "type": "number" },
                      "confidence": { "type": "number" }, "reason": { "type": "string" }
                    }
                  }
                }
              },
              "additionalProperties": false
            }
            """)!,
            ToolRisk.Write, Lease, TimeSpan.FromMinutes(15)),

        new ToolDescriptor(ImportStepTool,
            "Import STEP files (.step/.stp, e.g. from cad_generate_parametric_step) into SolidWorks and save each as a NEW .SLDPRT " +
            "in outputFolder (never overwrites). The user picks which files to import. Then call swx_part_cards on outputFolder.",
            (JsonObject)JsonNode.Parse("""
            {
              "type": "object",
              "required": ["outputFolder"],
              "properties": {
                "paths":        { "type": "array", "items": { "type": "string" }, "description": "Absolute .step/.stp paths." },
                "folder":       { "type": "string", "description": "Folder whose top-level STEP files to import (instead of paths)." },
                "outputFolder": { "type": "string", "description": "Existing folder for the new .SLDPRT files." }
              },
              "additionalProperties": false
            }
            """)!,
            ToolRisk.Write, Lease, TimeSpan.FromMinutes(20)),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        if (call.Tool.Name == ImportStepTool) return Task.FromResult<JsonNode?>(ImportPreview(call));
        if (call.Tool.Name != BuildTool) return Task.FromResult<JsonNode?>(null);
        var (plan, resolved, error) = ParseAndResolve(call);
        var preview = new JsonObject
        {
            ["kind"] = "swx_mate_plan",
            ["title"] = "Build SolidWorks assembly",
            ["summary"] = plan is null ? "Invalid plan" :
                $"{plan.Components.Count} components, {plan.Mates?.Count ?? 0} mates → {Path.GetFileName(plan.OutputPath)}",
            ["outputPath"] = plan?.OutputPath,
            ["error"] = error,
            ["selectable"] = resolved is not null && resolved.Mates.Count > 0,
        };
        if (plan is not null)
        {
            preview["components"] = new JsonArray(plan.Components.Select(c => (JsonNode)new JsonObject
            {
                ["key"] = c.Key,
                ["part"] = Path.GetFileName(c.PartPath),
                ["fixed"] = c.Fixed,
            }).ToArray());
        }
        // One row per mate: what the approval table shows. Unchecking a row drops that mate from the build.
        preview["rows"] = resolved is null ? new JsonArray() : new JsonArray(resolved.Mates.Select(m => (JsonNode)new JsonObject
        {
            ["key"] = m.Mate.Key,
            ["primary"] = $"{m.Mate.Type} ({m.Mate.Alignment})",
            ["detail"] = $"{Describe(m.Mate.ComponentA, m.FaceA)} ↔ {Describe(m.Mate.ComponentB, m.FaceB)}" + (m.Mate.Reason is { Length: > 0 } r ? $" · {r}" : ""),
            ["confidence"] = m.Mate.Confidence,
        }).ToArray());
        return Task.FromResult<JsonNode?>(preview);
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        try
        {
            return call.Tool.Name switch
            {
                PartCardsTool => await PartCardsAsync(call, ct).ConfigureAwait(false),
                BuildTool => await BuildAsync(call, ct).ConfigureAwait(false),
                ImportStepTool => await ImportStepAsync(call, ct).ConfigureAwait(false),
                _ => ToolResult.Fail($"Unknown tool {call.Tool.Name}."),
            };
        }
        catch (SwxWorkerException ex) when (ex.Code == SwxWorkerException.ModalDialogBlocking)
        {
            // The COM call is still pending behind the dialog. Retrying would queue behind it, so tell the model to stop.
            return new ToolResult(true,
                $"SolidWorks: {ex.Code}: {ex.Message} Do not retry this tool now. Ask the user to answer the dialog in SolidWorks, " +
                "then check the result (re-read part cards or reopen the output) before continuing.",
                new JsonObject { ["code"] = ex.Code, ["dialog"] = ex.Detail?.DeepClone() });
        }
        catch (SwxWorkerException ex)
        {
            return ToolResult.Fail($"SolidWorks: {ex.Code}: {ex.Message}");
        }
    }

    private static (List<string>? Paths, string? Output, string? Error) ImportArgs(ToolCallContext call)
    {
        var args = call.Arguments;
        var outputVerdict = call.Job.Paths.Check(args.GetProperty("outputFolder").GetString(), PathAccess.List);
        if (!outputVerdict.Allowed) return (null, null, outputVerdict.Message);
        string output = outputVerdict.FullPath;
        List<string> paths;
        if (args.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String)
        {
            var folderVerdict = call.Job.Paths.Check(f.GetString(), PathAccess.List);
            if (!folderVerdict.Allowed) return (null, null, folderVerdict.Message);
            paths = Directory.EnumerateFiles(folderVerdict.FullPath).Where(IsStep).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else if (args.TryGetProperty("paths", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            paths = ps.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        else return (null, null, "Pass either 'paths' or 'folder'.");
        if (paths.Count == 0) return (null, null, "No STEP files found.");
        if (paths.Count > MaxParts) return (null, null, $"More than {MaxParts} files; narrow the selection.");
        var authorized = new List<string>();
        foreach (var p in paths)
        {
            var v = call.Job.Paths.Check(p, PathAccess.Read);
            if (!v.Allowed) return (null, null, v.Message);
            if (!IsStep(v.FullPath)) return (null, null, $"{v.FullPath} is not a STEP file.");
            authorized.Add(v.FullPath);
        }
        return (authorized, output, null);
    }

    private static bool IsStep(string p) => Path.GetExtension(p).ToLowerInvariant() is ".step" or ".stp";

    private static JsonNode ImportPreview(ToolCallContext call)
    {
        var (paths, output, error) = ImportArgs(call);
        return new JsonObject
        {
            ["kind"] = "swx_import_step",
            ["title"] = "Import STEP files into SolidWorks",
            ["summary"] = error ?? $"{paths!.Count} file(s) → new .SLDPRT in {output}",
            ["selectable"] = paths is { Count: > 0 },
            ["rows"] = new JsonArray((paths ?? []).Select(p => (JsonNode)new JsonObject
            {
                ["key"] = p,
                ["primary"] = Path.GetFileName(p),
                ["detail"] = "→ " + Path.Combine(output!, Path.GetFileNameWithoutExtension(p) + ".SLDPRT") + (File.Exists(Path.Combine(output!, Path.GetFileNameWithoutExtension(p) + ".SLDPRT")) ? " (exists: will be skipped)" : ""),
            }).ToArray()),
        };
    }

    private async Task<ToolResult> ImportStepAsync(ToolCallContext call, CancellationToken ct)
    {
        var (paths, output, error) = ImportArgs(call);
        if (error is not null) return ToolResult.Fail(error);
        var chosen = call.ApprovedRows is { } keep ? paths!.Where(keep.Contains).ToList() : paths!;
        // SolidWorks opens these by path, so each is verified again right before the hand-off.
        foreach (var p in chosen.Append(output!))
            if (call.Job.Paths.VerifyExisting(p, p == output ? PathAccess.List : PathAccess.Read) is { Allowed: false } changed) return ToolResult.Fail(changed.Message);
        var parameters = new JsonObject { ["paths"] = new JsonArray(chosen.Select(p => (JsonNode)p).ToArray()), ["outputFolder"] = output };
        var node = await _worker.CallAsync("import_step", parameters, call.Tool.ExecutionTimeout, ct).ConfigureAwait(false);
        var batch = node.Deserialize(SwxJsonContext.Default.StepImportBatch) ?? throw new SwxWorkerException("bad_reply", "Empty import_step reply.");
        int ok = batch.Results.Count(r => r.PartPath is not null);
        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"Imported {ok} of {batch.Results.Count}"));
        if (chosen.Count < paths!.Count) sb.Append(CultureInfo.InvariantCulture, $" ({paths.Count - chosen.Count} excluded by the user; do not import them)");
        sb.Append(". ");
        foreach (var r in batch.Results.Where(r => r.Error is not null)) sb.Append(CultureInfo.InvariantCulture, $"{Path.GetFileName(r.Source)}: {r.Error}. ");
        if (ok > 0) sb.Append(CultureInfo.InvariantCulture, $"Next: swx_part_cards with folder \"{output}\".");
        var structured = new JsonObject
        {
            ["parts"] = new JsonArray(batch.Results.Where(r => r.PartPath is not null).Select(r => (JsonNode)r.PartPath!).ToArray()),
            ["errors"] = new JsonArray(batch.Results.Where(r => r.Error is not null).Select(r => (JsonNode)new JsonObject { ["source"] = r.Source, ["error"] = r.Error }).ToArray()),
        };
        return ok == 0 ? new ToolResult(true, sb.ToString(), structured) : ToolResult.Ok(sb.ToString(), structured);
    }

    private async Task<ToolResult> PartCardsAsync(ToolCallContext call, CancellationToken ct)
    {
        var args = call.Arguments;
        List<string> paths;
        if (args.TryGetProperty("folder", out var folderEl) && folderEl.ValueKind == JsonValueKind.String)
        {
            var folderVerdict = call.Job.Paths.Check(folderEl.GetString(), PathAccess.List);
            if (!folderVerdict.Allowed) return ToolResult.Fail(folderVerdict.Message);
            string folder = folderVerdict.FullPath;
            paths = Directory.EnumerateFiles(folder, "*.sldprt", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false })
                .Where(p => !Path.GetFileName(p).StartsWith("~$", StringComparison.Ordinal)) // SolidWorks lock files
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Take(MaxParts + 1).ToList();
        }
        else if (args.TryGetProperty("paths", out var pathsEl) && pathsEl.ValueKind == JsonValueKind.Array)
        {
            paths = pathsEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
        }
        else
        {
            return ToolResult.Fail("Pass either 'paths' or 'folder'.");
        }

        if (paths.Count == 0) return ToolResult.Fail("No .SLDPRT files found.");
        if (paths.Count > MaxParts) return ToolResult.Fail($"More than {MaxParts} parts; narrow the selection.");
        for (int i = 0; i < paths.Count; i++)
        {
            var v = call.Job.Paths.VerifyExisting(paths[i], PathAccess.Read);
            if (!v.Allowed) return ToolResult.Fail(v.Message);
            paths[i] = v.FullPath;
        }

        var parameters = new JsonObject { ["paths"] = new JsonArray(paths.Select(p => (JsonNode)p).ToArray()) };
        var node = await _worker.CallAsync("part_cards", parameters, call.Tool.ExecutionTimeout, ct).ConfigureAwait(false);
        var batch = node.Deserialize(SwxJsonContext.Default.PartCardBatch) ?? throw new SwxWorkerException("bad_reply", "Empty part_cards reply.");

        foreach (var card in batch.Cards)
            _cards[(call.Job.JobId, Norm(card.Path))] = card;

        // What the model sees: no persist references or fallback names, geometry rounded.
        var forModel = new JsonObject
        {
            ["cards"] = new JsonArray(batch.Cards.Select(c => (JsonNode)new JsonObject
            {
                ["path"] = c.Path,
                ["units"] = c.DisplayUnits,
                ["bboxM"] = ToArray(c.BoundingBoxM),
                ["massKg"] = c.MassKg,
                ["configurations"] = new JsonArray(c.Configurations.Select(s => (JsonNode)s).ToArray()),
                ["mateReferences"] = new JsonArray(c.MateReferences.Select(s => (JsonNode)s).ToArray()),
                ["faces"] = new JsonArray(c.Faces.Select(f => (JsonNode)new JsonObject
                {
                    ["id"] = f.Id,
                    ["kind"] = f.Kind.ToString(),
                    ["areaM2"] = f.AreaM2,
                    ["pointM"] = ToArray(f.PointM),
                    ["dir"] = ToArray(f.DirectionUnit),
                    ["radiusM"] = f.RadiusM,
                    ["name"] = f.EntityNameIsFallback ? null : f.EntityName,
                }).ToArray()),
                ["warnings"] = c.Warnings.Count == 0 ? null : new JsonArray(c.Warnings.Select(w => (JsonNode)w).ToArray()),
            }).ToArray()),
            ["errors"] = new JsonArray(batch.Errors.Select(e => (JsonNode)new JsonObject { ["path"] = e.Path, ["code"] = e.Code, ["message"] = e.Message }).ToArray()),
        };
        string text = forModel.ToJsonString();
        return batch.Cards.Count == 0
            ? ToolResult.Fail("No part could be read: " + forModel["errors"]!.ToJsonString())
            : ToolResult.Ok(text, forModel);
    }

    private async Task<ToolResult> BuildAsync(ToolCallContext call, CancellationToken ct)
    {
        var (_, resolved, error) = ParseAndResolve(call);
        if (error is not null || resolved is null) return ToolResult.Fail(error ?? "Invalid plan.");

        // Honour a partial approval: only the mates the user kept are built.
        int excluded = 0;
        if (call.ApprovedRows is { } keep)
        {
            var kept = resolved.Mates.Where(m => keep.Contains(m.Mate.Key)).ToList();
            excluded = resolved.Mates.Count - kept.Count;
            resolved = resolved with { Mates = kept };
        }

        var parameters = new JsonObject { ["plan"] = JsonSerializer.SerializeToNode(resolved, SwxJsonContext.Default.ResolvedAssemblyPlan) };
        var node = await _worker.CallAsync("build_assembly", parameters, call.Tool.ExecutionTimeout, ct).ConfigureAwait(false);
        var result = node.Deserialize(SwxJsonContext.Default.BuildResult) ?? throw new SwxWorkerException("bad_reply", "Empty build reply.");

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{(result.Saved ? "Saved" : "NOT saved")}: {result.OutputPath}. ");
        sb.Append(CultureInfo.InvariantCulture, $"{result.Components.Count} components; ");
        sb.Append(CultureInfo.InvariantCulture, $"mates ok {result.Mates.Count(m => m.Status == MateStatus.Ok)}, ");
        sb.Append(CultureInfo.InvariantCulture, $"warning {result.Mates.Count(m => m.Status == MateStatus.Warning)}, ");
        sb.Append(CultureInfo.InvariantCulture, $"failed {result.Mates.Count(m => m.Status == MateStatus.Failed)}. ");
        foreach (var m in result.Mates.Where(m => m.Status != MateStatus.Ok))
            sb.Append(CultureInfo.InvariantCulture, $"[{m.Key}: {m.Status} {m.Detail}] ");
        foreach (var w in result.Warnings) sb.Append(CultureInfo.InvariantCulture, $"[warning: {w}] ");
        if (excluded > 0) sb.Append(CultureInfo.InvariantCulture, $"{excluded} mate(s) were excluded by the user and not built; do not add them back. ");
        sb.Append(CultureInfo.InvariantCulture, $"Undo in SolidWorks: Ctrl+Z on '{result.UndoLabel}', or delete the new file.");

        var structured = JsonSerializer.SerializeToNode(result, SwxJsonContext.Default.BuildResult);
        return result.Saved ? ToolResult.Ok(sb.ToString(), structured) : ToolResult.Fail(sb.ToString());
    }

    /// <summary>Validates a submitted plan against the job's roots and the cached cards, and resolves face IDs.</summary>
    private (AssemblyPlan? Plan, ResolvedAssemblyPlan? Resolved, string? Error) ParseAndResolve(ToolCallContext call)
    {
        AssemblyPlan? plan;
        try { plan = call.Arguments.Deserialize(SwxJsonContext.Default.AssemblyPlan); }
        catch (JsonException ex) { return (null, null, $"Plan is not valid: {ex.Message}"); }
        if (plan is null) return (null, null, "Plan is empty.");

        var outputVerdict = call.Job.Paths.Check(plan.OutputPath, PathAccess.CreateFile);
        if (!outputVerdict.Allowed) return (plan, null, outputVerdict.Code == "exists" ? $"{outputVerdict.FullPath} already exists; choose a new file name." : outputVerdict.Message);
        string output = outputVerdict.FullPath;
        if (!output.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)) return (plan, null, "outputPath must end in .SLDASM.");
        if (plan.TemplatePath is string t && !t.EndsWith(".asmdot", StringComparison.OrdinalIgnoreCase))
            return (plan, null, "templatePath must be an .ASMDOT file.");
        // Templates live outside the job's folders (SolidWorks' own data), but a network or device path is never
        // accepted: opening \\server\share\x.asmdot would send the user's credentials to that server.
        if (plan.TemplatePath is string tp && call.Job.Paths.Syntax.Check(tp) is string badTemplate)
            return (plan, null, $"templatePath is not acceptable: {badTemplate}");
        if (plan.Components.Count == 0) return (plan, null, "The plan has no components.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var cardsByKey = new Dictionary<string, PartCard>(StringComparer.Ordinal);
        foreach (var c in plan.Components)
        {
            if (!keys.Add(c.Key)) return (plan, null, $"Duplicate component key '{c.Key}'.");
            var partVerdict = call.Job.Paths.Check(c.PartPath, PathAccess.Read);
            if (!partVerdict.Allowed) return (plan, null, partVerdict.Message);
            string part = partVerdict.FullPath;
            if (!_cards.TryGetValue((call.Job.JobId, Norm(part)), out var card))
                return (plan, null, $"No card for {Path.GetFileName(part)}; call {PartCardsTool} on it first.");
            if (c.PositionM is { Length: not 3 }) return (plan, null, $"positionM for '{c.Key}' must have 3 numbers.");
            cardsByKey[c.Key] = card;
        }

        var mates = new List<ResolvedMate>();
        var mateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in plan.Mates ?? [])
        {
            if (!mateKeys.Add(m.Key)) return (plan, null, $"Duplicate mate key '{m.Key}'.");
            if (!cardsByKey.TryGetValue(m.ComponentA, out var ca) || !cardsByKey.TryGetValue(m.ComponentB, out var cb))
                return (plan, null, $"Mate '{m.Key}' refers to an unknown component.");
            if (m.ComponentA == m.ComponentB) return (plan, null, $"Mate '{m.Key}' must join two different components.");
            var fa = ca.Faces.FirstOrDefault(f => f.Id == m.FaceA);
            var fb = cb.Faces.FirstOrDefault(f => f.Id == m.FaceB);
            if (fa is null || fb is null) return (plan, null, $"Mate '{m.Key}' refers to a face ID that is not in the part card.");
            if (m.Type == MateKind.Concentric && (fa.Kind != FaceKind.Cylinder || fb.Kind != FaceKind.Cylinder))
                return (plan, null, $"Mate '{m.Key}': Concentric needs two cylinder faces (c..).");
            if (m.Type != MateKind.Concentric && (fa.Kind != FaceKind.Plane || fb.Kind != FaceKind.Plane))
                return (plan, null, $"Mate '{m.Key}': {m.Type} needs two plane faces (p..).");
            if (m.Type == MateKind.Distance && m.DistanceM is null)
                return (plan, null, $"Mate '{m.Key}': Distance needs distanceM.");
            mates.Add(new ResolvedMate(m, fa, fb));
        }

        var resolved = new ResolvedAssemblyPlan(output, plan.TemplatePath, plan.Components, mates);
        return (plan, resolved, null);
    }

    private static string Describe(string component, FaceInfo f)
    {
        string size = f.RadiusM is double r
            ? $"r {Math.Round(r * 1000, 3).ToString(CultureInfo.InvariantCulture)} mm"
            : $"{Math.Round(f.AreaM2 * 1e6, 1).ToString(CultureInfo.InvariantCulture)} mm²";
        string name = !f.EntityNameIsFallback && f.EntityName is { Length: > 0 } n ? $" '{n}'" : "";
        return $"{component}.{f.Id}{name} ({f.Kind.ToString().ToLowerInvariant()}, {size})";
    }

    private static JsonArray ToArray(double[] values) => new(values.Select(v => (JsonNode)v).ToArray());

    private static string Norm(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
}
