using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Files;
using Mediator.Core.Jobs;
using Mediator.Protocol;

// Helper modes used by tests that need a stand-in child process (a "terminal" that stays up, or a CLI that prints a stream).
if (args.Length == 2 && args[0] == "--sleep") { await Task.Delay(TimeSpan.FromSeconds(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture))); return 0; }
if (args.Length >= 2 && args[0] == "--emit")
{
    // --emit <stdout file> [exit=<code>] [gate=<path: wait until it exists>] [stderr=<file>]
    await Console.In.ReadToEndAsync(); // consume the prompt like a real CLI
    string? Opt(string k) => args.Skip(2).FirstOrDefault(x => x.StartsWith(k + "=", StringComparison.Ordinal))?[(k.Length + 1)..];
    if (Opt("gate") is string gate) while (!File.Exists(gate)) await Task.Delay(20);
    if (Opt("stderr") is string err) { using var e = Console.OpenStandardError(); await e.WriteAsync(File.ReadAllBytes(err)); }
    using (var stdout = Console.OpenStandardOutput()) await stdout.WriteAsync(File.ReadAllBytes(args[1]));
    return Opt("exit") is string code ? int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) : 0;
}

if (args.Length == 2 && args[0] == "--write-fcpxml")
{
    static Mediator.Core.Resolve.ClipInfo V(string id, string name, double dur, bool video = true) =>
        new(id, Path.Combine(Path.GetTempPath(), name), dur, video ? new Mediator.Core.Resolve.FrameRate(30000, 1001) : null, video ? 1920 : null, video ? 1080 : null, video, 1, 2, 48000, null, "t");
    var plan = new Mediator.Core.Resolve.TimelinePlan("DTD check", new Mediator.Core.Resolve.FrameRate(30000, 1001), 1920, 1080,
    [
        new("k1", V("c1", "A clip.mp4", 10), 1.0, 3.5),
        new("k3", V("c3", "C.mp4", 12), 2, 6, Transition: Mediator.Core.Resolve.CutTransition.Dissolve),
        new("k4", V("c4", "music.wav", 30, video: false), 0, 4, Track: 2, TimelineSec: 0.5),
        new("k5", V("c5", "broll.mov", 8), 1, 2, Track: 2, TimelineSec: 3.0),
    ]);
    File.WriteAllText(args[1], Mediator.Core.Resolve.TimelineWriter.Fcpxml(plan));
    return 0;
}
if (args.Length >= 1 && args[0] == "--fake-mcp") return await Mediator.Tests.ConnectorTests.FakeMcpServerAsync(args.Skip(1).ToArray());
if (args.Length == 3 && args[0] == "--bench") return await Mediator.Tests.Benchmarks.RunAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
if (args.Length == 2 && args[0] == "--audit-child") return await Mediator.Tests.AuditTests.ChildAppend(args[1]);
if (args.Length == 4 && args[0] == "--pdf-child") return Mediator.Tests.PdfHardeningTests.Child(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
if (args.Length == 5 && args[0] == "--ops-core") return await Mediator.Tests.OperationTests.CoreChildAsync(args[1], args[2], args[3], args[4]);
if (args.Length == 6 && args[0] == "--journal-child") return await Mediator.Tests.JournalFaultTests.ChildAsync(args);
if (args.Length == 2 && args[0] == "--test-pdf") { File.WriteAllBytes(args[1], Mediator.Tests.TestPdf.Build()); return 0; }
if (args.Length == 4 && args[0] == "--pdf-text")
{
    var doc = Mediator.Core.Tutor.PdfTextExtractor.Extract(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture));
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { doc.PageCount, Pages = doc.Pages.Select(p => new { p.Number, p.Text }) }));
    return 0;
}
if (args.Length == 3 && args[0] == "--write-blender-script")
{
    File.WriteAllText(args[1], Mediator.Core.Blender.CarSceneScript.Render(new Mediator.Core.Blender.CarSceneParameters { DurationSec = 2, Engine = "CYCLES" }, Path.GetFullPath(args[2])));
    return 0;
}

