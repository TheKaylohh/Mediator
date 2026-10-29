// VERIFY ONLY. A strict, in-memory stand-in for the parts of the SolidWorks object model the worker uses.
// It enforces the rules that break real automation: AddMate5 needs exactly two pre-selected entities that are
// assembly-context proxies (from Component2.GetCorrespondingEntity) selected with mark 1; parts must be
// loaded before AddComponent5; surfaces expose the documented CylinderParams / PlaneParams array layouts.
using System.Text;
using Mediator.SwxWorker;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Mediator.Tests.Fakes;

sealed class FakeSelection
{
    public readonly List<(object Entity, int Mark)> Items = new();
    public void Add(bool append, object entity, SelectData? data)
    {
        if (!append) Items.Clear();
        Items.Add((entity, data?.Mark ?? 0));
    }
    public void Add(bool append, object entity, int mark)
    {
        if (!append) Items.Clear();
        Items.Add((entity, mark));
    }
}

sealed class FakeSldWorks : SldWorks
{
    public readonly Dictionary<string, FakePart> Parts = new(StringComparer.OrdinalIgnoreCase);
    public readonly HashSet<string> Open = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<FakeAssembly> Assemblies = new();
    public readonly List<int> OpenOptions = new();
    public readonly List<string> Closed = new();
    public string TemplatePath { get; set; }
    public FakeSelection? ActiveSelection => Assemblies.LastOrDefault()?.Selection;

    public FakeSldWorks(string templatePath) => TemplatePath = templatePath;

    public FakePart AddPart(string path) => Parts[path] = new FakePart(this, path);

    public bool Visible { get; set; }
    public bool CommandInProgress { get; set; }
    public string RevisionNumber() => "32.1.0";
    public int GetProcessID() => 4242;
    public object GetOpenDocumentByName(string DocumentName) => Open.Contains(DocumentName) ? Parts[DocumentName] : null!;

    /// <summary>When set, OpenDoc6 signals <see cref="OpenEntered"/> and blocks until the gate opens (a stalled COM call).</summary>
    public ManualResetEventSlim? OpenGate { get; set; }
    public ManualResetEventSlim OpenEntered { get; } = new();

    public ModelDoc2 OpenDoc6(string FileName, int Type, int Options, string Configuration, ref int Errors, ref int Warnings)
    {
        OpenEntered.Set();
        OpenGate?.Wait();
        OpenOptions.Add(Options);
        if (!Parts.TryGetValue(FileName, out var part)) { Errors = 2; return null!; }
        Open.Add(FileName);
        return part;
    }

    public object NewDocument(string TemplateName, int PaperSize, double Width, double Height)
    {
        var asm = new FakeAssembly(this);
        Assemblies.Add(asm);
        return asm;
    }

    public string GetUserPreferenceStringValue(int UserPreferenceValue) => TemplatePath;
    public readonly Dictionary<int, bool> Toggles = new();
    public readonly Dictionary<int, int> Integers = new();
    public bool GetUserPreferenceToggle(int UserPreferenceValue) => Toggles.GetValueOrDefault(UserPreferenceValue);
    public void SetUserPreferenceToggle(int UserPreferenceValue, bool OnFlag) => Toggles[UserPreferenceValue] = OnFlag;
    public int GetUserPreferenceIntegerValue(int UserPreferenceValue) => Integers.GetValueOrDefault(UserPreferenceValue);
    public bool SetUserPreferenceIntegerValue(int UserPreferenceValue, int Value) { Integers[UserPreferenceValue] = Value; return true; }

    /// <summary>STEP files this fake can "import": path → geometry to give the imported part.</summary>
    public readonly Dictionary<string, Action<FakePart>> StepFiles = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> ImportDataRequested = new();
    public object GetImportFileData(string FileName) { ImportDataRequested.Add(FileName); return new object(); }
    /// <summary>
    /// When LoadFile4 is asked for one of these STEP files it returns the user's already-open document instead of a new
    /// one (what SolidWorks does when a document with the same name is already open in the session).
    /// </summary>
    public readonly Dictionary<string, FakePart> ReturnsExisting = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Documents created by LoadFile4 and not yet closed.</summary>
    public readonly List<FakePart> Imported = new();
    public ModelDoc2 LoadFile4(string FileName, string ArgString, object ImportData, ref int Errors)
    {
        if (ReturnsExisting.TryGetValue(FileName, out var existing)) return existing;
        if (ImportData is null || !StepFiles.TryGetValue(FileName, out var shape)) { Errors = 1; return null!; }
        var doc = new FakePart(this, FileName) { IsImported = true };
        shape(doc);
        Imported.Add(doc);
        return doc;
    }
    public void CloseDoc(string Name)
    {
        Closed.Add(Name);
        Open.Remove(Name);
        Imported.RemoveAll(d => d.GetTitle() == Name);
        foreach (var p in Parts.Values.Where(p => p.GetTitle() == Name)) Open.Remove(p.Path);
    }
    public int GetDocumentCount() => Open.Count + Imported.Count;
    public object GetDocuments() => Open.Where(Parts.ContainsKey).Select(p => (object)Parts[p]).Concat(Imported).ToArray();
    public void ExitApp() { }
}

