#if !SOLIDWORKS_NOT_INSTALLED
// Compiled only when the SolidWorks interop assemblies are available (Directory.Build.targets). Without them the
// worker is built with SwxUnavailableHost instead, so the rest of the solution still builds and runs.
using System.Globalization;
using System.Runtime.InteropServices;
using Mediator.Protocol.Swx;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Mediator.SwxWorker;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────
//  SwxComHost: SolidWorks connection, part cards and assembly builds. All COM work happens on the STA queue.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class SwxComHost : ISwxHost
{
    public const string UndoLabel = "Mediator Batch";

    private const double LengthTol = 1e-6;   // 1 µm
    private const double DirTol = 1e-4;      // ~0.6° as 1 - |cos|

    private static readonly int[] DisconnectedHResults =
    [
        unchecked((int)0x80010108), // RPC_E_DISCONNECTED
        unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
        unchecked((int)0x80010007), // RPC_E_SERVER_DIED
        unchecked((int)0x80010012), // RPC_E_SERVER_DIED_DNE
        unchecked((int)0x800401FD), // CO_E_OBJNOTCONNECTED
    ];

    private readonly SwxHostOptions _options;
    private readonly ISwxConnector _connector;
    private readonly ISwxDialogDetector? _dialogs;
    private readonly DedicatedStaQueue _sta;
    private readonly HashSet<string> _docsOpenedByUs = new(StringComparer.OrdinalIgnoreCase);
    private SldWorks? _sw;
    private bool _launchedByUs;
    private int _swPid; // read by the dialog probe on a timer thread: always accessed with Volatile

    public SwxComHost(SwxHostOptions options, ISwxConnector? connector = null, ISwxDialogDetector? dialogDetector = null)
    {
        _options = options;
        _connector = connector ?? new RotSwxConnector();
        _dialogs = dialogDetector ?? (options.UseStaApartment ? new Win32DialogDetector() : null);
        _sta = options.UseStaApartment
            ? new DedicatedStaQueue("swx-sta", ApartmentState.STA,
                threadInit: () => ComMessageFilter.Register(options.BusyRetryWindow),
                threadCleanup: ComMessageFilter.Revoke)
            : new DedicatedStaQueue("swx-sta", ApartmentState.Unknown);
    }

    /// <summary>
    /// Every COM call goes through here: it runs on the STA queue and, once it has been blocked for
    /// <see cref="SwxHostOptions.DialogProbeDelay"/>, the caller is released with a structured
    /// <see cref="SwxBlockedByModalDialogException"/> if SolidWorks is showing a modal dialog.
    /// </summary>
    private Task<T> RunAsync<T>(Func<T> work, TimeSpan runTimeout, TimeSpan queueTimeout, CancellationToken ct)
    {
        StallProbe? probe = _dialogs is null ? null : new StallProbe(_options.DialogProbeDelay, _options.DialogProbeInterval, () =>
        {
            int pid = Volatile.Read(ref _swPid);
            if (pid <= 0) return null;
            var dialog = _dialogs.FindBlockingDialog(pid);
            return dialog is null ? null : new SwxBlockedByModalDialogException(dialog);
        });
        return _sta.InvokeAsync(work, runTimeout, queueTimeout, ct, probe);
    }

    public event Action<string>? Wedged
    {
        add => _sta.Wedged += value;
        remove => _sta.Wedged -= value;
    }

    // ── Async entry points (any thread) ──────────────────────────────────────────────────────────────

    public Task<string> HealthAsync(CancellationToken ct) => RunAsync(() =>
    {
        var sw = EnsureConnected();
        return $"SolidWorks {sw.RevisionNumber()} (PID {Volatile.Read(ref _swPid)}{(_launchedByUs ? ", launched by Mediator" : "")}); busy retries so far: {ComMessageFilter.RetryCount}";
    }, TimeSpan.FromSeconds(120), _options.QueueTimeout, ct);

    public Task<PartCardBatch> ExtractPartCardsAsync(IReadOnlyList<string> paths, TimeSpan runTimeout, CancellationToken ct) =>
        RunAsync(() =>
        {
            var cards = new List<PartCard>();
            var errors = new List<PathError>();
            foreach (var p in paths)
            {
                try { cards.Add(ExtractPartCard(p)); }
                catch (SwxException ex) { errors.Add(new PathError(p, ex.Code, ex.Message)); }
                catch (COMException ex) { errors.Add(new PathError(p, "com_error", $"0x{ex.HResult:X8}: {ex.Message}")); }
            }
            return new PartCardBatch(cards, errors);
        }, runTimeout, _options.QueueTimeout, ct);

    /// <summary>
    /// Imports STEP files (e.g. from the Code-CAD generator) and saves each as a new .SLDPRT in
    /// <paramref name="outputFolder"/>. Never overwrites; one failure does not stop the others.
    /// </summary>
    public Task<StepImportBatch> ImportStepAsync(IReadOnlyList<string> paths, string outputFolder, TimeSpan runTimeout, CancellationToken ct) =>
        RunAsync(() => ImportStep(paths, outputFolder), runTimeout, _options.QueueTimeout, ct);

    /// <summary>
    /// V6 ownership rules. The worker closes only a document it can prove it created: one that was not among the
    /// session's open documents before LoadFile4 (compared by COM identity and by title). If LoadFile4 hands back a
    /// document that was already open (the user's), it is reported and left exactly as it was: not saved, not closed.
    /// A STEP whose source or target is already open is refused before anything is loaded. An import with no solid
    /// body (surfaces or wireframe only), or that is not a part, is closed unsaved and reported. Targets are checked
    /// for duplicates within the batch, never overwritten, and verified on disk after saving.
    /// </summary>
    private StepImportBatch ImportStep(IReadOnlyList<string> paths, string outputFolder)
    {
        var sw = EnsureConnected();
        var results = new List<StepImportResult>();
        var planned = paths.Select(raw => (Source: Path.GetFullPath(raw),
            Target: Path.Combine(Path.GetFullPath(outputFolder), Path.GetFileNameWithoutExtension(raw) + ".SLDPRT"))).ToList();
        var clashes = planned.GroupBy(p => p.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Source).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in planned)
        {
            if (clashes.TryGetValue(target, out var others))
            {
                results.Add(new StepImportResult(source, null,
                    $"duplicate_target: {string.Join(" and ", others.Select(Path.GetFileName))} would both become {Path.GetFileName(target)}; rename one and import again"));
                continue;
            }
            if (!File.Exists(source)) { results.Add(new StepImportResult(source, null, "not_found: file does not exist")); continue; }
            if (File.Exists(target)) { results.Add(new StepImportResult(source, null, $"exists: {target} already exists; not overwritten")); continue; }
            if (sw.GetOpenDocumentByName(source) is ModelDoc2) { results.Add(new StepImportResult(source, null, "already_open: the STEP file is open in SolidWorks; close it and import again")); continue; }
            if (sw.GetOpenDocumentByName(target) is ModelDoc2) { results.Add(new StepImportResult(source, null, $"target_open: a document named {Path.GetFileName(target)} is open in SolidWorks; not touched")); continue; }

            var before = OpenDocuments(sw);
            ModelDoc2? doc = null;
            bool ours = false;
            try
            {
                int errors = 0;
                object importData = sw.GetImportFileData(source);
                doc = sw.LoadFile4(source, "r", importData, ref errors);
                if (doc is null) { results.Add(new StepImportResult(source, null, $"import_failed: LoadFile4 errors={errors}")); continue; }
                string title = doc.GetTitle();
                ours = !before.Any(b => ReferenceEquals(b.Doc, doc) || string.Equals(b.Title, title, StringComparison.OrdinalIgnoreCase));
                if (!ours)
                {
                    results.Add(new StepImportResult(source, null,
                        $"returned_existing_document: SolidWorks returned '{title}', which was already open; it was not saved, changed or closed"));
                    continue;
                }
                _docsOpenedByUs.Add(title);
                if (doc.GetType() != (int)swDocumentTypes_e.swDocPART)
                {
                    results.Add(new StepImportResult(source, null, "not_a_part: the STEP imported as an assembly or drawing; import its parts separately"));
                    continue;
                }
                int solids = ToObjects(((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, false)).Count();
                if (solids == 0)
                {
                    int sheets = ToObjects(((PartDoc)doc).GetBodies2((int)swBodyType_e.swSheetBody, false)).Count();
                    results.Add(new StepImportResult(source, null, sheets > 0
                        ? $"no_solid_body: the STEP produced {sheets} surface body(ies) and no solid; not saved"
                        : "no_solid_body: the STEP produced no bodies; not saved"));
                    continue;
                }
                int saveErrors = 0, saveWarnings = 0;
                bool saved = doc.Extension.SaveAs3(target, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null!, null!, ref saveErrors, ref saveWarnings);
                var written = new FileInfo(target);
                results.Add(saved && written.Exists && written.Length > 0
                    ? new StepImportResult(source, target, null)
                    : new StepImportResult(source, null, saved
                        ? $"save_failed: SaveAs3 reported success but {target} is missing or empty"
                        : $"save_failed: errors={saveErrors}, warnings={saveWarnings}"));
            }
            catch (COMException ex)
            {
                results.Add(new StepImportResult(source, null, $"com_error: 0x{ex.HResult:X8} {ex.Message}"));
            }
            finally
            {
                if (doc is not null && ours)
                {
                    string title = doc.GetTitle();
                    try { sw.CloseDoc(title); } catch (COMException) { }
                    _docsOpenedByUs.Remove(title);
                }
            }
        }
        return new StepImportBatch(results);
    }

    private static List<(ModelDoc2 Doc, string Title)> OpenDocuments(SldWorks sw)
    {
        var list = new List<(ModelDoc2, string)>();
        foreach (var o in ToObjects(sw.GetDocuments()))
            if (o is ModelDoc2 d)
            {
                string t;
                try { t = d.GetTitle(); } catch (COMException) { t = ""; }
                list.Add((d, t));
            }
        return list;
    }

    public Task<BuildResult> BuildAssemblyAsync(ResolvedAssemblyPlan plan, TimeSpan runTimeout, CancellationToken ct) =>
        RunAsync(() => BuildAssembly(plan), runTimeout, _options.QueueTimeout, ct);

    /// <summary>Closes parts the Mediator opened (never the user's own documents) and, if we launched SolidWorks and nothing is left open, exits it.</summary>
    public Task ShutdownAsync(TimeSpan timeout) => _sta.InvokeAsync(() =>
    {
        if (_sw is null) return;
        foreach (var path in _docsOpenedByUs)
        {
            try { _sw.CloseDoc(path); } catch (COMException) { /* already closed or still referenced by an open assembly */ }
        }
        _docsOpenedByUs.Clear();
        try
        {
            if (_launchedByUs && _sw.GetDocumentCount() == 0) _sw.ExitApp();
        }
        catch (COMException) { }
        ReleaseSolidWorks();
    }, timeout, timeout, CancellationToken.None);

    // ── STA-thread operations ────────────────────────────────────────────────────────────────────────

    /// <summary>Extracts a compact card for one part. Must run on the STA queue; use <see cref="ExtractPartCardsAsync"/> from elsewhere.</summary>
    public PartCard ExtractPartCard(string sldprtPath)
    {
        AssertOnSta();
        string path = Path.GetFullPath(sldprtPath);
        if (!path.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase))
            throw new SwxException("not_a_part", $"{Path.GetFileName(path)} is not a .SLDPRT file.");
        if (!File.Exists(path))
            throw new SwxException("not_found", $"{path} does not exist.");

        var sw = EnsureConnected();
        var model = OpenReadOnly(sw, path, swDocumentTypes_e.swDocPART);
        var part = (PartDoc)model;
        var warnings = new List<string>();

        string units = ReadDisplayUnits(model, warnings);
        double[] box = part.GetPartBox(true) as double[] ?? new double[6];

        double? mass = null;
        try { mass = Round(model.Extension.CreateMassProperty()?.Mass ?? double.NaN); }
        catch (COMException ex) { warnings.Add($"mass unavailable (0x{ex.HResult:X8})"); }
        if (mass is double m && double.IsNaN(m)) mass = null;

        string[] configs = ToStrings(model.GetConfigurationNames());
        string[] mateRefs = FindMateReferences(model);
        var faces = CollectCandidateFaces(model, part, warnings);

        return new PartCard(path, units, box.Select(Round).ToArray(), mass, configs, mateRefs, faces, warnings);
    }

    /// <summary>Builds a new assembly from a resolved plan as one undoable batch. Must run on the STA queue.</summary>
    public BuildResult BuildAssembly(ResolvedAssemblyPlan plan)
    {
        AssertOnSta();
        string output = Path.GetFullPath(plan.OutputPath);
        if (!output.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase))
            throw new SwxException("bad_output", "The output file must end in .SLDASM.");
        if (File.Exists(output))
            throw new SwxException("output_exists", $"{output} already exists; the Mediator never overwrites files.");
        if (!Directory.Exists(Path.GetDirectoryName(output)))
            throw new SwxException("bad_output", $"Folder {Path.GetDirectoryName(output)} does not exist.");

        var sw = EnsureConnected();
        string template = plan.TemplatePath
            ?? sw.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
        if (string.IsNullOrWhiteSpace(template) || !File.Exists(template))
            throw new SwxException("no_template", "No assembly template found. Set a default assembly template in SolidWorks (Tools > Options > Default Templates) or pass templatePath.");

        // AddComponent5 needs each part loaded in memory.
        foreach (var c in plan.Components)
            OpenReadOnly(sw, Path.GetFullPath(c.PartPath), swDocumentTypes_e.swDocPART);

        var asmModel = sw.NewDocument(template, 0, 0, 0) as ModelDoc2
            ?? throw new SwxException("new_document_failed", "SolidWorks did not create a new assembly document.");
        var asm = (AssemblyDoc)asmModel;
        var ext = asmModel.Extension;
        var warnings = new List<string>();
        var components = new List<ComponentResult>();
        var mates = new List<MateResult>();

        using var quiet = new QuietPrompts(sw);
        bool previousCommandInProgress = sw.CommandInProgress;
        var view = asmModel.ActiveView as ModelView;
        bool? previousGraphics = view?.EnableGraphicsUpdate;
        var featureManager = asmModel.FeatureManager;
        bool previousTree = featureManager.EnableFeatureTree;
        bool recordingUndo = false;
        bool succeeded = false;

        try
        {
            sw.CommandInProgress = true;               // batch mode for out-of-process callers
            if (view is not null) view.EnableGraphicsUpdate = false;
            featureManager.EnableFeatureTree = false;
            // The real interop declares StartRecordingUndoObject() as void: success is "it did not throw".
            try { ext.StartRecordingUndoObject(); recordingUndo = true; }
            catch (System.Runtime.InteropServices.COMException) { recordingUndo = false; }
            if (!recordingUndo) warnings.Add("SolidWorks did not start an undo group; Ctrl+Z will undo steps one by one.");

            var placed = new Dictionary<string, Component2>(StringComparer.Ordinal);
            foreach (var c in plan.Components)
            {
                double[] pos = c.PositionM is { Length: 3 } p ? p : [0, 0, 0];
                var comp = asm.AddComponent5(Path.GetFullPath(c.PartPath),
                    (int)swAddComponentConfigOptions_e.swAddComponentConfigOptions_CurrentSelectedConfig,
                    "", c.Configuration is not null, c.Configuration ?? "", pos[0], pos[1], pos[2])
                    ?? throw new SwxException("insert_failed", $"Could not insert {Path.GetFileName(c.PartPath)} (component '{c.Key}').");
                placed[c.Key] = comp;

                if (c.Fixed)
                {
                    // FixComponent acts on the current selection: select exactly this component, fix it, clear.
                    asmModel.ClearSelection2(true);
                    if (comp.Select4(false, null!, false)) asm.FixComponent();
                    else warnings.Add($"Could not select component '{c.Key}' to fix it.");
                    asmModel.ClearSelection2(true);
                }
                components.Add(new ComponentResult(c.Key, comp.Name2, c.Fixed));
            }

            foreach (var rm in plan.Mates)
                mates.Add(AddMate(asmModel, asm, placed, rm));

            asmModel.ClearSelection2(true);
            asmModel.ForceRebuild3(false);
            mates = ApplyRebuildErrors(asmModel, mates);
            succeeded = true;
        }
        finally
        {
            if (recordingUndo)
            {
                try { ext.FinishRecordingUndoObject2(UndoLabel, false); }
                catch (COMException ex) { warnings.Add($"Closing the undo group failed (0x{ex.HResult:X8})."); }
            }
            try
            {
                featureManager.EnableFeatureTree = previousTree;
                if (view is not null && previousGraphics is bool g) view.EnableGraphicsUpdate = g;
            }
            catch (COMException) { /* document may already be gone */ }
            sw.CommandInProgress = previousCommandInProgress;

            if (!succeeded)
            {
                // Leave nothing half-built behind: the new document was never saved, parts were opened read-only.
                try { sw.CloseDoc(asmModel.GetTitle()); } catch (COMException) { }
            }
        }

        int saveErrors = 0, saveWarnings = 0;
        bool saved = ext.SaveAs3(output, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
            (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null!, null!, ref saveErrors, ref saveWarnings);
        if (!saved)
            warnings.Add($"Save failed (errors={saveErrors}, warnings={saveWarnings}). The assembly is still open in SolidWorks, unsaved.");
        else if (saveWarnings != 0)
            warnings.Add($"Saved with warnings={saveWarnings}.");

        return new BuildResult(output, saved, UndoLabel, components, mates, warnings);
    }

    // ── Mates ─────────────────────────────────────────────────────────────────────────────────────────

    private MateResult AddMate(ModelDoc2 asmModel, AssemblyDoc asm, IReadOnlyDictionary<string, Component2> placed, ResolvedMate rm)
    {
        var m = rm.Mate;
        if (!placed.TryGetValue(m.ComponentA, out var compA) || !placed.TryGetValue(m.ComponentB, out var compB))
            return new MateResult(m.Key, MateStatus.Failed, null, "Unknown component key.");
        if (m.Type == MateKind.Concentric && (rm.FaceA.Kind != FaceKind.Cylinder || rm.FaceB.Kind != FaceKind.Cylinder))
            return new MateResult(m.Key, MateStatus.Failed, null, "Concentric needs two cylindrical faces.");
        if (m.Type is MateKind.Coincident or MateKind.Parallel or MateKind.Distance && (rm.FaceA.Kind != FaceKind.Plane || rm.FaceB.Kind != FaceKind.Plane))
            return new MateResult(m.Key, MateStatus.Failed, null, $"{m.Type} is only planned between planar faces.");
        if (m.Type == MateKind.Distance && m.DistanceM is null)
            return new MateResult(m.Key, MateStatus.Failed, null, "Distance mate needs distanceM.");

        object entityA, entityB;
        try
        {
            entityA = ResolveInAssembly(compA, rm.FaceA);
            entityB = ResolveInAssembly(compB, rm.FaceB);
        }
        catch (SwxException ex)
        {
            return new MateResult(m.Key, MateStatus.Failed, null, $"{ex.Code}: {ex.Message}");
        }

        // AddMate5 reads its two entities from the assembly's selection, with selection mark 1. They must be
        // the assembly-context entities from GetCorrespondingEntity: selecting raw part faces gives
        // swAddMateError_IncorrectSelections.
        asmModel.ClearSelection2(true);
        var selectData = CreateMateSelectData(asmModel);
        if (!SelectForMate(entityA, selectData) || !SelectForMate(entityB, selectData))
        {
            asmModel.ClearSelection2(true);
            return new MateResult(m.Key, MateStatus.Failed, null, "Could not select both faces.");
        }

        double distance = m.DistanceM ?? 0;
        var mate = asm.AddMate5((int)ToSwMateType(m.Type), (int)ToSwAlign(m.Alignment), m.Flip,
            distance, distance, distance, 1, 1, 0, 0, 0, false, false,
            (int)swMateWidthOptions_e.swMateWidth_Centered, out int status);
        asmModel.ClearSelection2(true);

        if (mate is null || status != (int)swAddMateError_e.swAddMateError_NoError)
            return new MateResult(m.Key, MateStatus.Failed, null, $"AddMate5 returned {(swAddMateError_e)status}.");

        // Give the mate a predictable name so "undo this one mate" can delete it by name.
        string? featureName = null;
        if (NewestMateFeature(asmModel) is Feature feat)
        {
            string wanted = $"Mediator_{SanitizeName(m.Key)}";
            try { feat.Name = wanted; featureName = feat.Name; }
            catch (COMException) { featureName = feat.Name; }
        }
        return new MateResult(m.Key, MateStatus.Ok, featureName, null);
    }

    private static SelectData CreateMateSelectData(ModelDoc2 model)
    {
        if (model.SelectionManager is not SelectionMgr manager || manager.CreateSelectData() is not SelectData data)
            throw new SwxException("selection_unavailable", "SolidWorks did not provide selection data for the mate.");
        data.Mark = 1;
        return data;
    }

    /// <summary>
    /// Maps a part-space face to the placed component's assembly-space entity (IComponent2.GetCorrespondingEntity).
    /// Newer API versions also offer IComponent2.GetCorresponding; either returns the proxy AddMate5 needs.
    /// </summary>
    private object ResolveInAssembly(Component2 component, FaceInfo face)
    {
        var partModel = component.GetModelDoc2() as ModelDoc2
            ?? throw new SwxException("component_not_resolved", $"Component {component.Name2} is lightweight or suppressed.");
        if (StandardPlanes.IsStandard(face)) return StandardPlaneInAssembly(component, partModel, StandardPlanes.IndexOf(face.Id));
        var partFace = ResolveFace(partModel, face);
        return component.GetCorrespondingEntity(partFace)
            ?? throw new SwxException("no_assembly_entity", $"Could not map face {face.Id} into component {component.Name2}.");
    }

    /// <summary>
    /// Re-finds a face in a part: persist reference first, then entity name, then geometry. The geometric
    /// fallback covers parts that were reopened (new persist IDs) or whose fallback name was lost.
    /// </summary>
    internal Face2 ResolveFace(ModelDoc2 partModel, FaceInfo face)
    {
        if (face.PersistRefBase64 is { Length: > 0 } b64)
        {
            byte[] bytes = Convert.FromBase64String(b64);
            object? obj = partModel.Extension.GetObjectByPersistReference3(bytes, out int state);
            if (state == (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok && obj is Face2 byRef)
                return byRef;
        }
        if (face.EntityName is { Length: > 0 } name && partModel is PartDoc pd && pd.GetEntityByName(name, (int)swSelectType_e.swSelFACES) is Face2 byName)
            return byName;

        var matches = EnumerateFaces((PartDoc)partModel).Where(c => GeometryMatches(c, face)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Face,
            0 => throw new SwxException("face_not_found", $"Face {face.Id} no longer exists in {partModel.GetTitle()} (the part changed since its card was made)."),
            _ => throw new SwxException("face_ambiguous", $"Face {face.Id} matches {matches.Count} faces in {partModel.GetTitle()}; regenerate the part card."),
        };
    }

    private static List<MateResult> ApplyRebuildErrors(ModelDoc2 asmModel, List<MateResult> mates)
    {
        var errors = new Dictionary<string, (int Code, bool Warning)>(StringComparer.Ordinal);
        foreach (var f in MateFeatures(asmModel))
        {
            int code = f.GetErrorCode2(out bool isWarning);
            if (code != 0) errors[f.Name] = (code, isWarning);
        }
        return mates.Select(r =>
            r.FeatureName is string n && errors.TryGetValue(n, out var e)
                ? r with { Status = e.Warning ? MateStatus.Warning : MateStatus.Failed, Detail = $"Rebuild error code {e.Code} (swFeatureError_e)" }
                : r).ToList();
    }

    private static IEnumerable<Feature> MateFeatures(ModelDoc2 model)
    {
        for (var f = model.FirstFeature() as Feature; f is not null; f = f.GetNextFeature() as Feature)
        {
            if (f.GetTypeName2() != "MateGroup") continue;
            for (var sub = f.GetFirstSubFeature() as Feature; sub is not null; sub = sub.GetNextSubFeature() as Feature)
                yield return sub;
        }
    }

    private static Feature? NewestMateFeature(ModelDoc2 model) => MateFeatures(model).LastOrDefault();

    private static swMateType_e ToSwMateType(MateKind k) => k switch
    {
        MateKind.Coincident => swMateType_e.swMateCOINCIDENT,
        MateKind.Concentric => swMateType_e.swMateCONCENTRIC,
        MateKind.Parallel => swMateType_e.swMatePARALLEL,
        MateKind.Distance => swMateType_e.swMateDISTANCE,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    private static swMateAlign_e ToSwAlign(MateAlignment a) => a switch
    {
        MateAlignment.Aligned => swMateAlign_e.swMateAlignALIGNED,
        MateAlignment.AntiAligned => swMateAlign_e.swMateAlignANTI_ALIGNED,
        _ => swMateAlign_e.swMateAlignCLOSEST,
    };

    // ── Faces ─────────────────────────────────────────────────────────────────────────────────────────

    private readonly record struct CandidateFace(Face2 Face, FaceKind Kind, double Area, double[] Point, double[] Dir, double? Radius);

    private static IEnumerable<CandidateFace> EnumerateFaces(PartDoc part)
    {
        foreach (var bodyObj in ToObjects(part.GetBodies2((int)swBodyType_e.swSolidBody, true)))
        {
            if (bodyObj is not Body2 body) continue;
            foreach (var faceObj in ToObjects(body.GetFaces()))
            {
                if (faceObj is not Face2 face || face.GetSurface() is not Surface surface) continue;
                if (surface.IsCylinder() && surface.CylinderParams is double[] c && c.Length >= 7)
                {
                    yield return new CandidateFace(face, FaceKind.Cylinder, face.GetArea(), [c[0], c[1], c[2]], Normalize([c[3], c[4], c[5]]), c[6]);
                }
                else if (surface.IsPlane() && surface.PlaneParams is double[] pl && pl.Length >= 6)
                {
                    var n = Normalize([pl[0], pl[1], pl[2]]);
                    if (!face.FaceInSurfaceSense()) n = [-n[0], -n[1], -n[2]]; // outward normal of the face, not the surface
                    yield return new CandidateFace(face, FaceKind.Plane, face.GetArea(), [pl[3], pl[4], pl[5]], n, null);
                }
            }
        }
    }

    private List<FaceInfo> CollectCandidateFaces(ModelDoc2 model, PartDoc part, List<string> warnings)
    {
        var all = EnumerateFaces(part).ToList();

        // SolidWorks often splits one hole into two half-cylinders: group coaxial, equal-radius faces,
        // rank the group by total area, and keep its largest face as the representative.
        var cylinders = new List<(CandidateFace Rep, double TotalArea)>();
        foreach (var c in all.Where(f => f.Kind == FaceKind.Cylinder).OrderByDescending(f => f.Area))
        {
            int i = cylinders.FindIndex(g => SameCylinder(g.Rep, c));
            if (i < 0) cylinders.Add((c, c.Area));
            else cylinders[i] = (cylinders[i].Rep, cylinders[i].TotalArea + c.Area);
        }

        var picked = cylinders.OrderByDescending(g => g.TotalArea).Take(_options.MaxCylinderFaces).Select(g => g.Rep)
            .Concat(all.Where(f => f.Kind == FaceKind.Plane).OrderByDescending(f => f.Area).Take(_options.MaxPlanarFaces))
            .ToList();

        var result = new List<FaceInfo>(picked.Count);
        int ci = 0, pi = 0;
        foreach (var f in picked)
        {
            string id = f.Kind == FaceKind.Cylinder ? $"c{++ci}" : $"p{++pi}";
            string? persist = null;
            try
            {
                if (model.Extension.GetPersistReference3(f.Face) is byte[] bytes && bytes.Length > 0)
                    persist = Convert.ToBase64String(bytes);
            }
            catch (COMException ex) { warnings.Add($"{id}: no persist reference (0x{ex.HResult:X8})"); }

            string? name = null;
            bool fallback = false;
            try { name = part.GetEntityName(f.Face) is { Length: > 0 } existing ? existing : null; }
            catch (COMException) { }

            if (persist is null && name is null)
            {
                // Last resort: name the face in memory. The part is open read-only and is never saved, so the
                // name lives only for this SolidWorks session; the geometric fallback covers anything later.
                string candidate = $"mediator_{id}_{System.Environment.TickCount64 % 1_000_000}";
                if (part.SetEntityName(f.Face, candidate))
                {
                    name = candidate;
                    fallback = true;
                    warnings.Add($"{id}: named '{candidate}' in memory only (no persist reference).");
                }
                else
                {
                    warnings.Add($"{id}: no persist reference and naming failed; will re-find by geometry.");
                }
            }

            result.Add(new FaceInfo(id, f.Kind, Round(f.Area), f.Point.Select(Round).ToArray(), f.Dir.Select(RoundDir).ToArray(),
                f.Radius is double r ? Round(r) : null, persist, name, fallback));
        }
        return result;
    }

    private static bool SameCylinder(CandidateFace a, CandidateFace b)
    {
        if (a.Radius is not double ra || b.Radius is not double rb || Math.Abs(ra - rb) > LengthTol) return false;
        if (1 - Math.Abs(Dot(a.Dir, b.Dir)) > DirTol) return false;
        return DistanceToLine(b.Point, a.Point, a.Dir) <= LengthTol;
    }

    private static bool GeometryMatches(CandidateFace c, FaceInfo f)
    {
        if (c.Kind != f.Kind) return false;
        if (Math.Abs(c.Area - f.AreaM2) > Math.Max(1e-9, 1e-4 * f.AreaM2)) return false;
        if (f.Kind == FaceKind.Cylinder)
        {
            if (c.Radius is not double r || f.RadiusM is not double fr || Math.Abs(r - fr) > LengthTol) return false;
            if (1 - Math.Abs(Dot(c.Dir, f.DirectionUnit)) > DirTol) return false;
            return DistanceToLine(f.PointM, c.Point, c.Dir) <= 10 * LengthTol;
        }
        if (1 - Dot(c.Dir, f.DirectionUnit) > DirTol) return false; // planes: same outward side
        return Math.Abs(Dot(c.Dir, Sub(f.PointM, c.Point))) <= 10 * LengthTol;
    }

    // ── Connection management ─────────────────────────────────────────────────────────────────────────

    private SldWorks EnsureConnected()
    {
        if (_sw is not null)
        {
            try
            {
                _ = _sw.RevisionNumber();
                return _sw;
            }
            catch (COMException ex) when (DisconnectedHResults.Contains(ex.HResult))
            {
                ReleaseSolidWorks(); // the user closed or SolidWorks crashed since the last call
            }
            catch (InvalidComObjectException)
            {
                ReleaseSolidWorks();
            }
        }

        var connection = _connector.Connect(_options);
        _sw = connection.App;
        _launchedByUs = connection.LaunchedByMediator;
        Volatile.Write(ref _swPid, SafeProcessId(connection.App));
        return _sw;
    }

    private static int SafeProcessId(SldWorks sw)
    {
        try { return sw.GetProcessID(); }
        catch (COMException) { return 0; }
    }

    private void ReleaseSolidWorks()
    {
        if (_sw is not null)
        {
            // Fakes used in offline tests are not COM objects; only release real RCWs.
            if (Marshal.IsComObject(_sw))
            {
                try { Marshal.FinalReleaseComObject(_sw); } catch (ArgumentException) { }
            }
            _sw = null;
            Volatile.Write(ref _swPid, 0);
        }
        _docsOpenedByUs.Clear();
    }

    private ModelDoc2 OpenReadOnly(SldWorks sw, string path, swDocumentTypes_e type)
    {
        if (sw.GetOpenDocumentByName(path) is ModelDoc2 alreadyOpen)
            return alreadyOpen; // the user's document: reuse it, never close it
        int errors = 0, warnings = 0;
        var model = sw.OpenDoc6(path, (int)type,
            (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
            "", ref errors, ref warnings)
            ?? throw new SwxException("open_failed", $"SolidWorks could not open {Path.GetFileName(path)} (swFileLoadError_e={errors}, warnings={warnings}).");
        _docsOpenedByUs.Add(path);
        return model;
    }

    private void AssertOnSta()
    {
        if (!_sta.IsOnQueueThread)
            throw new InvalidOperationException("SolidWorks COM calls must run on the dedicated STA thread; use the *Async methods.");
    }

    public void Dispose() => _sta.Dispose();

    // ── Small helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string ReadDisplayUnits(ModelDoc2 model, List<string> warnings)
    {
        try
        {
            int v = model.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitsLinear,
                (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
            return Enum.IsDefined(typeof(swLengthUnit_e), v) ? (swLengthUnit_e)v switch
            {
                swLengthUnit_e.swMM => "mm",
                swLengthUnit_e.swCM => "cm",
                swLengthUnit_e.swMETER => "m",
                swLengthUnit_e.swINCHES => "in",
                swLengthUnit_e.swFEET => "ft",
                swLengthUnit_e.swFEETINCHES => "ft-in",
                swLengthUnit_e.swMICRON => "um",
                swLengthUnit_e.swMIL => "mil",
                var other => other.ToString(),
            } : v.ToString(CultureInfo.InvariantCulture);
        }
        catch (COMException ex)
        {
            warnings.Add($"units unavailable (0x{ex.HResult:X8})");
            return "unknown";
        }
    }

    private static string[] FindMateReferences(ModelDoc2 model)
    {
        var names = new List<string>();
        for (var f = model.FirstFeature() as Feature; f is not null; f = f.GetNextFeature() as Feature)
        {
            if (f.GetTypeName2() == "MateReference") names.Add(f.Name);
            for (var sub = f.GetFirstSubFeature() as Feature; sub is not null; sub = sub.GetNextSubFeature() as Feature)
                if (sub.GetTypeName2() == "MateReference") names.Add(sub.Name);
        }
        return names.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static object[] ToObjects(object? value) => value switch
    {
        object[] arr => arr,
        Array a => a.Cast<object>().ToArray(),
        null => [],
        var single => [single],
    };

    private static string[] ToStrings(object? value) => ToObjects(value).OfType<string>().ToArray();

    private static string SanitizeName(string key) => new(key.Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-').Take(48).ToArray());

    private static double Round(double v) => Math.Round(v, 7);     // 0.1 µm, keeps cards short
    private static double RoundDir(double v) => Math.Round(v, 6);
    private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Normalize(double[] v)
    {
        double len = Math.Sqrt(Dot(v, v));
        return len < 1e-12 ? v : [v[0] / len, v[1] / len, v[2] / len];
    }

    private static double DistanceToLine(double[] point, double[] linePoint, double[] lineDir)
    {
        var d = Sub(point, linePoint);
        double t = Dot(d, lineDir);
        double[] perp = [d[0] - t * lineDir[0], d[1] - t * lineDir[1], d[2] - t * lineDir[2]];
        return Math.Sqrt(Dot(perp, perp));
    }

    /// <summary>Selects a mate entity with mark 1: a face (Entity) through the select data, a default plane (Feature) by mark.</summary>
    private static bool SelectForMate(object entity, SelectData data) => entity switch
    {
        Feature plane => plane.Select2(true, 1),
        Entity e => e.Select4(true, data),
        _ => false,
    };

    /// <summary>
    /// A component's default plane (Front, Top, Right) in the assembly: the part's first three reference planes in feature
    /// tree order (so renamed or translated planes still work), then the component's own copy of that feature by name.
    /// </summary>
    internal static Feature StandardPlaneInAssembly(Component2 component, ModelDoc2 partModel, int index)
    {
        int seen = 0;
        for (var f = partModel.FirstFeature() as Feature; f is not null; f = f.GetNextFeature() as Feature)
        {
            if (f.GetTypeName2() != "RefPlane") continue;
            if (seen++ != index) continue;
            return component.FeatureByName(f.Name) as Feature
                ?? throw new SwxException("no_assembly_entity", $"Could not find plane '{f.Name}' of component {component.Name2} in the assembly.");
        }
        throw new SwxException("plane_not_found", $"{partModel.GetTitle()} has no default plane #{index + 1} ({StandardPlanes.Ids[index]}).");
    }

    /// <summary>
    /// Stops the SolidWorks questions that block an automated build, for its duration only (the user's settings come back
    /// after): "save modified read-only referenced documents?" (the Yes/No seen after an assembly save), "rebuild the
    /// models?", the rebuild error box and the mate flip prompt. ISldWorks.CloseDoc has no "silent" option, and
    /// UserControl = false on the user's own session does not stop dialogs (it hands SolidWorks' lifetime to us), so
    /// prompts are prevented at their source instead.
    /// </summary>
    internal sealed class QuietPrompts : IDisposable
    {
        private const int ExtRefNoPromptOrSave = 15, ShowErrorsEveryRebuild = 77, PromptForAutoMateFlip = 368; // swUserPreferenceToggle_e
        private const int RebuildOnActivation = 230, RebuildActiveDoc = 2;                                    // swUserPreferenceIntegerValue_e
        private readonly SldWorks _sw;
        private readonly List<(int Id, bool Value)> _toggles = [];
        private readonly int? _rebuild;

        public QuietPrompts(SldWorks sw)
        {
            _sw = sw;
            Set(ExtRefNoPromptOrSave, true);
            Set(ShowErrorsEveryRebuild, false);
            Set(PromptForAutoMateFlip, false);
            try { _rebuild = sw.GetUserPreferenceIntegerValue(RebuildOnActivation); sw.SetUserPreferenceIntegerValue(RebuildOnActivation, RebuildActiveDoc); }
            catch (COMException) { _rebuild = null; }
        }

        private void Set(int id, bool value)
        {
            try { _toggles.Add((id, _sw.GetUserPreferenceToggle(id))); _sw.SetUserPreferenceToggle(id, value); }
            catch (COMException) { }
        }

        public void Dispose()
        {
            foreach (var (id, value) in _toggles) try { _sw.SetUserPreferenceToggle(id, value); } catch (COMException) { }
            if (_rebuild is int r) try { _sw.SetUserPreferenceIntegerValue(RebuildOnActivation, r); } catch (COMException) { }
        }
    }
}
#endif