var bridgeDll = args.Length > 0 ? Path.GetFullPath(args[0]) : throw new ArgumentException("Pass the path to mediator-mcp-bridge.dll");
var cliDll = args.Length > 1 ? Path.GetFullPath(args[1]) : throw new ArgumentException("Pass the path to mediator-cli.dll (the console host)");
var testsDll = typeof(Check).Assembly.Location;
Mediator.Tests.JournalFaultTests.TestsDll = testsDll;
Mediator.Tests.PdfHardeningTests.TestsDll = testsDll;
var tests = new List<(string Name, Func<Task> Run)>
{
    ("xxhash64 matches reference vectors", Tests.XxHashVectors),
    ("protected paths: git repo, SolidWorks project folder, protected segment", Tests.ProtectedPaths),
    ("journal: plan rejects unsafe moves, executes, rolls back exactly", Tests.JournalRoundTrip),
    ("journal: rollback refuses to overwrite a changed file", Tests.JournalConflict),
    ("journal: crash between rename and 'done' is reconciled on reopen", Tests.JournalRecovery),
    ("journal: another job cannot see or execute a batch", Tests.JournalJobIsolation),
    ("rules: first match wins, templates render, unmatched stay", Tests.RulesExpansion),
    ("gate: Destructive tools can never be auto-approved", Tests.GateClamp),
    ("generators: wsl.conf, fstab, .wsb, MCP configs", Tests.Generators),
    ("bridge+bus end to end: initialize, filtered list, calls, approval, errors", () => Tests.BridgeEndToEnd(bridgeDll)),
    ("bridge: local timeout answers once and drops the late reply", () => Tests.BridgeTimeout(bridgeDll)),
    ("bridge: rejected token exits 3 with clean errors", () => Tests.BridgeRejected(bridgeDll)),
    ("#13 swx: layouts, mapped entities with mark 1, undo group; strict mock rejects raw faces and mark 0", Mediator.Tests.SwxTests.MateSelectionAndLayouts),
    ("swx: modal dialog 2 s into a stalled call returns MODAL_DIALOG_BLOCKING at the 3 s probe; queue survives", Mediator.Tests.SwxTests.ModalDialogWatchdog),
    ("swx: STEP import via LoadFile4 + SaveAs3 into new parts, never overwriting; result feeds part_cards", Mediator.Tests.SwxTests.StepImport),
    ("swx: STEP import ownership (fake COM): user's document returned by LoadFile4 left untouched; surface-only, duplicate target, already-open refused", Mediator.Tests.SwxTests.StepImportOwnership),
    ("swx: geometric fallback when persist references no longer resolve", Mediator.Tests.SwxTests.FaceFallbackAfterReopen),
    ("tcp: listener runs only while WSL jobs exist, on an ephemeral loopback port", Mediator.Tests.TransportTests.ListenerOnlyWhileWslJobsExist),
    ("tcp: a valid pipe-bound token over TCP is rejected and audited", Mediator.Tests.TransportTests.PipeTokenOverTcpRejectedAndAudited),
    ("tcp: a socket that never says hello is dropped at 2 s", Mediator.Tests.TransportTests.IdleTcpSocketDropped),
    ("stream-json: BOM, CRLF, partial chunks, tool calls, errors, usage, rate limit", Mediator.Tests.StreamTests.ParserFixture),
    ("usage ledger: runs recorded, cooldown from rate limit, API-key billing alarm", Mediator.Tests.StreamTests.LedgerAndMonitor),
    ("billing: headless and .ps1 scrub API keys unless metered billing is opted in", Mediator.Tests.StreamTests.BillingScrub),
    ("runtime: assemble through the real bridge with AutoApprovalPrompt + simulated SolidWorks; full audit trail", () => Mediator.Tests.RuntimeTests.AssembleThroughBridgeWithAudit(bridgeDll)),
    ("runtime: kill switch during a pending approval: Killed < 100 ms, call cancelled, lease released, audited", () => Mediator.Tests.RuntimeTests.KillDuringApproval(bridgeDll)),
    ("console approval: [A]/[S]elect rows/[D]/[K]ill, timeout, abandoned read reused", Mediator.Tests.RuntimeTests.ConsolePrompt),
    ("overlay VM: untick 1 of 4 mates → 3 built; kill switch < 100 ms clears the pending approval", () => Mediator.Tests.OverlayTests.ApprovalSubsetAndKillSwitch(bridgeDll)),
    ("overlay VM: routing, / search, context chips, folder resolution, keep-subscription, notices, banner", Mediator.Tests.OverlayTests.RoutingAndContext),
    ("overlay VM: all 7 actions enabled; prefix/keyword routing → action, tools, subscription; failover moves the active pill", Mediator.Tests.OverlayTests.AllActionsAndFailoverPills),
    ("overlay XAML: well-formed, resources defined, every binding resolves on the ViewModels, every theme pack URI resolves", Mediator.Tests.OverlayTests.XamlBindings),
    ("interactive: wt.exe exits at once, bridge connects 200 ms later and works; done/kill/closed tab/no start end the job", () => Mediator.Tests.LaunchTests.InteractiveSurvivesLauncherExit(bridgeDll, testsDll)),
    ("antigravity stream-json: chunks, BOM, tool errors, joined deltas, usage, 429 → synthesized limit, daily reset, result-only", Mediator.Tests.LaunchTests.GeminiParser),
    ("router: category defaults, explicit provider, missing CLI fallback, prefixes", Mediator.Tests.LaunchTests.RouterRules),
    ("failover: Claude cooling → Gemini with settings.json + scrub + usage; NeverDowngrade refuses; Gemini quota recorded", () => Mediator.Tests.LaunchTests.FailoverToGemini(bridgeDll, testsDll)),
    ("resolve: probe + export through the bus, 1 of 4 cuts unticked → FCPXML 1.9 with 3 asset-clips at exact 29.97 frames, EDL, Studio script", () => Mediator.Tests.ProviderTests.ResolveTimeline(bridgeDll)),
    ("media probe: built-in MP4 (mvhd/tkhd/mdhd/stts/stsd) and WAV readers, ffprobe JSON", Mediator.Tests.ProviderTests.NativeProbe),
    ("blender: approved parameters in build_scene.py, blender -b -P, roots, ranges, no overwrite, render args", () => Mediator.Tests.ProviderTests.BlenderScene(bridgeDll)),
    ("code-cad: 4-part kit, 1 unticked → 3 STEP → swx_import_step → part cards → assembly; custom code is Destructive", () => Mediator.Tests.ProviderTests.CodeCadPipeline(bridgeDll)),
    ("tutor: exact det, inverse, RREF, charpoly, eigenpairs and graded claims on 3×3 matrices", () => Mediator.Tests.ProviderTests.MatrixVerifier(bridgeDll)),
    ("tutor: built-in PDF reader (ObjStm, Flate, Type0 + ToUnicode) → study pack for pages 2–3", () => Mediator.Tests.ProviderTests.PdfChapter(bridgeDll)),
    ("cli host process: doctor self-test, organize --rules --yes, rollback, usage errors", () => Mediator.Tests.RuntimeTests.CliHostProcess(bridgeDll, cliDll)),
    // V6 Phase 2a: filesystem authorization
    ("fs-auth: Windows and POSIX lexical rules (device, UNC, ADS, dots/spaces, reserved, 8.3, ..) and component containment", Mediator.Tests.SecurityTests.PathSyntaxRules),
    ("fs-auth: real symlinks (final, intermediate, relative, dangling), root protection, alias roots, no overwrite; openat2 and fallback", Mediator.Tests.SecurityTests.SymlinkEscapes),
    ("fs-auth: a bind mount inside a root is refused", Mediator.Tests.SecurityTests.BindMountInsideRoot),
    ("fs-auth: TOCTOU swap race (folder ↔ symlink) never writes or reads outside; openat2 and fallback", Mediator.Tests.SecurityTests.SwapRace),
    ("fs-auth: providers refuse a PDF behind a link and timeline media outside the roots; audited with the rule", () => Mediator.Tests.SecurityTests.ProvidersRefuseLinks(bridgeDll)),
    ("fs-auth (Windows): junctions, junction swap race, real ADS", Mediator.Tests.SecurityTests.WindowsJunctions),
    // V6 Phase 2c: journal state machine under real faults
    ("journal faults: real crash (FailFast in a child) at every boundary, first and middle hit → recover, resume, roll back exactly", Mediator.Tests.JournalFaultTests.CrashAtEveryBoundary),
    ("journal faults: file changed after approval is skipped; changed between fingerprint and rename is not rolled back over", Mediator.Tests.JournalFaultTests.ConcurrentModification),
    ("journal faults: stale rollback conflicts, newest-first works; a second process cannot open the journal", Mediator.Tests.JournalFaultTests.StaleRollbackAndSecondProcess),
    ("journal faults: permission failure as uid 65534 (real): one op fails, rest commit, rollback restores", Mediator.Tests.JournalFaultTests.PermissionFailure),
    ("journal faults: disk full under the journal (real 1 MiB tmpfs): stops consistently, reconciles, finishes", Mediator.Tests.JournalFaultTests.DiskFullJournal),
    ("journal faults (Windows): locked file (sharing violation)", Mediator.Tests.JournalFaultTests.LockedFileWindows),
    // V6 Phase 3: providers, failover, usage
    ("chat V10: one prompt at a time (double click starts one job), Stop kills the running turn and frees the chat", Mediator.Tests.ModelChoiceTests.OnePromptAtATimeAndStop),
    ("model picker V10: every installed CLI's models + efforts, pick fixes CLI/model/effort and is remembered; Auto untouched", Mediator.Tests.ModelChoiceTests.PickerAppliesModelAndEffort),
    ("effort V10: --effort on claude and agy command lines; unknown values never passed", Mediator.Tests.ModelChoiceTests.EffortOnCommandLine),
    ("F40 pipeline V10: folder from the prompt (existing, file path, new; system folders refused), CAD tools, capable model tier on both CLIs", Mediator.Tests.ModelChoiceTests.F40Pipeline),
    ("app session V10: CAD jobs start SolidWorks early (others do not), every SolidWorks tool waits for it, a failed start is a clear error", () => Mediator.Tests.AppSessionTests.SolidWorksStartedForCadJobs(bridgeDll)),
    ("chat app V10: search (titles, prompts, answers), rename, star (saved, listed first), retry, edit, Up recalls, new chat resets", Mediator.Tests.ChatAppTests.SearchRenameStarRetryEdit),
    ("folder permissions V10: refusals list allowed folders; a running job is granted a folder (created, audited, protected places refused)", () => Mediator.Tests.FolderPermissionTests.GrantWhileRunning(bridgeDll)),
    ("chat folders V10: Allow folder / a refusal / Save to reach every turn of the chat, outputs go to Save to, saved with the chat", Mediator.Tests.FolderPermissionTests.ChatFoldersAndSaveToReachEveryTurn),
    ("cad toolset V10: parts + assembly prompts get part building AND part cards/assembly in one job, Assemble builds missing parts, 60-turn CAD budget", () => { Mediator.Tests.AppSessionTests.PartsAndAssemblyInOneJob(); return Task.CompletedTask; }),
    ("live progress V10: partial messages show the tool being prepared and characters written, on the turn while it runs", () => { Mediator.Tests.ChatAppTests.LiveProgressWhileWriting(); return Task.CompletedTask; }),
    ("cad import V10: DXF/SVG profiles chain into outlines (arcs, bulges, curves, units, transforms), width/centre, import paths checked", () => { Mediator.Tests.CadToolsetTests.DxfAndSvgProfiles(); return Task.CompletedTask; }),
    ("cad toolset V16: all seven SolidWorks CAD tools in every CAD job, each holding the solidworks lease; export formats; files named in a prompt are readable, not writable", () => { Mediator.Tests.CadToolsetTests.ToolsetAndReadRoots(); return Task.CompletedTask; }),
    ("output folder V10: parts are saved INTO the allowed folder, an existing or a new folder below it; the folder itself is never replaced", () => { Mediator.Tests.FollowUpContextTests.OutputFolderInsideAllowedFolder(); return Task.CompletedTask; }),
    ("follow-up context V10: every turn carries the whole conversation (first request whole, failed turns marked), also after a failed turn", Mediator.Tests.FollowUpContextTests.FollowUpKeepsTheFirstRequest),
    ("user request V10: every job's prompt holds what the user typed, even when the action template (Assemble) has no slot for it", () => Mediator.Tests.FollowUpContextTests.ActionTemplatesKeepTheRequest(bridgeDll)),
    ("auto-approve V10: the toggle approves every request (and the waiting ones) without a card; every decision is logged in the chat; off at start", Mediator.Tests.AutoApproveTests.ToggleApprovesAndLogs),
    ("assembly name V10: the .SLDASM name a request gives is the output (folder or full path); default planes front/top/right as mate ids", () => { Mediator.Tests.AssemblyRequestTests.RequestedNameAndPlaneIds(); return Task.CompletedTask; }),
    ("default-plane mates V10: a plan mating front/top/right planes is validated and reaches SolidWorks as planes; never for Concentric", () => Mediator.Tests.AssemblyRequestTests.DefaultPlaneMatesReachTheBackend(bridgeDll)),
    ("swx worker V10: mates on default planes (by tree position), prompts quiet during the build and the user's settings restored", Mediator.Tests.SwxTests.DefaultPlaneMatesAndQuietPrompts),
    ("chrome V11: browser prompts route to Browser; claude --chrome with only the safe browser tools; local files stay off", () => { Mediator.Tests.BrowserTests.RoutingAndLaunch(); return Task.CompletedTask; }),
    ("chrome V11: Chrome started once on demand (joined, light flags), an open Chrome reused, a missing one explained", Mediator.Tests.BrowserTests.StartsChromeOnceAndLight),
    ("chrome V13: extension bridge: origin + mutual pairing proof, multi-frame messages, concurrent requests, errors, disconnect fails pending calls", Mediator.Tests.ExtensionBridgeTests.PairingAndCalls),
    ("chrome V13: upgrade checks (origin, loopback Host, path, headers) and proofs", () => { Mediator.Tests.ExtensionBridgeTests.UpgradeChecks(); return Task.CompletedTask; }),
    ("chrome V13: shipped MV3 extension (permissions, key = admitted id, methods), install/update, pairing.json, secret kept", () => { Mediator.Tests.ExtensionBridgeTests.PackagingAndPairingFiles(); return Task.CompletedTask; }),
    ("chrome V14.1: side panel chat: messages both ways over the paired bridge; a panel message is a Browser job on the tab beside it, follow-ups keep the chat", Mediator.Tests.ExtensionBridgeTests.PanelChatMessages),
    ("chrome V14.1: side panel chat in the app: a failed start is reported to the panel; Stop and New chat; panel jobs are not the overlay's follow-up", Mediator.Tests.OverlayTests.PanelChatErrors),
    ("chrome V14.1: Chrome profiles say where the extension was loaded from; a copy that never pairs (repo folder) is flagged with the fix", () => { Mediator.Tests.ExtensionBridgeTests.WrongFolderDetected(); return Task.CompletedTask; }),
    ("chrome V13: browser_* tools through the real bridge and bus: compact read, repeatable clicks, screenshot image, approvals, audit", () => Mediator.Tests.ExtensionBridgeTests.ToolsThroughTheBus(bridgeDll)),
    ("usage V14: Claude plan meter (5 h + week) read with Claude Code's token, cached a minute, never refreshed; failures readable, token never shown", Mediator.Tests.UsageTests.ClaudePlanMeter),
    ("usage V14: Usage tab: Claude meters + 80% flag, Mediator's own runs from the ledger, Google AI Pro cooldown, no tokens used", () => Mediator.Tests.UsageTests.UsageTab(bridgeDll)),
    ("office V15: Word, Excel and PowerPoint files written and read back without Office; never replaces a file; legacy formats explained; only documents are opened", Mediator.Tests.OfficeCadTests.OfficeDocumentsRoundTrip),
    ("office V15: classic Outlook (faked): search, read, draft asks first and is never sent; there is no send tool", Mediator.Tests.OfficeCadTests.OutlookDraftsNeverSend),
    ("cad files V15: meshes analysed (watertight, volume) and converted STL/OBJ/PLY/3MF with units and centring; STEP described; nothing replaced", Mediator.Tests.OfficeCadTests.CadFiles),
    ("routing V15: Office and CAD-file requests get their tools; email validation, C++ STL and 'next step' do not; /office and /outlook", Mediator.Tests.OfficeCadTests.Routing),
    ("attachments V15: an attached workbook is read as cells and a mesh as its description", Mediator.Tests.OfficeCadTests.AttachmentsReadOfficeAndCad),
    ("cad apps V16: tools come from the app's prefix and capabilities, hold its lease; SolidWorks names kept; an app that cannot run is never called", Mediator.Tests.CadAppTests.ToolsetFromCapabilities),
    ("cad apps V16: documents only inside the job's folders and of the app's types; nothing replaced; the active document's folder checked before a change", Mediator.Tests.CadAppTests.PathsAndActiveDocument),
    ("cad apps V16: edit operations checked before approval, one row each, only the kept ones run", Mediator.Tests.CadAppTests.EditOperationsApproval),
    ("cad apps V16: pictures of the model returned as images from the call's own folder, which is then deleted", Mediator.Tests.CadAppTests.PicturesAsImages),
    ("cad V16.1: with SolidWorks open, requests about the part/drawing/BOM/selection get every SolidWorks tool; chats saved by V15 get the V16 tools", () => { Mediator.Tests.CadRoutingTests.OpenModelRequestsAndOldChats(); return Task.CompletedTask; }),
    ("cad V16.1: a SolidWorks job gets the open document's folder and its name; other jobs do not", () => Mediator.Tests.CadRoutingTests.OpenDocumentFolderShared(bridgeDll)),
    ("cad V16.1: the running build is recorded so a newer one can replace an older overlay; never itself", () => { Mediator.Tests.CadRoutingTests.RunningBuildRecorded(); return Task.CompletedTask; }),
    ("iterations V10: saving vN deletes Mediator's older versions (and backups, copies, STLs, views) only; views returned as images; proportions vs reference", () => { Mediator.Tests.ChatAppTests.IterationsVersionsAndViews(); return Task.CompletedTask; }),
    ("part tool V10: schema offers every builder op (loft, sweep, revolve, surfaces, shell, mirror...) and fitBoxMm", () => { Mediator.Tests.AppSessionTests.PartToolSchemaCoversSurfacing(); return Task.CompletedTask; }),
    ("saved chats V10: tools, category and folder survive a restart; old chats inferred; follow-ups gain the tools they need", Mediator.Tests.ModelChoiceTests.SavedChatKeepsTools),
    ("providers: pre-launch routing matrix (each unavailable, both unavailable, both cooling, NeverDowngrade) with original + reason", Mediator.Tests.FailoverTests.RouterMatrix),
    ("providers: REAL captures (Claude Code 2.1.309 invalid key, Gemini 0.65.0 no-auth/untrusted/no-result) classified; result-then-abnormal-exit", Mediator.Tests.FailoverTests.RealCapturesClassified),
    ("usage: unknown stays unknown (never 0), duplicate results counted once, no-result runs recorded, V5 ledger migrated", Mediator.Tests.FailoverTests.UsageUnknownNotZero),
    ("failover: mid-job switch only before side effects, same grant; side effect / NeverDowngrade / second limit refuse; all audited", () => Mediator.Tests.FailoverTests.MidJobFailover(bridgeDll, testsDll)),
    ("failover: real invalid-key capture killed by billing guard, AuthFailed when metered allowed; Gemini exit 41; trust env; abnormal exit", () => Mediator.Tests.FailoverTests.RealAuthFailures(bridgeDll, testsDll)),
    ("overlay: an uncertain call is amber 'outcome unknown', kill says it may still finish, a late result clears it", () => Mediator.Tests.OverlayStateTests.UncertainCallsAreNotShownAsStopped(bridgeDll)),
    ("bus: hostile frames (oversized/negative/partial hello, invalid UTF-8, non-object, duplicate id by value, >16 MiB) handled; bus keeps serving", Mediator.Tests.BusHardeningTests.HostileFrames),
    ("bus: per-connection in-flight cap refuses excess requests with an error", Mediator.Tests.BusHardeningTests.InFlightCap),
    ("bus: a grant revoked while a Write waits for approval never executes, even if approval arrives after", Mediator.Tests.BusHardeningTests.RevokedWhileAwaitingApproval),
    ("bus: 4 jobs x 40 concurrent calls with a mid-run revocation; every response to the right id and job; no deadlock", Mediator.Tests.BusHardeningTests.ConcurrencyStress),
    // V6 Phases 4-5: hostile inputs
    ("pdf: 200 000 nested arrays → FormatException in a child process (was a stack overflow that killed the process)", Mediator.Tests.PdfHardeningTests.DeepNestingIsAFormatError),
    ("pdf: limits: 300 MiB decompression bomb, total budget, operand flood, time budget, huge ObjStm /N, encrypted, DCT, page ranges", Mediator.Tests.PdfHardeningTests.ResourceLimits),
    ("pdf: 3000 seeded mutations (flips, truncation, junk, splices) → parse or FormatException only, none over 5 s, no crash", Mediator.Tests.PdfHardeningTests.MutationFuzz),
    ("tutor: exact near-singular/singular/rational/defective/complex/huge cases; pathological numbers and malformed claims refused", () => Mediator.Tests.TutorExactnessTests.ExactnessAndPathologicalInput(bridgeDll)),
    ("audit: hash chain detects edit, delete, insert and unhashed rows; append-only triggers still refuse UPDATE", Mediator.Tests.AuditTests.HashChainDetectsTampering),
    ("audit: V5 log migrates; rotation archives (never deletes) and the new log continues the chain; forged link detected", Mediator.Tests.AuditTests.V5LogMigratesAndRotationContinuesChain),
    ("audit: secrets redacted before storage (registered values, key formats, secret fields, bearer, VAR=value)", Mediator.Tests.AuditTests.RedactionBeforeStorage),
    ("audit: appender SIGKILLed mid-stream 3 times; chain verifies and continues each time", () => Mediator.Tests.AuditTests.CrashMidAppendStaysConsistent(testsDll)),
    ("support bundle: only documented files; no secrets, tokens, file names, contents or real paths; chain head included", () => Mediator.Tests.AuditTests.SupportBundleIsClean(bridgeDll)),
    ("step: validator on STEP files written by real build123d/OpenCascade: solid ok; surface-only, truncated, empty refused", Mediator.Tests.StepFixtureTests.ValidatorOnRealStepFiles),
    // V6 Phase 2d: durable operation store
    ("operations: state machine refuses illegal transitions; canonical request keys; full history", Mediator.Tests.OperationTests.StateMachineRules),
    ("operations: replay after commit returns the stored result; concurrent duplicate refused; one side effect", () => Mediator.Tests.OperationTests.ReplayAndDuplicates(bridgeDll)),
    ("operations: timeout during a call that ignores cancellation → outcome_unknown, retry refused, late commit recorded, then replay", () => Mediator.Tests.OperationTests.TimeoutDuringExecution(bridgeDll)),
    ("operations: bridge killed mid-call → cancellation_requested (not cancelled), real outcome recorded, reconnect replays", () => Mediator.Tests.OperationTests.KillBridgeMidCall(bridgeDll)),
    ("operations: Core killed mid-call (real SIGKILL) → restart marks recovery_required, no blind replay, user verdict; journal reconciler settles files ops", () => Mediator.Tests.OperationTests.KillCoreAndRestart(bridgeDll, testsDll)),
    // V6 Phase 2b: custom-code isolation
    ("isolation: approval bound to script/args/folder/policy; tampered binding refused; link/surface/truncated/extra outputs refused; staging cleanup never follows links", () => Mediator.Tests.IsolationTests.ApprovalBindingAndOutputChecks(bridgeDll)),
    ("isolation (real bwrap): no host files, read-only system, no secrets in env, uid 65534, own PID ns, no nested userns, no mount", Mediator.Tests.IsolationTests.RealSandboxConfinement),
    ("isolation (real bwrap): no network (host loopback, external, DNS)", Mediator.Tests.IsolationTests.RealSandboxNoNetwork),
    ("isolation (real bwrap): memory, CPU, process and file-size limits; wall-clock kill; no surviving grandchildren", Mediator.Tests.IsolationTests.RealSandboxLimitsAndCleanup),
    ("fs-auth: journal rejects link moves at plan time and stops a move whose destination was swapped after approval", Mediator.Tests.SecurityTests.JournalRefusesLinks),
    // V7: attachments (drag-drop, paste, Attach button)
    ("attachments: snapshot/folder/PNG staging; links, FIFO, /proc, /dev, credentials, home, data folder, drive root, oversize, fake PNG refused; discard; prune", Mediator.Tests.AttachmentTests.StagingRules),
    ("attachments: read-only roots allow read/list, deny create/move/mkdir (read_only); never remove write access; links still refused", Mediator.Tests.AttachmentTests.ReadOnlyRoots),
    ("attachments through the bus: listed, text + image content, other job refused, files tool cannot move them, live attach audited; V6 tool lists unchanged without attachments", () => Mediator.Tests.AttachmentTests.ThroughTheBus(bridgeDll)),
    // V7: connectors, custom MCP, overlay
    ("custom MCP: validate, 15 s health ping, mediator.mcp.json (no env), reload, disable/remove unregister; dead or missing servers reported", () => Mediator.Tests.ConnectorTests.CustomMcpRegistry(testsDll)),
    ("custom MCP through the bus: Destructive (asks even when the grant says Auto), denied never runs, secrets scrubbed, image passthrough, audited", () => Mediator.Tests.ConnectorTests.CustomMcpThroughGate(bridgeDll, testsDll)),
    ("connector probe: --version parsed, credential file only checked for existence, sign-in terminal without API keys", Mediator.Tests.ConnectorTests.ProbeOnLinux),
    ("overlay V10: SelectModel + Quota Saver routing; free text runs headless (no terminal) and streams a >500-char answer untruncated; cross-CLI continuation", () => Mediator.Tests.OverlayTests.V10ChatRoutingAndContinuation(bridgeDll, testsDll)),
    // V9: session continuation (claude --resume)
    ("session V9: a finished run's session_id is offered; the follow-up runs claude -p --resume <id> in the same folder with a new job and grant; unsafe ids, Gemini and a limited Claude are refused; killed runs are not offered", () => Mediator.Tests.SessionTests.RuntimeContinuation(bridgeDll, testsDll)),
    ("overlay V9: ↩ Continue session chip appears auto-selected, Enter continues, click deselects, × and a folder switch remove it, a different folder in the prompt starts fresh, the chip returns after the follow-up", () => Mediator.Tests.SessionTests.OverlayContinuation(bridgeDll, testsDll)),
    // V9: the whole overlay is compiled (WpfCompileStub)
    ("wpf V9: overlay code runs against the compile stub: converters, ThemeManager pack URIs and its compiled → loose → keep-current fallback", Mediator.Tests.WpfCompileTests.ConvertersAndThemeManager),
    ("wpf V9: the compile pass catches a missing using System.IO, an x:Name renamed only in XAML, and a non-existent Windows Forms member", Mediator.Tests.WpfCompileTests.CompilePassCatchesMistakes),
    // V9: hotkeys
    ("overlay V9: HotkeyParser accepts Ctrl/Alt/Shift/Win + Space, A–Z, 0–9, F1–F24, Tab, Enter, ~ (normalised, round-trips); refuses modifier-only, Shift-only and system combinations", Mediator.Tests.OverlayTests.HotkeyParsing),
    ("overlay V9: hotkey start falls back to the default then none; live rebind releases the old one first; a conflict keeps the previous hotkey; the setup card rebinds before saving; tray text follows", Mediator.Tests.OverlayTests.HotkeyRebinding),
    // V8: multi-monitor / mixed-DPI placement
    ("placement V8: work area on every taskbar edge, auto-hide strips, 100/150/200 % and 100→200 % moves, negative coordinates, too tall/wide clamped, 5000 random layouts stay on screen", Mediator.Tests.PlacementTests.Geometry),
    ("placement V8: WM_WINDOWPOSCHANGING rewrite on an unmanaged WINDOWPOS (resize, DPI rect, move, hide untouched); WINDOWPOS/APPBARDATA/MONITORINFO layouts", Mediator.Tests.PlacementTests.WindowPosChanging),
    // V8: Windows release scripts (static: they cannot run here)
    ("release scripts V8: .ps1 ASCII-only (PowerShell 5.1 ANSI trap), launch.bat/install-shortcut.vbs ASCII + CRLF, no cmd blocks, per-user, every VBScript variable declared", Mediator.Tests.ReleaseScriptTests.ScriptsAreWindowsSafe),
    ("release scripts V8: publish-release.ps1 always publishes the 4 executables, the 3 themes and both launchers, and checks them", Mediator.Tests.ReleaseScriptTests.PublishScriptPackagesEverything),
    // V9: offline shims decoupled from MediatorVerify
    ("release scripts V9: publish-release.ps1 restores from NuGet and falls back to -p:MediatorUseShims=true (framework-dependent, no Native AOT); -NoShimFallback; no MSVC never stops it", Mediator.Tests.ReleaseScriptTests.PublishScriptFallsBackToShims),
    ("build V9: MediatorUseShims evaluated by MSBuild: NuGet by default, shims + empty local feed + no AOT when set, independent of MediatorVerify", Mediator.Tests.ReleaseScriptTests.ShimSwitchEvaluation),
    ("build V9: real ship build (MediatorVerify=false) with MediatorUseShims=true from an empty feed: 0 packages, 0 warnings; mediator-cli doctor from it opens the databases and passes the bridge self-test", Mediator.Tests.ReleaseScriptTests.OfflineShipBuild),
    // V8: building and running without SolidWorks
    ("swx V8: SwxUnavailableHost fails every call with solidworks_not_installed; the stub build still creates SwxComHost", Mediator.Tests.SwxFallbackTests.UnavailableHostInProcess),
    ("swx V8: worker built without the SolidWorks interop, as a real child process through the bus: \"SolidWorks assemblies not installed on this machine\", do-not-retry, audited", () => Mediator.Tests.SwxFallbackTests.NoInteropWorkerThroughTheBus(bridgeDll)),
    ("swx V8: ship build (MediatorVerify=false) with no interop: warning MEDIATOR001 (not an error, even with -warnaserror), MediatorRequireSolidWorks=true → error MEDIATOR002, built worker answers", Mediator.Tests.SwxFallbackTests.ShipBuildWithoutSolidWorks),
    // V8: Windows desktop-app discovery (fake registry and Program Files tree)
    ("desktop apps V8: Blender in version folders (newest wins), %LOCALAPPDATA%\\Programs, BlenderFoundation key, Apps & features, Steam; PATH and MEDIATOR_BLENDER take precedence", Mediator.Tests.DesktopAppTests.Blender),
    ("desktop apps V8: Resolve in Program Files (x86), a registry-only folder, Studio from Apps & features (merged), probe says where it looked", Mediator.Tests.DesktopAppTests.Resolve),
    ("desktop apps V8: SolidWorks newest SOLIDWORKS <year>\\Setup with api\\redist, COM LocalServer32, default folder, never PATH; probe running / idle / absent", Mediator.Tests.DesktopAppTests.SolidWorks),
    ("overlay V7: theme cycle, ThemeChanged, settings.json persisted and reloaded; malformed settings kept as .bad; hotkey parser", Mediator.Tests.OverlayTests.ThemeSwitchingAndPersistence),
    ("overlay V7: reactor Idle → Active → AwaitingApproval → Active → Error (failure, rate limit, billing) → acknowledged / cleared by a new job", () => Mediator.Tests.OverlayTests.ReactorStateTransitions(bridgeDll)),
    ("overlay V7: drop/paste/pick → chips; refusals reported; × deletes unshared snapshots; shared with the one running job; consumed by the next job; pin", () => Mediator.Tests.OverlayTests.DragDropAndClipboardAttachments(bridgeDll)),
    ("overlay V7: Connectors tab statuses (version, sign-in, cooldown, apps), Sign In terminal, + Add Custom MCP; first-run card", () => Mediator.Tests.OverlayTests.ConnectorsAndOnboarding(testsDll)),
    ("overlay XAML: checker catches a missing theme key, StaticResource to a theme key, a colour literal, a duplicate property, a bad binding, a relative or wrong-assembly pack URI and a theme the loose fallback cannot load", Mediator.Tests.OverlayTests.XamlCheckerCatchesMistakes),
};