sealed class FakeSurface : Surface
{
    private readonly double[]? _cylinder; // origin X,Y,Z; axis I,J,K; radius
    private readonly double[]? _plane;    // normal I,J,K; root X,Y,Z
    private FakeSurface(double[]? cylinder, double[]? plane) { _cylinder = cylinder; _plane = plane; }
    public static FakeSurface Cylinder(double[] origin, double[] axis, double radius) => new([.. origin, .. axis, radius], null);
    public static FakeSurface Plane(double[] normal, double[] root) => new(null, [.. normal, .. root]);
    public bool IsCylinder() => _cylinder is not null;
    public bool IsPlane() => _plane is not null;
    public object CylinderParams => _cylinder?.Clone() ?? throw new InvalidOperationException("not a cylinder");
    public object PlaneParams => _plane?.Clone() ?? throw new InvalidOperationException("not a plane");
    public double Radius => _cylinder![6];
}

sealed class FakeFace : Face2, Entity
{
    public FakePart Part { get; }
    public string Id { get; }
    public FakeSurface Surface { get; }
    public double Area { get; }
    public bool Sense { get; }
    public FakeFace(FakePart part, string id, FakeSurface surface, double area, bool sense) { Part = part; Id = id; Surface = surface; Area = area; Sense = sense; }
    public object GetSurface() => Surface;
    public double GetArea() => Area;
    public bool FaceInSurfaceSense() => Sense;
    // Selecting a raw part-space face while an assembly is active: allowed, but AddMate5 will reject it.
    public bool Select4(bool Append, SelectData Data) { Part.App.ActiveSelection?.Add(Append, this, Data); return true; }
    public FakeFace CopyTo(FakePart other) => new(other, Id, Surface, Area, Sense);
}

sealed class FakeBody : Body2
{
    private readonly object[] _faces;
    public FakeBody(IEnumerable<FakeFace> faces) => _faces = faces.Cast<object>().ToArray();
    public object GetFaces() => _faces;
}

sealed class FakeFeature : Feature
{
    private readonly List<FakeFeature> _siblings;
    public readonly List<FakeFeature> Subs = new();
    public string Name { get; set; }
    public string TypeName { get; }
    public int ErrorCode { get; set; }
    public FakeFeature(List<FakeFeature> siblings, string name, string typeName) { _siblings = siblings; Name = name; TypeName = typeName; }
    public string GetTypeName2() => TypeName;
    public object GetNextFeature() => Next();
    public object GetFirstSubFeature() => Subs.FirstOrDefault()!;
    public object GetNextSubFeature() => Next();
    public int GetErrorCode2(out bool IsWarning) { IsWarning = false; return ErrorCode; }
    /// <summary>Set by FakeComponent.FeatureByName: the component this assembly-context copy belongs to.</summary>
    public FakeComponent? Owner { get; set; }
    public bool Select2(bool Append, int Mark) { if (Owner is null) return false; Owner.Assembly.Selection.Add(Append, this, Mark); return true; }
    private object Next() { int i = _siblings.IndexOf(this); return i >= 0 && i + 1 < _siblings.Count ? _siblings[i + 1] : null!; }
}

sealed class FakeFeatureManager : FeatureManager { public bool EnableFeatureTree { get; set; } = true; }
sealed class FakeView : ModelView { public bool EnableGraphicsUpdate { get; set; } = true; }
sealed class FakeMass : MassProperty { public double Mass { get; init; } }
sealed class FakeMate : Mate2 { public int Type { get; init; } }
sealed class FakeSelectData : SelectData { public int Mark { get; set; } }
sealed class FakeSelectionMgr : SelectionMgr { public object CreateSelectData() => new FakeSelectData(); }

