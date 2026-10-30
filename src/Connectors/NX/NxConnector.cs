using Mediator.Core.Cad;

namespace Mediator.Connectors.NX;

/// <summary>V18 connector: <see cref="NxApp"/>'s tools, its routing and its entry in the Software Library.</summary>
public sealed class NxConnector() : CadAppConnector(folder => new NxApp(overrideFolder: folder), NxApp.Program, 23,
    ["siemens nx", "nx open", "nx part", "ugraf"], "ugraf");