// Live tests are always listed; without MEDIATOR_LIVE_PYTHON each one reports SKIP (never PASS).
Func<Task> Live(Func<Task> body) => () => Mediator.Tests.LiveTests.Python is null
    ? throw new SkipException("MEDIATOR_LIVE_PYTHON is not set (a Python with build123d, bpy, pymupdf and sympy)")
    : body();
tests.Add(("live: build123d builds all 7 templates to STEP within their envelopes", Live(() => Mediator.Tests.LiveTests.Build123dTemplates(bridgeDll))));
tests.Add(("live: the car scene script runs on the real Blender API (bpy) and saves a .blend", Live(() => Mediator.Tests.LiveTests.BlenderScene(bridgeDll))));
tests.Add(("live: PyMuPDF chapter extraction and the SymPy cross-check script", Live(() => Mediator.Tests.LiveTests.PdfAndSympy(bridgeDll))));
tests.Add(("live: build123d custom code inside real bubblewrap through the bus; hostile script contained", Live(() => Mediator.Tests.IsolationTests.LiveCustomCodeInSandbox(bridgeDll))));
tests.Add(("live: every CAD template at min/max and seeded random valid parameters → STEP, validated and re-imported in the sandbox", Live(() => Mediator.Tests.LiveCadBoundaryTests.BoundaryAndRandomParameters(bridgeDll))));
tests.Add(("live: built-in MP4/MOV/WAV reader agrees with ffprobe on clips made by ffmpeg", Live(Mediator.Tests.LiveTests.MediaProbes)));

