using Mediator.Core.Cad;

namespace Mediator.Connectors.Creo;

/// <summary>V18 connector: <see cref="CreoApp"/>'s tools, its routing and its entry in the Software Library.</summary>
public sealed class CreoConnector() : CadAppConnector(folder => new CreoApp(overrideFolder: folder), CreoApp.Program, 24,
    ["creo", "pro/engineer", "proe"], "xtop");
