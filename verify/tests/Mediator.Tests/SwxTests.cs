extern alias worker;
using Mediator.Connectors.SolidWorks.Contracts;
using worker::Mediator.SwxWorker;
using Mediator.Tests.Fakes;
using SolidWorks.Interop.swconst;

namespace Mediator.Tests;

static class SwxTests
{
    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Two parts on disk and in the fake: a shaft (split half-cylinders, two end planes) and a housing (bore, face).</summary>
    public static (FakeSldWorks App, string Shaft, string Housing, string Dir) Scene()
    {
        string dir = TempDir();
        string template = Path.Combine(dir, "assembly.asmdot");
        File.WriteAllText(template, "template");
        string shaft = Path.Combine(dir, "shaft.sldprt");
        string housing = Path.Combine(dir, "housing.sldprt");
        File.WriteAllText(shaft, "part");
        File.WriteAllText(housing, "part");

        var app = new FakeSldWorks(template);
        app.AddPart(shaft)
            .Cylinder("half1", [0, 0, 0], [0, 0, 1], 0.005, 0.000157)
            .Cylinder("half2", [0, 0, 0.001], [0, 0, -1], 0.005, 0.000150) // same axis line, same radius: one hole
            .Plane("end0", [0, 0, -1], [0, 0, 0], 0.0000785)
            .Plane("end1", [0, 0, -1], [0, 0, 0.02], 0.0000785, sense: false) // surface normal points in; face is reversed
            .MateReference("MateRef-ShaftAxis");
        app.AddPart(housing)
            .Cylinder("bore", [0.05, 0, 0], [0, 0, 1], 0.005, 0.000314)
            .Plane("face", [0, 0, 1], [0.05, 0, 0.01], 0.0004);
        return (app, shaft, housing, dir);
    }

    public static SwxComHost Host(FakeSldWorks app, ISwxDialogDetector? dialogs = null, TimeSpan? probeDelay = null) => new(
        new SwxHostOptions { UseStaApartment = false, DialogProbeDelay = probeDelay ?? TimeSpan.FromSeconds(3), DialogProbeInterval = TimeSpan.FromMilliseconds(250) },
        new FakeConnector(app), dialogs);