// A skip is never a pass: it is printed as SKIP, counted separately, and with MEDIATOR_REQUIRE_ALL=1 it fails the run.
bool requireAll = Environment.GetEnvironmentVariable("MEDIATOR_REQUIRE_ALL") == "1";
string? only = Environment.GetEnvironmentVariable("MEDIATOR_ONLY");
int failed = 0, skipped = 0, passed = 0;
var skips = new List<string>();
foreach (var (name, run) in tests)
{
    if (only is not null && !only.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase))) continue; // MEDIATOR_ONLY=a,b,c
    var sw = Stopwatch.StartNew();
    try
    {
        await run().WaitAsync(TimeSpan.FromSeconds(name.StartsWith("live:", StringComparison.Ordinal) ? 900 : 120));
        passed++;
        Console.WriteLine($"PASS  {name} ({sw.ElapsedMilliseconds} ms)");
    }
    catch (SkipException skip)
    {
        skipped++;
        skips.Add($"{name}: {skip.Message}");
        Console.WriteLine($"SKIP  {name}: {skip.Message}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}: {ex}");
    }
}
int total = passed + failed + skipped;
Console.WriteLine($"\n{passed} passed, {failed} failed, {skipped} skipped ({total} total).");
foreach (var s in skips) Console.WriteLine($"  skipped: {s}");
if (failed > 0) { Console.WriteLine($"{failed} of {total} tests FAILED."); return 1; }
if (skipped > 0 && requireAll) { Console.WriteLine("MEDIATOR_REQUIRE_ALL=1: skipped tests count as failures."); return 2; }
Console.WriteLine(skipped == 0 ? $"All {total} tests passed." : $"All {passed} tests that ran passed; {skipped} did not run.");
return 0;

