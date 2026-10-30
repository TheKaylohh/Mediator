using Mediator.Core.Cad;

namespace Mediator.Connectors.FreeCAD;

/// <summary>V18 connector: <see cref="FreeCadApp"/>'s tools, its routing and its entry in the Software Library.</summary>
public sealed class FreeCadConnector() : CadAppConnector(folder => new FreeCadApp(overrideFolder: folder), FreeCadApp.Program, 20,
    ["freecad", "fcstd"], "freecad");