sealed class FakePart : ModelDoc2, PartDoc, ModelDocExtension
{
    private readonly Dictionary<object, string> _names = new(ReferenceEqualityComparer.Instance);
    private readonly List<FakeFeature> _features = new();
    public FakeSldWorks App { get; }
    public string Path { get; }
    public readonly List<FakeFace> Faces = new();
    public bool PersistRefsInvalid { get; set; }
    public int SaveAttempts { get; private set; }
    public bool Dirty { get; private set; }

    public FakePart(FakeSldWorks app, string path)
    {
        App = app; Path = path;
        // Like every SolidWorks part: the three default planes come first (names may be renamed or translated).
        foreach (var plane in new[] { "Plan de face", "Top Plane", "Right Plane" }) _features.Add(new FakeFeature(_features, plane, "RefPlane"));
    }
    public IReadOnlyList<FakeFeature> Features => _features;

    public FakePart Cylinder(string id, double[] origin, double[] axis, double radius, double area)
    {
        Faces.Add(new FakeFace(this, id, FakeSurface.Cylinder(origin, axis, radius), area, true));
        return this;
    }

    public FakePart Plane(string id, double[] normal, double[] root, double area, bool sense = true)
    {
        Faces.Add(new FakeFace(this, id, FakeSurface.Plane(normal, root), area, sense));
        return this;
    }

    public FakePart MateReference(string name)
    {
        _features.Add(new FakeFeature(_features, name, "MateReference"));
        return this;
    }

    public FakeFace Face(string id) => Faces.Single(f => f.Id == id);

    // ModelDoc2
    public ModelDocExtension Extension => this;
    public FeatureManager FeatureManager { get; } = new FakeFeatureManager();
    public object ActiveView => null!;
    public object SelectionManager { get; } = new FakeSelectionMgr();
    public string GetTitle() => System.IO.Path.GetFileName(Path);
    public string GetPathName() => Path;
    int ModelDoc2.GetType() => (int)swDocumentTypes_e.swDocPART;
    public object GetConfigurationNames() => new[] { "Default", "Short" };
    public bool ForceRebuild3(bool TopOnly) => true;
    public bool ClearSelection2(bool All) => true;
    public object FirstFeature() => _features.FirstOrDefault()!;
    public bool GetSaveFlag() => Dirty;

    // ModelDocExtension
    public object GetPersistReference3(object Dispatch) => Encoding.UTF8.GetBytes($"{Path}#{((FakeFace)Dispatch).Id}");
    public object GetObjectByPersistReference3(object PersistReference, out int ErrorCode)
    {
        string key = Encoding.UTF8.GetString((byte[])PersistReference);
        var face = PersistRefsInvalid ? null : Faces.FirstOrDefault(f => $"{Path}#{f.Id}" == key);
        ErrorCode = face is null ? (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Invalid : (int)swPersistReferencedObjectStates_e.swPersistReferencedObject_Ok;
        return face!;
    }
    public bool StartRecordingUndoObject() => true;
    public bool FinishRecordingUndoObject2(string Name, bool Hidden) => true;
    public int GetUserPreferenceInteger(int UserPreferenceValue, int UserPreferenceOption) => (int)swLengthUnit_e.swMM;
    public MassProperty CreateMassProperty() => new FakeMass { Mass = 0.25 };
    /// <summary>Source parts are never saved (attempts are counted). A document created by a STEP import may be saved once, as a new part.</summary>
    public bool SaveAs3(string Name, int Version, int Options, object ExportData, object AdvancedSaveAsOptions, ref int Errors, ref int Warnings)
    {
        SaveAttempts++;
        if (!IsImported || File.Exists(Name)) return false;
        File.WriteAllText(Name, "fake part from " + Path);
        var saved = App.AddPart(Name);
        saved.Faces.AddRange(Faces.Select(f => f.CopyTo(saved)));
        return true;
    }
    public bool IsImported { get; init; }

