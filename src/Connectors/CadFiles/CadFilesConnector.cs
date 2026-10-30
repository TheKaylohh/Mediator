using System.Text.RegularExpressions;
using Mediator.Core.Connectors;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;

namespace Mediator.Connectors.CadFiles;

/// <summary>V18 connector: CAD files without a CAD program: inspect meshes, STEP, IGES and profiles; convert meshes.</summary>
public sealed partial class CadFilesConnector : IMediatorConnector
{
    public string Id => "cad-files";
    public string DisplayName => "CAD files";
    public string Category => "CAD";
    public string Summary => "Read STL, OBJ, PLY, 3MF, STEP, IGES, DXF and SVG files and convert meshes, without a CAD program.";

    public static readonly string[] Tools = CadFileToolProvider.Names;
    public IReadOnlyList<string> ChatTools => Tools;

    public ConnectorInstance Start(ConnectorContext context) => new([new CadFileToolProvider()]);

    /// <summary>V15: a CAD file named by type or extension (".stl", "step file", "the 3mf"), not words like "step" or C++'s STL.</summary>
    [GeneratedRegex(@"\.(?:stl|obj|ply|3mf|step|stp|iges|igs|dxf)\b|\b(?:stl|step|stp|iges|igs|3mf|obj|ply|dxf)\s+(?:files?|models?|meshes?)\b|\bstls\b|\b3mf\b")]
    private static partial Regex CadFileWord();

    /// <summary>"stl" as a print file (".stl", "stl file"), not C++'s STL.</summary>
    [GeneratedRegex(@"\.stl\b|\bstls?\s+files?\b|\bstls\b")]
    private static partial Regex StlFile();

    public IReadOnlyList<ConnectorRoute> Routes { get; } =
        [new(40, TaskCategory.Cad, Tools, r => CadFileWord().IsMatch(r.Text) || StlFile().IsMatch(r.Text))];

    public bool ReadsAttachment(string path) => CadFileInspector.IsCadFile(path);

    public string DescribeAttachment(string fullPath, int maxChars) => CadFileInspector.Describe(fullPath);
}