    public static async Task MateSelectionAndLayouts()
    {
        var (app, shaftPath, housingPath, dir) = Scene();
        using var host = Host(app);

        // ── Extraction: array layouts, face sense, half-cylinder grouping, read-only open ──
        var batch = await host.ExtractPartCardsAsync([shaftPath, housingPath], TimeSpan.FromSeconds(30), CancellationToken.None);
        Check.Equal(0, batch.Errors.Count, "no extraction errors");
        var shaft = batch.Cards.Single(c => c.Path == shaftPath);
        var housing = batch.Cards.Single(c => c.Path == housingPath);

        Check.True(app.OpenOptions.All(o => (o & (int)swOpenDocOptions_e.swOpenDocOptions_ReadOnly) != 0
                                         && (o & (int)swOpenDocOptions_e.swOpenDocOptions_Silent) != 0), "parts opened silent and read-only");
        Check.Equal("mm", shaft.DisplayUnits, "units");
        Check.True(shaft.MateReferences.SequenceEqual(new[] { "MateRef-ShaftAxis" }), "mate references listed");

        var cyl = shaft.Faces.Where(f => f.Kind == FaceKind.Cylinder).ToList();
        Check.Equal(1, cyl.Count, "two half-cylinders of one hole grouped into one candidate");
        Check.Equal(0.005, cyl[0].RadiusM, "radius read from CylinderParams[6]");
        Check.True(cyl[0].PointM.SequenceEqual(new[] { 0.0, 0.0, 0.0 }), "origin from CylinderParams[0..2]");
        Check.True(cyl[0].DirectionUnit.SequenceEqual(new[] { 0.0, 0.0, 1.0 }), "axis from CylinderParams[3..5]");

        var planes = shaft.Faces.Where(f => f.Kind == FaceKind.Plane).ToList();
        var reversed = planes.Single(p => p.PointM[2] == 0.02);
        Check.True(reversed.DirectionUnit.SequenceEqual(new[] { 0.0, 0.0, 1.0 }), "normal from PlaneParams[0..2], flipped when FaceInSurfaceSense is false");
        Check.True(shaft.Faces.All(f => f.PersistRefBase64 is { Length: > 0 }), "persist references captured as Base64");

        // ── Build: mapped entities, mark 1, undo grouping, nothing written to parts ──
        string output = Path.Combine(dir, "out.SLDASM");
        var plan = new ResolvedAssemblyPlan(output, null,
            [new PlanComponent("shaft", shaftPath, null, [0, 0, 0], true), new PlanComponent("housing", housingPath, null, [0.05, 0, 0], false)],
            [
                new ResolvedMate(new PlanMate("axis", MateKind.Concentric, "shaft", cyl[0].Id, "housing", "c1", MateAlignment.Closest, false, null, 0.9, "same radius"),
                    cyl[0], housing.Faces.Single(f => f.Id == "c1")),
                new ResolvedMate(new PlanMate("seat", MateKind.Coincident, "shaft", reversed.Id, "housing", "p1", MateAlignment.AntiAligned, false, null, 0.7, "shoulder"),
                    reversed, housing.Faces.Single(f => f.Id == "p1")),
            ]);
        var result = await host.BuildAssemblyAsync(plan, TimeSpan.FromSeconds(30), CancellationToken.None);
        var asm = app.Assemblies.Single();

        Check.True(result.Saved && File.Exists(output), "assembly saved to a new file");
        Check.True(result.Mates.All(m => m.Status == MateStatus.Ok), "both mates created: " + string.Join("; ", result.Mates.Select(m => $"{m.Key} {m.Status} {m.Detail}")) + " / " + asm.LastRejection);
        Check.Equal(2, asm.Mates.Count, "fake recorded two mates");
        Check.Equal(0.005, asm.Mates[0].RadiusA, "concentric mate between radius-5 mm cylinders (params[6])");
        Check.Equal("Mediator_axis", result.Mates[0].FeatureName, "mate renamed for undo-by-name");
        Check.True(asm.Components.Single(c => c.Name2 == "shaft-1").Fixed, "first component fixed");
        Check.True(!asm.Components.Single(c => c.Name2 == "housing-1").Fixed, "only the base component fixed");
        Check.Equal(0, asm.FixWithNothingSelected + asm.FixWithOtherSelections, "FixComponent called with exactly the base component selected");
        Check.Equal(1, asm.UndoStarts, "one undo group started");
        Check.True(asm.UndoFinishes.SequenceEqual(new[] { SwxComHost.UndoLabel }), "undo group closed with the batch label");
        Check.Equal(false, app.CommandInProgress, "CommandInProgress restored");
        Check.Equal(0, app.Parts[shaftPath].SaveAttempts + app.Parts[housingPath].SaveAttempts, "parts never saved");

        // ── The strict mock rejects what real SolidWorks rejects ──
        var shaftComp = asm.Components[0];
        var housingComp = asm.Components[1];
        var rawShaftFace = app.Parts[shaftPath].Face("half1");
        var mappedShaft = (FakeMappedFace)shaftComp.GetCorrespondingEntity(rawShaftFace);
        var mappedBore = (FakeMappedFace)housingComp.GetCorrespondingEntity(app.Parts[housingPath].Face("bore"));
        var mark1 = new FakeSelectData { Mark = 1 };

        asm.ClearSelection2(true);
        rawShaftFace.Select4(true, mark1);
        mappedBore.Select4(true, mark1);
        var m1 = asm.AddMate5((int)swMateType_e.swMateCONCENTRIC, 2, false, 0, 0, 0, 1, 1, 0, 0, 0, false, false, 0, out int err1);
        Check.True(m1 is null && err1 == (int)swAddMateError_e.swAddMateError_IncorrectSelections, "unmapped part-space face rejected");

        asm.ClearSelection2(true);
        mappedShaft.Select4(true, null!);
        mappedBore.Select4(true, null!);
        var m2 = asm.AddMate5((int)swMateType_e.swMateCONCENTRIC, 2, false, 0, 0, 0, 1, 1, 0, 0, 0, false, false, 0, out int err2);
        Check.True(m2 is null && err2 == (int)swAddMateError_e.swAddMateError_IncorrectSelections, "mark 0 rejected");

        Check.True(shaftComp.GetCorrespondingEntity(app.Parts[housingPath].Face("bore")) is null, "a face of another part does not map into this component");
    }