    // PartDoc
    /// <summary>Imported STEP that yields only surface bodies (no solid).</summary>
    public bool SurfaceOnly { get; set; }
    public object GetBodies2(int BodyType, bool BVisibleOnly) =>
        (BodyType == (int)swBodyType_e.swSolidBody) == !SurfaceOnly ? new object[] { new FakeBody(Faces) } : Array.Empty<object>();
    public object GetPartBox(bool Precise) => new[] { -0.01, -0.01, 0.0, 0.01, 0.01, 0.02 };
    public bool SetEntityName(object Entity, string Name) { _names[Entity] = Name; Dirty = true; return true; }
    public string GetEntityName(object Entity) => _names.TryGetValue(Entity, out var n) ? n : "";
    public object GetEntityByName(string Name, int EntityType) => _names.FirstOrDefault(kv => kv.Value == Name).Key!;
}

sealed class FakeComponent : Component2
{
    public FakeAssembly Assembly { get; }
    public FakePart Part { get; }
    public double[] Position { get; }
    public bool Fixed { get; set; }
    public string Name2 { get; }
    public FakeComponent(FakeAssembly asm, FakePart part, string name, double[] position) { Assembly = asm; Part = part; Name2 = name; Position = position; }
    public object GetModelDoc2() => Part;
    public object GetCorrespondingEntity(object Entity) =>
        Entity is FakeFace f && ReferenceEquals(f.Part, Part) ? new FakeMappedFace(this, f) : null!;
    public bool Select4(bool Append, SelectData Data, bool ShowPopup) { Assembly.Selection.Add(Append, this, Data); return true; }
    public object FeatureByName(string Name) => Part.Features.FirstOrDefault(f => f.Name == Name) is { } f
        ? new FakeFeature([], f.Name, f.TypeName) { Owner = this } : null!;
}

/// <summary>An assembly-context proxy for a part face, as returned by GetCorrespondingEntity.</summary>
sealed class FakeMappedFace : Entity
{
    public FakeComponent Component { get; }
    public FakeFace Face { get; }
    public FakeMappedFace(FakeComponent component, FakeFace face) { Component = component; Face = face; }
    public bool Select4(bool Append, SelectData Data) { Component.Assembly.Selection.Add(Append, this, Data); return true; }
}

sealed record CreatedMate(swMateType_e Type, object A, object B, double RadiusA, double RadiusB);

sealed class FakeAssembly : ModelDoc2, AssemblyDoc, ModelDocExtension
{
    private readonly List<FakeFeature> _features = new();
    private readonly FakeFeature _mateGroup;
    public FakeSldWorks App { get; }
    public FakeSelection Selection { get; } = new();
    public readonly List<FakeComponent> Components = new();
    public readonly List<CreatedMate> Mates = new();
    public int UndoStarts { get; private set; }
    public readonly List<string> UndoFinishes = new();
    public string? SavedPath { get; private set; }
    public string? LastRejection { get; private set; }

    public FakeAssembly(FakeSldWorks app)
    {
        App = app;
        _features.Add(new FakeFeature(_features, "Origin", "OriginProfileFeature"));
        _mateGroup = new FakeFeature(_features, "Mates", "MateGroup");
        _features.Add(_mateGroup);
    }

    // ModelDoc2
    public ModelDocExtension Extension => this;
    public FeatureManager FeatureManager { get; } = new FakeFeatureManager();
    public object ActiveView { get; } = new FakeView();
    public object SelectionManager { get; } = new FakeSelectionMgr();
    public string GetTitle() => "Assem1";
    public string GetPathName() => SavedPath ?? "";
    int ModelDoc2.GetType() => (int)swDocumentTypes_e.swDocASSEMBLY;
    public object GetConfigurationNames() => new[] { "Default" };
    public bool ForceRebuild3(bool TopOnly) => true;
    public bool ClearSelection2(bool All) { Selection.Items.Clear(); return true; }
    public object FirstFeature() => _features[0];
    public bool GetSaveFlag() => SavedPath is null;

    // ModelDocExtension
    public object GetPersistReference3(object Dispatch) => throw new NotSupportedException();
    public object GetObjectByPersistReference3(object PersistReference, out int ErrorCode) => throw new NotSupportedException();
    public bool StartRecordingUndoObject() { UndoStarts++; return true; }
    public bool FinishRecordingUndoObject2(string Name, bool Hidden) { UndoFinishes.Add(Name); return true; }
    public int GetUserPreferenceInteger(int UserPreferenceValue, int UserPreferenceOption) => (int)swLengthUnit_e.swMM;
    public MassProperty CreateMassProperty() => new FakeMass { Mass = 1 };
    public bool SaveAs3(string Name, int Version, int Options, object ExportData, object AdvancedSaveAsOptions, ref int Errors, ref int Warnings)
    {
        File.WriteAllText(Name, "fake assembly");
        SavedPath = Name;
        return true;
    }

    // AssemblyDoc
    public Component2 AddComponent5(string CompName, int ConfigOption, string NewConfigName, bool UseConfigForPartReferences,
        string ExistingConfigName, double X, double Y, double Z)
    {
        if (!App.Open.Contains(CompName)) return null!; // real SolidWorks needs the part loaded first
        var part = App.Parts[CompName];
        var comp = new FakeComponent(this, part, $"{System.IO.Path.GetFileNameWithoutExtension(CompName)}-{Components.Count(c => c.Part == part) + 1}", [X, Y, Z]);
        Components.Add(comp);
        return comp;
    }

