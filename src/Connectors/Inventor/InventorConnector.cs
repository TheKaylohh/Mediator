using Mediator.Core.Cad;

namespace Mediator.Connectors.Inventor;

/// <summary>V18 connector: <see cref="InventorApp"/>'s tools, its routing and its entry in the Software Library.</summary>
public sealed class InventorConnector() : CadAppConnector(folder => new InventorApp(overrideFolder: folder), InventorApp.Program, 22,
    ["inventor", "ipt", "iam", "idw"], "inventor");