    /// <summary>
    /// A part open stalls; 2 s in, SolidWorks puts up a modal dialog. At the 3 s probe the caller gets a structured
    /// MODAL_DIALOG_BLOCKING error instead of waiting for the run watchdog (which would kill the worker with exit 3).
    /// When the user answers, the stalled call finishes and the queue keeps serving.
    /// </summary>
    public static async Task ModalDialogWatchdog()
    {
        var (app, shaftPath, _, _) = Scene();
        var dialogs = new FakeDialogDetector();
        using var gate = new ManualResetEventSlim(false);
        app.OpenGate = gate;
        bool wedged = false;
        using var host = Host(app, dialogs, TimeSpan.FromSeconds(3));
        host.Wedged += _ => wedged = true;

        var started = DateTimeOffset.UtcNow;
        var call = host.ExtractPartCardsAsync([shaftPath], TimeSpan.FromSeconds(30), CancellationToken.None);
        Check.True(app.OpenEntered.Wait(TimeSpan.FromSeconds(5)), "call reached OpenDoc6 and stalled");
        dialogs.AppearAt = started.AddSeconds(2);

        SwxBlockedByModalDialogException? blocked = null;
        try { await call.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (SwxBlockedByModalDialogException ex) { blocked = ex; }
        var elapsed = DateTimeOffset.UtcNow - started;

        Check.True(blocked is not null, "caller released with SwxBlockedByModalDialogException");
        Check.Equal("MODAL_DIALOG_BLOCKING", SwxBlockedByModalDialogException.Code, "structured code");
        Check.True(blocked!.Dialog.Buttons.SequenceEqual(new[] { "Rebuild", "Don't rebuild" }), "dialog buttons reported");
        Check.True(blocked.Message.Contains("Rebuild now?", StringComparison.Ordinal), "dialog text in the message");
        Check.True(elapsed >= TimeSpan.FromSeconds(2.9) && elapsed < TimeSpan.FromSeconds(5), $"released at the 3 s probe, not before the dialog and not at the run timeout ({elapsed.TotalSeconds:F1} s)");

        // The user answers the dialog: the COM call completes in the background and the queue is usable again.
        dialogs.AppearAt = null;
        gate.Set();
        var health = await host.HealthAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(health.StartsWith("SolidWorks ", StringComparison.Ordinal), "next call succeeds: " + health);
        Check.Equal(false, wedged, "queue never wedged");

        // Control: a slow call with no dialog is NOT released early.
        var (app2, shaft2, _, _) = Scene();
        var none = new FakeDialogDetector();
        using var gate2 = new ManualResetEventSlim(false);
        app2.OpenGate = gate2;
        using var host2 = Host(app2, none, TimeSpan.FromMilliseconds(300));
        var slow = host2.ExtractPartCardsAsync([shaft2], TimeSpan.FromSeconds(30), CancellationToken.None);
        app2.OpenEntered.Wait(TimeSpan.FromSeconds(5));
        await Task.Delay(1200);
        Check.True(!slow.IsCompleted && none.Probes >= 2, "still waiting while probes find no dialog");
        gate2.Set();
        var cards = await slow.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal(1, cards.Cards.Count, "slow call completed normally");
    }

    /// <summary>STEP files from the Code-CAD generator become new .SLDPRT files (LoadFile4 + SaveAs3) that part_cards can read.</summary>
    public static async Task StepImport()
    {
        var (app, _, _, dir) = Scene();
        string hub = Path.Combine(dir, "wheel_hub.step"), disc = Path.Combine(dir, "brake_disc.step"), bad = Path.Combine(dir, "broken.step");
        foreach (var f in new[] { hub, disc, bad }) File.WriteAllText(f, "ISO-10303-21;");
        app.StepFiles[hub] = p => p.Cylinder("bore", [0, 0, 0], [0, 0, 1], 0.02, 0.003).Plane("face", [0, 0, 1], [0, 0, 0.04], 0.01);
        app.StepFiles[disc] = p => p.Cylinder("bore", [0, 0, 0], [0, 0, 1], 0.02, 0.002).Plane("face", [0, 0, -1], [0, 0, 0], 0.05);
        string outDir = Path.Combine(dir, "parts");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "brake_disc.SLDPRT"), "already here");
        using var host = Host(app);