/// <summary>Thrown by a test that cannot run here (missing tool or privilege). Reported as SKIP, never as PASS.</summary>
sealed class SkipException(string reason) : Exception(reason);

static class Check
{
    public static void True(bool condition, string what)
    {
        if (!condition) throw new Exception("Assertion failed: " + what);
    }
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Assertion failed: {what}: expected {expected}, got {actual}");
    }
}

static class Tests
{
    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static ProtectedPathPolicy Policy(params string[] protectedRoots) => new(protectedRoots, [".sldasm", ".slddrw"]);

    public static Task XxHashVectors()
    {
        // Reference values from the xxhash Python package (xxh64).
        (byte[] Data, ulong Seed0, ulong Seed42)[] vectors =
        [
            ([], 0xef46db3751d8e999, 0x98b1582b0977e704),
            ("a"u8.ToArray(), 0xd24ec4f1a98c6e5b, 0x88e4fe59adf7b0cc),
            ("abc"u8.ToArray(), 0x44bc2cf5ad770999, 0x13c1d910702770e6),
            ("The quick brown fox jumps over the lazy dog"u8.ToArray(), 0x0b242d361fda71bc, 0xaa9f288a8baa3d3f),
            (Enumerable.Range(0, 1280).Select(i => (byte)(i % 256)).ToArray(), 0xafc184ad7938a354, 0x244d19fb92671adb),
        ];
        foreach (var (data, s0, s42) in vectors)
        {
            Check.Equal(s0, XxHash64.HashToUInt64(data), $"seed 0, {data.Length} bytes");
            Check.Equal(s42, XxHash64.HashToUInt64(data, 42), $"seed 42, {data.Length} bytes");
            var inc = new XxHash64();
            for (int i = 0; i < data.Length; i += 7) inc.Append(data.AsSpan(i, Math.Min(7, data.Length - i)));
            Check.Equal(s0, inc.GetCurrentHashAsUInt64(), $"incremental, {data.Length} bytes");
        }
        return Task.CompletedTask;
    }

