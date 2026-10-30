using Mediator.Core.Cad;

namespace Mediator.Connectors.AutoCAD;

/// <summary>V18 connector: <see cref="AutoCadApp"/>'s tools, its routing and its entry in the Software Library.</summary>
public sealed class AutoCadConnector() : CadAppConnector(folder => new AutoCadApp(overrideFolder: folder), AutoCadApp.Program, 21,
    ["autocad", "dwg", "acad"], "acad");