    /// <summary>Like SolidWorks: fixes whatever components are selected, and silently does nothing when none is.</summary>
    public void FixComponent()
    {
        var selected = Selection.Items.Select(i => i.Entity).OfType<FakeComponent>().ToList();
        if (selected.Count == 0) FixWithNothingSelected++;
        if (Selection.Items.Count != selected.Count) FixWithOtherSelections++;
        foreach (var c in selected) c.Fixed = true;
    }
    public int FixWithNothingSelected { get; private set; }
    public int FixWithOtherSelections { get; private set; }

    public Mate2 AddMate5(int MateTypeFromEnum, int AlignFromEnum, bool Flip, double Distance, double DistanceAbsUpperLimit,
        double DistanceAbsLowerLimit, double GearRatioNumerator, double GearRatioDenominator, double Angle,
        double AngleAbsUpperLimit, double AngleAbsLowerLimit, bool ForPositioningOnly, bool LockRotation,
        int WidthMateOption, out int ErrorStatus)
    {
        Mate2 Reject(string why)
        {
            LastRejection = why;
            return null!;
        }
        ErrorStatus = (int)swAddMateError_e.swAddMateError_IncorrectSelections;
        if (Selection.Items.Count != 2) return Reject($"expected 2 selected entities, got {Selection.Items.Count}");
        // An assembly-context face proxy, or a component's reference plane (FeatureByName in the assembly).
        bool InThisAssembly(object e) => e is FakeMappedFace m ? ReferenceEquals(m.Component.Assembly, this)
            : e is FakeFeature { TypeName: "RefPlane", Owner: { } o } && ReferenceEquals(o.Assembly, this);
        if (Selection.Items.Any(i => !InThisAssembly(i.Entity)))
            return Reject("a selected entity is a part-space face, not an assembly-context proxy");
        if (Selection.Items.Any(i => i.Mark != 1))
            return Reject("selection mark must be 1 for standard mates");

        object a = Selection.Items[0].Entity, b = Selection.Items[1].Entity;
        static bool Cyl(object e) => e is FakeMappedFace m && m.Face.Surface.IsCylinder();
        static bool Flat(object e) => e is FakeFeature || e is FakeMappedFace m && m.Face.Surface.IsPlane();
        var type = (swMateType_e)MateTypeFromEnum;
        if (type == swMateType_e.swMateCONCENTRIC && !(Cyl(a) && Cyl(b)))
            return Reject("concentric needs two cylinders");
        if (type is swMateType_e.swMateCOINCIDENT or swMateType_e.swMatePARALLEL or swMateType_e.swMateDISTANCE && !(Flat(a) && Flat(b)))
            return Reject($"{type} needs two planes");

        double ra = Cyl(a) ? ((double[])((FakeMappedFace)a).Face.Surface.CylinderParams)[6] : double.NaN;
        double rb = Cyl(b) ? ((double[])((FakeMappedFace)b).Face.Surface.CylinderParams)[6] : double.NaN;
        Mates.Add(new CreatedMate(type, a, b, ra, rb));
        _mateGroup.Subs.Add(new FakeFeature(_mateGroup.Subs, $"{type}{_mateGroup.Subs.Count + 1}", "MateConcentric"));
        ErrorStatus = (int)swAddMateError_e.swAddMateError_NoError;
        return new FakeMate { Type = MateTypeFromEnum };
    }
}

sealed class FakeConnector : ISwxConnector
{
    private readonly SldWorks _app;
    public FakeConnector(SldWorks app) => _app = app;
    public SwxConnection Connect(SwxHostOptions options) => new(_app, LaunchedByMediator: false);
}

/// <summary>Reports a dialog for the fake SolidWorks PID once <see cref="AppearAt"/> has passed, until dismissed.</summary>
sealed class FakeDialogDetector : ISwxDialogDetector
{
    public DateTimeOffset? AppearAt { get; set; }
    public int Probes;
    public BlockingDialogInfo Dialog { get; } = new("SOLIDWORKS", "The referenced file shaft.sldprt was modified. Rebuild now?", ["Rebuild", "Don't rebuild"], "#32770");

    public BlockingDialogInfo? FindBlockingDialog(int processId)
    {
        Interlocked.Increment(ref Probes);
        return processId == 4242 && AppearAt is { } at && DateTimeOffset.UtcNow >= at ? Dialog : null;
    }
}
