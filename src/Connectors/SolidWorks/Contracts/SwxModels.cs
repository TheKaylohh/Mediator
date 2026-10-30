using System.Text.Json.Serialization;

namespace Mediator.Connectors.SolidWorks.Contracts;

// All lengths are meters and all areas square meters: the SolidWorks API's internal units,
// regardless of the part's display units (reported separately in PartCard.DisplayUnits).

/// <summary>Compact, model-friendly summary of one part. Typically 200-600 tokens as JSON.</summary>
public sealed record PartCard(
    string Path,
    string DisplayUnits,
    double[] BoundingBoxM,          // [xmin, ymin, zmin, xmax, ymax, zmax]
    double? MassKg,
    string[] Configurations,
    string[] MateReferences,
    IReadOnlyList<FaceInfo> Faces,
    IReadOnlyList<string> Warnings);

/// <summary>
/// One candidate mating face. The model refers to it only by <see cref="Id"/>; the persist reference,
/// entity name and geometry stay server-side and are used to re-find the face at build time.
/// </summary>
public sealed record FaceInfo(
    string Id,                       // "c1".."cN" cylinders, "p1".."pM" planes, stable per card
    FaceKind Kind,
    double AreaM2,
    double[] PointM,                 // cylinder: axis origin; plane: a point on the plane
    double[] DirectionUnit,          // cylinder: axis; plane: outward normal (corrected for face sense)
    double? RadiusM,
    string? PersistRefBase64,        // IModelDocExtension.GetPersistReference3 bytes
    string? EntityName,              // existing name (naming convention) or in-memory fallback name
    bool EntityNameIsFallback);

/// <summary>Result of extracting several parts: one failure does not hide the others.</summary>
public sealed record PartCardBatch(IReadOnlyList<PartCard> Cards, IReadOnlyList<PathError> Errors);

public sealed record PathError(string Path, string Code, string Message);

[JsonConverter(typeof(JsonStringEnumConverter<FaceKind>))]
public enum FaceKind { Cylinder, Plane }

/// <summary>
/// A part's three default planes, usable as mate entities by these face IDs (V10): "front" (XY, normal +Z), "top" (XZ,
/// normal +Y), "right" (YZ, normal +X). They are found by position in the feature tree (the first three reference planes),
/// so renamed or translated planes work too. They are how to centre parts whose faces offer no middle plane (a cylinder
/// on a block: its Right plane coincident with the block's Right plane).
/// </summary>
public static class StandardPlanes
{
    public static readonly IReadOnlyList<string> Ids = ["front", "top", "right"];

    /// <summary>Index 0..2 in the feature tree for a plane ID ("front", "Front Plane", "plane:top"...), or -1.</summary>
    public static int IndexOf(string? id)
    {
        string t = (id ?? "").Trim().ToLowerInvariant();
        if (t.StartsWith("plane:", StringComparison.Ordinal)) t = t[6..].Trim();
        if (t.EndsWith(" plane", StringComparison.Ordinal)) t = t[..^6].Trim();
        for (int i = 0; i < Ids.Count; i++) if (t == Ids[i]) return i;
        return -1;
    }

    /// <summary>The plane as a face (point = origin, normal along its axis, no persist reference).</summary>
    public static FaceInfo Face(int index) => new(Ids[index], FaceKind.Plane, 0, [0, 0, 0],
        index switch { 0 => [0, 0, 1], 1 => [0, 1, 0], _ => [1, 0, 0] }, null, null, null, false);

    public static bool IsStandard(FaceInfo face) => face.PersistRefBase64 is null && face.EntityName is null && IndexOf(face.Id) >= 0;
}

[JsonConverter(typeof(JsonStringEnumConverter<MateKind>))]
public enum MateKind { Coincident, Concentric, Parallel, Distance }

[JsonConverter(typeof(JsonStringEnumConverter<MateAlignment>))]
public enum MateAlignment { Closest, Aligned, AntiAligned }

/// <summary>What the model submits: components and mates that point at face IDs from part cards.</summary>
public sealed record AssemblyPlan(
    string OutputPath,
    string? TemplatePath,
    IReadOnlyList<PlanComponent> Components,
    IReadOnlyList<PlanMate> Mates);

public sealed record PlanComponent(string Key, string PartPath, string? Configuration, double[]? PositionM, bool Fixed);

public sealed record PlanMate(
    string Key,
    MateKind Type,
    string ComponentA, string FaceA,
    string ComponentB, string FaceB,
    MateAlignment Alignment,
    bool Flip,
    double? DistanceM,
    double? Confidence,
    string? Reason);

/// <summary>What the Core sends the worker: the same plan with face IDs resolved to full FaceInfo records.</summary>
public sealed record ResolvedAssemblyPlan(
    string OutputPath,
    string? TemplatePath,
    IReadOnlyList<PlanComponent> Components,
    IReadOnlyList<ResolvedMate> Mates);

public sealed record ResolvedMate(PlanMate Mate, FaceInfo FaceA, FaceInfo FaceB);

public sealed record BuildResult(
    string OutputPath,
    bool Saved,
    string UndoLabel,
    IReadOnlyList<ComponentResult> Components,
    IReadOnlyList<MateResult> Mates,
    IReadOnlyList<string> Warnings);

public sealed record ComponentResult(string Key, string ComponentName, bool Fixed);

[JsonConverter(typeof(JsonStringEnumConverter<MateStatus>))]
public enum MateStatus { Ok, Warning, Failed }

public sealed record MateResult(string Key, MateStatus Status, string? FeatureName, string? Detail);

/// <summary>One STEP file imported and saved as a new part. Error is "code: message" when it failed.</summary>
public sealed record StepImportResult(string Source, string? PartPath, string? Error);

public sealed record StepImportBatch(IReadOnlyList<StepImportResult> Results);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PartCard))]
[JsonSerializable(typeof(List<PartCard>))]
[JsonSerializable(typeof(PartCardBatch))]
[JsonSerializable(typeof(AssemblyPlan))]
[JsonSerializable(typeof(ResolvedAssemblyPlan))]
[JsonSerializable(typeof(BuildResult))]
[JsonSerializable(typeof(StepImportBatch))]
[JsonSerializable(typeof(string[]))]
public sealed partial class SwxJsonContext : JsonSerializerContext;
