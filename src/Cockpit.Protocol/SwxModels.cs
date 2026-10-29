using System.Text.Json.Serialization;

namespace Cockpit.Protocol.Swx;

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