    public static Task ProtectedPaths()
    {
        string root = TempDir();
        string repo = Path.Combine(root, "repo"); Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "src.txt"), "x");
        string cad = Path.Combine(root, "gearbox"); Directory.CreateDirectory(Path.Combine(cad, "parts"));
        File.WriteAllText(Path.Combine(cad, "gearbox.SLDASM"), "asm");
        File.WriteAllText(Path.Combine(cad, "parts", "shaft.sldprt"), "prt");
        string plain = Path.Combine(root, "downloads"); Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "a.pdf"), "pdf");
        string sys = Path.Combine(root, "sys"); Directory.CreateDirectory(sys);
        File.WriteAllText(Path.Combine(sys, "x.txt"), "x");

        var p = Policy(sys);
        Check.True(p.CheckSource(Path.Combine(repo, "src.txt"))?.Contains("git repository") == true, "git repo protected");
        Check.True(p.CheckSource(Path.Combine(cad, "parts", "shaft.sldprt"))?.Contains("project folder") == true, "sldasm folder and descendants protected");
        Check.True(p.CheckSource(Path.Combine(sys, "x.txt"))?.Contains("protected location") == true, "protected root");
        Check.True(p.CheckSource(Path.Combine(plain, "a.pdf")) is null, "ordinary file allowed");
        Check.True(p.CheckDestination(Path.Combine(repo, "new", "b.pdf")) is not null, "destination inside repo refused");
        Check.True(p.CheckDestination(Path.Combine(root, "$Recycle.Bin", "b.pdf")) is not null, "protected segment refused");
        return Task.CompletedTask;
    }

    public static async Task JournalRoundTrip()
    {
        string root = TempDir();
        string src = Path.Combine(root, "inbox"); Directory.CreateDirectory(src);
        for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(src, $"doc{i}.pdf"), new string('x', 1000 + i));
        File.WriteAllText(Path.Combine(src, "keep.txt"), "already there");
        string repo = Path.Combine(root, "repo"); Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, "code.cs"), "code");
        string outside = TempDir();
        File.WriteAllText(Path.Combine(outside, "o.pdf"), "o");

        await using var journal = await FileJournalService.OpenAsync(Path.Combine(root, "journal.db"), Policy());
        var roots = new Mediator.Core.Security.PathAuthorizer([root]);
        var moves = Enumerable.Range(0, 5).Select(i => new MoveRequest(Path.Combine(src, $"doc{i}.pdf"), Path.Combine(root, "sorted", "pdf", "2026", $"doc{i}.pdf"))).ToList();
        moves.Add(new MoveRequest(Path.Combine(repo, "code.cs"), Path.Combine(root, "sorted", "code.cs")));        // protected
        moves.Add(new MoveRequest(Path.Combine(outside, "o.pdf"), Path.Combine(root, "sorted", "o.pdf")));         // outside roots
        moves.Add(new MoveRequest(Path.Combine(src, "keep.txt"), Path.Combine(src, "keep.txt")));                  // same path
        moves.Add(new MoveRequest(Path.Combine(src, "doc0.pdf"), Path.Combine(root, "elsewhere", "doc0.pdf")));    // moved twice

        var plan = await journal.PlanAsync("job1", moves, roots, "test", CancellationToken.None);
        Check.Equal(5, plan.Ops.Count(o => o.Kind == "move"), "accepted moves");
        Check.Equal(4, plan.Rejected.Count, "rejected moves");
        Check.Equal(3, plan.Ops.Count(o => o.Kind == "mkdir"), "folders to create (sorted, pdf, 2026)");
        Check.True(plan.Ops.First().Kind == "mkdir", "folders created before the first move");

        var exec = await journal.ExecuteAsync(plan.BatchId, "job1", null, CancellationToken.None);
        Check.Equal("committed", exec.Status, "execute status");
        Check.True(Enumerable.Range(0, 5).All(i => File.Exists(Path.Combine(root, "sorted", "pdf", "2026", $"doc{i}.pdf"))), "files at destination");
        Check.True(!File.Exists(Path.Combine(src, "doc0.pdf")), "source gone");
        Check.True(File.Exists(Path.Combine(repo, "code.cs")), "protected file untouched");

        var rb = await journal.RollbackAsync(plan.BatchId, "job1", CancellationToken.None);
        Check.Equal("rolled_back", rb.Status, "rollback status");
        Check.Equal(0, rb.Conflicts.Count, "no conflicts");
        Check.True(Enumerable.Range(0, 5).All(i => File.Exists(Path.Combine(src, $"doc{i}.pdf"))), "files back at source");
        Check.True(!Directory.Exists(Path.Combine(root, "sorted")), "created folders removed");
        Check.Equal(1001, (int)new FileInfo(Path.Combine(src, "doc1.pdf")).Length, "content intact");
    }

    public static async Task JournalConflict()
    {
        string root = TempDir();
        File.WriteAllText(Path.Combine(root, "a.txt"), "original");
        File.WriteAllText(Path.Combine(root, "b.txt"), "original b");
        await using var journal = await FileJournalService.OpenAsync(Path.Combine(root, "journal.db"), Policy());
        var roots = new Mediator.Core.Security.PathAuthorizer([root]);
        var plan = await journal.PlanAsync("job1",
            [new(Path.Combine(root, "a.txt"), Path.Combine(root, "out", "a.txt")), new(Path.Combine(root, "b.txt"), Path.Combine(root, "out", "b.txt"))],
            roots, null, CancellationToken.None);
        await journal.ExecuteAsync(plan.BatchId, "job1", null, CancellationToken.None);

        File.AppendAllText(Path.Combine(root, "out", "a.txt"), " edited after the move"); // user edited a moved file
        File.WriteAllText(Path.Combine(root, "b.txt"), "a new file now occupies b's old path");

        var rb = await journal.RollbackAsync(plan.BatchId, "job1", CancellationToken.None);
        Check.Equal("partially_rolled_back", rb.Status, "status");
        Check.Equal(3, rb.Conflicts.Count, "a changed, b's path occupied, folder not empty");
        Check.True(File.ReadAllText(Path.Combine(root, "out", "a.txt")).EndsWith("edited after the move"), "edited file left in place, not overwritten");
        Check.Equal("a new file now occupies b's old path", File.ReadAllText(Path.Combine(root, "b.txt")), "occupant not overwritten");
    }

    public static async Task JournalRecovery()
    {
        string root = TempDir();
        string db = Path.Combine(root, "journal.db");
        File.WriteAllText(Path.Combine(root, "a.txt"), "data a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "data b");
        string batch;
        await using (var journal = await FileJournalService.OpenAsync(db, Policy()))
        {
            var plan = await journal.PlanAsync("job1",
                [new(Path.Combine(root, "a.txt"), Path.Combine(root, "a2.txt")), new(Path.Combine(root, "b.txt"), Path.Combine(root, "b2.txt"))],
                new Mediator.Core.Security.PathAuthorizer([root]), null, CancellationToken.None);
            batch = plan.BatchId;
        }

        // Simulate a crash: the batch was 'executing', the fingerprint for a.txt was written, the rename
        // happened, but the process died before marking the op done. b.txt was never touched.
        var fp = await FileFingerprint.ComputeAsync(Path.Combine(root, "a.txt"), CancellationToken.None);
        await using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE batch SET status='executing' WHERE batch_id=$b; UPDATE op SET size_bytes=$s, mtime_ticks=$m, fingerprint=$f WHERE batch_id=$b AND seq=1;";
            cmd.Parameters.AddWithValue("$b", batch);
            cmd.Parameters.AddWithValue("$s", fp.Size);
            cmd.Parameters.AddWithValue("$m", fp.LastWriteUtcTicks);
            cmd.Parameters.AddWithValue("$f", fp.Hash);
            cmd.ExecuteNonQuery();
        }
        File.Move(Path.Combine(root, "a.txt"), Path.Combine(root, "a2.txt"));

        await using (var journal = await FileJournalService.OpenAsync(db, Policy())) // runs RecoverAsync
        {
            var status = await journal.GetStatusAsync(batch, "job1", CancellationToken.None);
            Check.Equal("partially_committed", status.Status, "status after recovery");
            Check.Equal(1, status.OpStates.GetValueOrDefault("done"), "a.txt reconciled as done");
            Check.Equal(1, status.OpStates.GetValueOrDefault("pending"), "b.txt still pending");

            var resumed = await journal.ExecuteAsync(batch, "job1", null, CancellationToken.None);
            Check.Equal("committed", resumed.Status, "resume completes the batch");
            var rb = await journal.RollbackAsync(batch, "job1", CancellationToken.None);
            Check.Equal("rolled_back", rb.Status, "both moves roll back");
            Check.True(File.Exists(Path.Combine(root, "a.txt")) && File.Exists(Path.Combine(root, "b.txt")), "originals restored");
        }
    }

    public static async Task JournalJobIsolation()
    {
        string root = TempDir();
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        await using var journal = await FileJournalService.OpenAsync(Path.Combine(root, "journal.db"), Policy());
        var plan = await journal.PlanAsync("job1", [new(Path.Combine(root, "a.txt"), Path.Combine(root, "a2.txt"))], new Mediator.Core.Security.PathAuthorizer([root]), null, CancellationToken.None);
        bool refused = false;
        try { await journal.ExecuteAsync(plan.BatchId, "job2", null, CancellationToken.None); }
        catch (JournalException ex) { refused = ex.Message.Contains("No batch"); }
        Check.True(refused, "job2 cannot execute job1's batch");
        Check.True(File.Exists(Path.Combine(root, "a.txt")), "nothing moved");
    }

    public static Task RulesExpansion()
    {
        string root = TempDir();
        string src = Path.Combine(root, "Downloads"); Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "invoice-march.pdf"), "1");
        File.WriteAllText(Path.Combine(src, "paper.PDF"), "2");
        File.WriteAllText(Path.Combine(src, "photo.jpg"), "3");
        File.WriteAllText(Path.Combine(src, "notes.txt"), "4");
        File.SetLastWriteTime(Path.Combine(src, "paper.PDF"), new DateTime(2024, 5, 3, 12, 0, 0, DateTimeKind.Local));

        var rules = FileRules.Parse(JsonDocument.Parse($$"""
        [
          { "nameGlob": "*invoice*", "destination": "{{Esc(root)}}/Finance" },
          { "extensions": ["pdf"], "destination": "{{Esc(root)}}/Docs/{ext}/{yyyy}" },
          { "extensions": [".jpg"], "olderThanDays": 3650, "destination": "{{Esc(root)}}/OldPhotos" }
        ]
        """).RootElement);
        var exp = FileRules.Expand(src, false, rules, 100, DateTime.UtcNow, CancellationToken.None);
        Check.Equal(4, exp.Scanned, "scanned");
        Check.Equal(2, exp.Unmatched, "photo is too new, notes has no rule");
        var byName = exp.Moves.ToDictionary(m => Path.GetFileName(m.From), m => m.To);
        Check.Equal(Path.GetFullPath(Path.Combine(root, "Finance", "invoice-march.pdf")), byName["invoice-march.pdf"], "first rule wins over extension rule");
        Check.Equal(Path.GetFullPath(Path.Combine(root, "Docs", "pdf", "2024", "paper.PDF")), byName["paper.PDF"], "template renders ext lowercased and year");
        return Task.CompletedTask;

        static string Esc(string s) => s.Replace("\\", "\\\\");
    }

    public static Task GateClamp()
    {
        var grant = new JobGrant
        {
            JobId = "j", Token = "t", AllowedTools = new HashSet<string>(), FileRoots = [], Paths = new Mediator.Core.Security.PathAuthorizer([]), ExpiresAt = DateTimeOffset.MaxValue,
            ToolModes = new Dictionary<string, ApprovalMode> { ["wipe"] = ApprovalMode.Auto, ["read"] = ApprovalMode.Deny },
        };
        var schema = new JsonObject();
        Check.Equal(ApprovalMode.Ask, PermissionGate.EffectiveMode(grant, new ToolDescriptor("wipe", "", schema, ToolRisk.Destructive, null, TimeSpan.FromSeconds(1))), "Destructive Auto clamps to Ask");
        Check.Equal(ApprovalMode.Deny, PermissionGate.EffectiveMode(grant, new ToolDescriptor("read", "", schema, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(1))), "grant can tighten to Deny");
        Check.Equal(ApprovalMode.Ask, PermissionGate.EffectiveMode(grant, new ToolDescriptor("w", "", schema, ToolRisk.Write, null, TimeSpan.FromSeconds(1))), "Write defaults to Ask");
        Check.Equal(ApprovalMode.Auto, PermissionGate.EffectiveMode(grant, new ToolDescriptor("r", "", schema, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(1))), "ReadOnly defaults to Auto");
        return Task.CompletedTask;
    }

    public static Task Generators()
    {
        string conf = WslIsolation.RenderWslConf("dev");
        Check.True(conf.Contains("[automount]\nenabled = false") && conf.Contains("[interop]\nenabled = false"), "wsl.conf disables automount and interop");
        string fstab = WslIsolation.RenderFstabLine(Path.Combine(Path.GetTempPath(), "My Exchange"), "/mnt/mediator", readOnly: false);
        Check.True(fstab.Contains("My\\040Exchange") && fstab.Contains(" drvfs rw,"), "fstab escapes spaces");

        string inDir = TempDir(), outDir = TempDir();
        string wsb = WindowsSandboxConfig.ForUntrustedRun(inDir, outDir, "cmd /c echo <hi> & exit");
        var x = System.Xml.Linq.XDocument.Parse(wsb);
        Check.Equal("Disable", x.Root!.Element("Networking")!.Value, "sandbox networking off by default");
        Check.Equal("true", x.Root.Element("MappedFolders")!.Elements().First().Element("ReadOnly")!.Value, "inputs read-only");
        Check.Equal("cmd /c echo <hi> & exit", x.Root.Element("LogonCommand")!.Element("Command")!.Value, "logon command escaped correctly in XML");

        var jobs = new JobRegistry();
        var grant = jobs.Issue("job-gen", ["swx_part_cards"], [TempDir()], TimeSpan.FromMinutes(5));
        var ws = JobWorkspaceFactory.Create(TempDir(), grant, "/opt/mediator/mediator-mcp-bridge", BusEndpoint.Default, TimeSpan.FromMinutes(20));
        byte[] raw = File.ReadAllBytes(ws.ClaudeMcpConfigPath);
        Check.True(raw[0] == (byte)'{', "MCP config has no BOM");
        var claude = JsonNode.Parse(raw)!;
        var argv = claude["mcpServers"]!["mediator"]!["args"]!.AsArray().Select(n => (string)n!).ToList();
        Check.True(argv.SequenceEqual(new[] { "--job-id", "job-gen", "--endpoint", "pipe:mediator-bus", "--timeout-seconds", "1200" }), "bridge args carry no token");
        Check.True(!argv.Any(a => a.Contains(grant.Token, StringComparison.Ordinal)), "the token is not in argv");
        var gemini = JsonNode.Parse(File.ReadAllText(ws.GeminiSettingsPath))!;
        Check.True(ws.GeminiSettingsPath.EndsWith(Path.Combine(".agents", "mcp_config.json"), StringComparison.Ordinal) && (long)gemini["mcpServers"]!["mediator"]!["timeoutSeconds"]! > 1_200, "Antigravity workspace MCP config; timeout exceeds bridge timeout");
        Check.Equal(grant.Token, (string?)claude["mcpServers"]!["mediator"]!["env"]!["MEDIATOR_JOB_TOKEN"], "Claude config passes the token in env");
        Check.Equal(grant.Token, (string?)gemini["mcpServers"]!["mediator"]!["env"]!["MEDIATOR_JOB_TOKEN"], "Gemini config passes the token in env");
        if (!OperatingSystem.IsWindows())
            Check.True(File.GetUnixFileMode(ws.ClaudeMcpConfigPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "config files are owner-only");
        return Task.CompletedTask;
    }

    // ── Bridge and bus ────────────────────────────────────────────────────────────────────────────────

    sealed class FakeTools : IToolProvider
    {
        public IReadOnlyList<ToolDescriptor> Tools { get; } =
        [
            new("echo", "Echo text back.", (JsonObject)JsonNode.Parse("""{"type":"object","required":["text"],"properties":{"text":{"type":"string"}}}""")!, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(10)),
            new("write_thing", "Pretend to write.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"value":{"type":"string"}}}""")!, ToolRisk.Write, "fakeapp", TimeSpan.FromSeconds(10)),
            new("slow", "Sleeps.", (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"ms":{"type":"integer"}}}""")!, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(30)),
            new("hidden", "Not granted to the job.", new JsonObject { ["type"] = "object" }, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(10)),
        ];
        public int SlowCompleted;
        public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) =>
            Task.FromResult<JsonNode?>(new JsonObject { ["kind"] = "fake", ["value"] = call.Arguments.TryGetProperty("value", out var v) ? v.GetString() : null });
        public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
        {
            switch (call.Tool.Name)
            {
                case "echo": return ToolResult.Ok("echo: " + call.Arguments.GetProperty("text").GetString());
                case "write_thing": return ToolResult.Ok("wrote " + call.Arguments.GetProperty("value").GetString());
                case "slow":
                    await Task.Delay(call.Arguments.GetProperty("ms").GetInt32(), ct);
                    Interlocked.Increment(ref SlowCompleted);
                    return ToolResult.Ok("slept");
                default: return ToolResult.Fail("nope");
            }
        }
    }

    sealed class ScriptedPrompt : IApprovalPrompt
    {
        public readonly List<ApprovalRequest> Seen = new();
        public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
        {
            lock (Seen) Seen.Add(request);
            // Approve unless the value says "deny".
            bool deny = (request.Preview?["value"]?.GetValue<string>() ?? "").Contains("deny");
            return Task.FromResult(deny ? ApprovalResponse.Deny : ApprovalResponse.ApproveAll);
        }
    }

    sealed record Harness(McpBusServer Bus, JobRegistry Jobs, JobGrant Grant, AuditLog Audit, ScriptedPrompt Prompt, FakeTools Tools, string PipeName) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Bus.DisposeAsync(); await Audit.DisposeAsync(); }
    }

    static async Task<Harness> StartBus()
    {
        string pipe = "mediator-test-" + Guid.NewGuid().ToString("N")[..10];
        var jobs = new JobRegistry();
        var registry = new ToolRegistry();
        var tools = new FakeTools();
        registry.Register(tools);
        var prompt = new ScriptedPrompt();
        var audit = await AuditLog.OpenAsync(Path.Combine(TempDir(), "audit.db"));
        var bus = new McpBusServer(new BusOptions { PipeName = pipe }, jobs, registry, new PermissionGate(prompt), new LeaseManager(), audit);
        bus.Start();
        var grant = jobs.Issue("job-e2e", ["echo", "write_thing", "slow"], [TempDir()], TimeSpan.FromMinutes(5));
        return new Harness(bus, jobs, grant, audit, prompt, tools, pipe);
    }

    sealed class BridgeProcess : IAsyncDisposable
    {
        private readonly Process _p;
        private readonly Stream _stdin;
        private readonly StreamReader _stdout;
        public StringBuilder Stderr { get; } = new();

        public BridgeProcess(string bridgeDll, string? token, params string[] args)
        {
            var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (token is not null) psi.Environment["MEDIATOR_JOB_TOKEN"] = token;
            psi.ArgumentList.Add(bridgeDll);
            foreach (var a in args) psi.ArgumentList.Add(a);
            _p = Process.Start(psi)!;
            _p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (Stderr) Stderr.AppendLine(e.Data); };
            _p.BeginErrorReadLine();
            _stdin = _p.StandardInput.BaseStream;
            _stdout = new StreamReader(_p.StandardOutput.BaseStream, new UTF8Encoding(false));
        }

        public async Task SendRawAsync(byte[] bytes) { await _stdin.WriteAsync(bytes); await _stdin.FlushAsync(); }
        public Task SendAsync(string json) => SendRawAsync(Encoding.UTF8.GetBytes(json + "\n"));

        public async Task<JsonNode> ReceiveAsync(TimeSpan? timeout = null)
        {
            var line = await _stdout.ReadLineAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(15))
                ?? throw new Exception("bridge closed stdout. stderr: " + Stderr);
            return JsonNode.Parse(line)!;
        }

        public async Task<string?> TryReceiveLineAsync(TimeSpan wait)
        {
            try { return await _stdout.ReadLineAsync().WaitAsync(wait); }
            catch (TimeoutException) { return null; }
        }

        public async Task<int> CloseAndWaitAsync()
        {
            _stdin.Close();
            await _p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return _p.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_p.HasExited) { try { _stdin.Close(); await _p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { _p.Kill(true); } }
            _p.Dispose();
        }
    }

    public static async Task BridgeEndToEnd(string bridgeDll)
    {
        await using var h = await StartBus();
        await using var b = new BridgeProcess(bridgeDll, h.Grant.Token, "--job-id", h.Grant.JobId, "--endpoint", "pipe:" + h.PipeName);

        // A BOM before the first message must be tolerated.
        await b.SendRawAsync([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""" + "\n")]);
        var init = await b.ReceiveAsync();
        Check.Equal("2025-06-18", (string)init["result"]!["protocolVersion"]!, "negotiated version echoed");
        await b.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        await b.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var list = await b.ReceiveAsync();
        var names = list["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToList();
        Check.True(names.SequenceEqual(new[] { "echo", "slow", "write_thing" }), "only granted tools listed: " + string.Join(",", names));

        await b.SendAsync("""{"jsonrpc":"2.0","id":"é-3","method":"tools/call","params":{"name":"echo","arguments":{"text":"héllo"}}}""");
        var echo = await b.ReceiveAsync();
        Check.Equal("é-3", (string)echo["id"]!, "non-ASCII string id round-trips (escaping-independent matching)");
        Check.Equal("echo: héllo", (string)echo["result"]!["content"]![0]!["text"]!, "echo result");
        Check.Equal(false, (bool)echo["result"]!["isError"]!, "echo not an error");

        await b.SendAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"write_thing","arguments":{"value":"ok"}}}""");
        var approved = await b.ReceiveAsync();
        Check.Equal("wrote ok", (string)approved["result"]!["content"]![0]!["text"]!, "approved write ran");

        await b.SendAsync("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"write_thing","arguments":{"value":"please deny"}}}""");
        var denied = await b.ReceiveAsync();
        Check.Equal(true, (bool)denied["result"]!["isError"]!, "denied write is a tool error");
        Check.True(((string)denied["result"]!["content"]![0]!["text"]!).Contains("denied"), "denial explained to the model");

        await b.SendAsync("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"hidden","arguments":{}}}""");
        var hidden = await b.ReceiveAsync();
        Check.Equal(JsonRpc.InvalidParams, (int)hidden["error"]!["code"]!, "ungranted tool is unknown even if called directly");

        await b.SendAsync("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"echo","arguments":{}}}""");
        var invalid = await b.ReceiveAsync();
        Check.True(((string)invalid["result"]!["content"]![0]!["text"]!).Contains("Missing required argument 'text'"), "argument validation");

        await b.SendAsync("""{"jsonrpc":"2.0","id":8,"method":"resources/list"}""");
        var notFound = await b.ReceiveAsync();
        Check.Equal(JsonRpc.MethodNotFound, (int)notFound["error"]!["code"]!, "unsupported method");

        await b.SendAsync("not json at all");
        var parse = await b.ReceiveAsync();
        Check.Equal(JsonRpc.ParseError, (int)parse["error"]!["code"]!, "parse error answered locally");

        Check.Equal(2, h.Prompt.Seen.Count, "two approvals asked (ok + deny), none for read-only");
        Check.Equal("fake", (string)h.Prompt.Seen[0].Preview!["kind"]!, "provider preview reached the UI");
        Check.True(await h.Audit.CountAsync(h.Grant.JobId) >= 5, "calls audited");

        Check.Equal(0, await b.CloseAndWaitAsync(), "clean exit on stdin EOF");
    }

    public static async Task BridgeTimeout(string bridgeDll)
    {
        await using var h = await StartBus();
        await using var b = new BridgeProcess(bridgeDll, h.Grant.Token, "--job-id", h.Grant.JobId, "--endpoint", "pipe:" + h.PipeName, "--timeout-seconds", "2");
        await b.SendAsync("""{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"slow","arguments":{"ms":5000}}}""");
        var timedOut = await b.ReceiveAsync(TimeSpan.FromSeconds(10));
        Check.Equal(10, (int)timedOut["id"]!, "timeout answers the right id");
        Check.Equal(JsonRpc.MediatorTimeout, (int)timedOut["error"]!["code"]!, "bridge timeout code");

        // The bridge forwarded notifications/cancelled, so the Core stops the slow tool; and even if a late
        // reply arrived it must be dropped. Either way nothing more may appear for id 10.
        await b.SendAsync("""{"jsonrpc":"2.0","id":11,"method":"ping"}""");
        var next = JsonNode.Parse((await b.TryReceiveLineAsync(TimeSpan.FromSeconds(8)))!)!;
        Check.Equal(11, (int)next["id"]!, "next message is the ping reply, not a duplicate for id 10");
        Check.True(await b.TryReceiveLineAsync(TimeSpan.FromSeconds(4)) is null, "no late duplicate for id 10");
        Check.Equal(0, h.Tools.SlowCompleted, "Core cancelled the slow tool");
    }

    public static async Task BridgeRejected(string bridgeDll)
    {
        await using var h = await StartBus();
        await using var b = new BridgeProcess(bridgeDll, "wrong-token", "--job-id", h.Grant.JobId, "--endpoint", "pipe:" + h.PipeName);
        // The bridge connects eagerly, so it may already have exited when this write happens.
        try { await b.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""); } catch (IOException) { }
        string? line = await b.TryReceiveLineAsync(TimeSpan.FromSeconds(10));
        int code = await b.CloseAndWaitAsync();
        Check.Equal(3, code, "exit code 3 on rejection");
        Check.True(b.Stderr.ToString().Contains("rejected"), "rejection explained on stderr");
        if (line is not null) Check.Equal(JsonRpc.MediatorUnavailable, (int)JsonNode.Parse(line)!["error"]!["code"]!, "pending request failed cleanly");
    }
}