        var batch = await host.ImportStepAsync([hub, disc, bad, Path.Combine(dir, "missing.step")], outDir, TimeSpan.FromSeconds(30), CancellationToken.None);
        var byName = batch.Results.ToDictionary(r => Path.GetFileName(r.Source));
        Check.True(byName["wheel_hub.step"].PartPath == Path.Combine(outDir, "wheel_hub.SLDPRT") && File.Exists(byName["wheel_hub.step"].PartPath!), "STEP imported and saved as a new part");
        Check.True(byName["brake_disc.step"].Error!.StartsWith("exists", StringComparison.Ordinal), "an existing .SLDPRT is never overwritten");
        Check.True(byName["broken.step"].Error!.StartsWith("import_failed", StringComparison.Ordinal), "LoadFile4 failure reported");
        Check.True(byName["missing.step"].Error!.StartsWith("not_found", StringComparison.Ordinal), "missing file reported");
        Check.True(app.ImportDataRequested.Contains(hub), "GetImportFileData passed to LoadFile4");
        Check.True(app.Closed.Contains("wheel_hub.step"), "imported document closed after saving");

        var cards = await host.ExtractPartCardsAsync([byName["wheel_hub.step"].PartPath!], TimeSpan.FromSeconds(30), CancellationToken.None);
        Check.True(cards.Cards.Single().Faces.Any(f => f.Kind == FaceKind.Cylinder && f.RadiusM == 0.02), "the imported part feeds swx_part_cards");
    }

    /// <summary>
    /// V6 ownership: the worker never saves, changes or closes a document it did not create; surface-only imports
    /// and duplicate targets are refused. (Fake COM: proves the worker's rules, not SolidWorks' behaviour — see
    /// docs/v6/INTEGRATION_MATRIX.md for the real-SolidWorks procedure.)
    /// </summary>
    public static async Task StepImportOwnership()
    {
        var (app, shaftPath, _, dir) = Scene();
        string outDir = Path.Combine(dir, "imported");
        Directory.CreateDirectory(outDir);
        string Step(string name, string? sub = null)
        {
            string folder = sub is null ? dir : Path.Combine(dir, sub);
            Directory.CreateDirectory(folder);
            string p = Path.Combine(folder, name);
            File.WriteAllText(p, "ISO-10303-21;");
            app.StepFiles[p] = d => d.Cylinder("c", [0, 0, 0], [0, 0, 1], 0.01, 0.001);
            return p;
        }
        // The user has a part open; LoadFile4 for this STEP hands that document back.
        app.Open.Add(shaftPath);
        string collides = Step("collides.step");
        app.ReturnsExisting[collides] = app.Parts[shaftPath];
        string surface = Step("surface.step");
        app.StepFiles[surface] = d => { d.Cylinder("c", [0, 0, 0], [0, 0, 1], 0.01, 0.001); d.SurfaceOnly = true; };
        string dupA = Step("bracket.step", "a"), dupB = Step("bracket.step", "b");
        string openStep = Step("open.step");
        app.Parts[openStep] = new FakePart(app, openStep);
        app.Open.Add(openStep);
        string good = Step("good.step");
        using var host = Host(app);

        var batch = await host.ImportStepAsync([collides, surface, dupA, dupB, openStep, good], outDir, TimeSpan.FromSeconds(30), CancellationToken.None);
        string E(string src) => batch.Results.Single(r => r.Source == src).Error ?? "";
        Check.True(E(collides).StartsWith("returned_existing_document", StringComparison.Ordinal), "LoadFile4 returned the user's document → reported: " + E(collides));
        Check.True(app.Open.Contains(shaftPath) && !app.Closed.Contains(app.Parts[shaftPath].GetTitle()) && app.Parts[shaftPath].SaveAttempts == 0,
            "the user's document stays open, unsaved and unclosed");
        Check.True(E(surface).StartsWith("no_solid_body", StringComparison.Ordinal) && E(surface).Contains("surface", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(outDir, "surface.SLDPRT")), "surface-only import refused and not saved");
        Check.True(E(dupA).StartsWith("duplicate_target", StringComparison.Ordinal) && E(dupB).StartsWith("duplicate_target", StringComparison.Ordinal)
            && !File.Exists(Path.Combine(outDir, "bracket.SLDPRT")), "two sources with one target name are both refused, nothing written");
        Check.True(E(openStep).StartsWith("already_open", StringComparison.Ordinal) && app.Open.Contains(openStep), "a STEP already open in SolidWorks is not touched");
        Check.True(E(good).Length == 0 && new FileInfo(Path.Combine(outDir, "good.SLDPRT")).Length > 0, "an ordinary import still works");
        Check.True(app.Imported.Count == 0, "every document the worker created was closed; nothing it did not create was");
    }

    public static async Task FaceFallbackAfterReopen()
    {
        var (app, shaftPath, housingPath, dir) = Scene();
        using var host = Host(app);
        var batch = await host.ExtractPartCardsAsync([shaftPath, housingPath], TimeSpan.FromSeconds(30), CancellationToken.None);
        var shaft = batch.Cards.Single(c => c.Path == shaftPath);
        var housing = batch.Cards.Single(c => c.Path == housingPath);

        // Simulate a reopened part whose persist references no longer resolve: geometry must still find the faces.
        app.Parts[shaftPath].PersistRefsInvalid = true;
        app.Parts[housingPath].PersistRefsInvalid = true;
        var plan = new ResolvedAssemblyPlan(Path.Combine(dir, "fallback.SLDASM"), null,
            [new PlanComponent("s", shaftPath, null, null, true), new PlanComponent("h", housingPath, null, null, false)],
            [new ResolvedMate(new PlanMate("axis", MateKind.Concentric, "s", "c1", "h", "c1", MateAlignment.Closest, false, null, null, null),
                shaft.Faces.Single(f => f.Id == "c1"), housing.Faces.Single(f => f.Id == "c1"))]);
        var result = await host.BuildAssemblyAsync(plan, TimeSpan.FromSeconds(30), CancellationToken.None);
        Check.True(result.Mates.Single().Status == MateStatus.Ok, "geometric re-match found both faces: " + result.Mates.Single().Detail);
    }

    /// <summary>
    /// V10: mates on default planes (found by feature-tree position, so a translated "Plan de face" works), and the prompts
    /// that blocked a build (save read-only references?, rebuild?, error box, mate flip) are off during it and restored after.
    /// </summary>
    public static async Task DefaultPlaneMatesAndQuietPrompts()
    {
        var (app, shaftPath, housingPath, dir) = Scene();
        app.Toggles[15] = false; app.Toggles[77] = true; app.Integers[230] = 0; // the user's own settings
        using var host = Host(app);
        var batch = await host.ExtractPartCardsAsync([shaftPath, housingPath], TimeSpan.FromSeconds(30), CancellationToken.None);
        var housing = batch.Cards.Single(c => c.Path == housingPath);
        string output = Path.Combine(dir, "Train_Master_Assembly.SLDASM");
        var plan = new ResolvedAssemblyPlan(output, null,
            [new PlanComponent("shaft", shaftPath, null, [0, 0, 0], true), new PlanComponent("housing", housingPath, null, [0.05, 0, 0], false)],
            [
                new ResolvedMate(new PlanMate("centre", MateKind.Coincident, "shaft", "right", "housing", "right", MateAlignment.Closest, false, null, 0.9, "centre"),
                    StandardPlanes.Face(2), StandardPlanes.Face(2)),
                new ResolvedMate(new PlanMate("front", MateKind.Coincident, "shaft", "front", "housing", "p1", MateAlignment.Closest, false, null, 0.9, "plane to face"),
                    StandardPlanes.Face(0), housing.Faces.Single(f => f.Id == "p1")),
            ]);
        var result = await host.BuildAssemblyAsync(plan, TimeSpan.FromSeconds(30), CancellationToken.None);
        var asm = app.Assemblies.Single();
        Check.True(result.Saved && result.Mates.All(m => m.Status == MateStatus.Ok), "plane mates built: " + string.Join("; ", result.Mates.Select(m => $"{m.Key} {m.Status} {m.Detail}")) + " / " + asm.LastRejection);
        Check.True(asm.Mates[0].A is FakeFeature { Name: "Right Plane" } && asm.Mates[1].A is FakeFeature { Name: "Plan de face" } && asm.Mates[1].B is FakeMappedFace,
            "the default planes are found by position (first, second, third RefPlane); a plane mates a face too");
        Check.True(app.Toggles[15] == false && app.Toggles[77] && app.Integers[230] == 0, "the user's prompt settings are back after the build");
    }
}
