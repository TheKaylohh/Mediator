using Mediator.Core.Cad;

namespace Mediator.Connectors.CATIA;

/// <summary>V18 connector: <see cref="CatiaApp"/>'s tools, its routing and its entry in the Software Library.</summary>
public sealed class CatiaConnector() : CadAppConnector(folder => new CatiaApp(overrideFolder: folder), CatiaApp.Program, 25,
    ["catia", "catpart", "catproduct", "catdrawing"], "cnext");
