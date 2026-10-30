using Mediator.Core.Isolation;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;

namespace Mediator.Tests;

/// <summary>V18: the connectors a test runtime starts, with stand-ins for the programs they drive.</summary>
internal static class TestConnectors
{
    public static IReadOnlyList<IMediatorConnector> All(ISwxBackend? swx = null, ComponentCommand? worker = null, IDesktopAppSession? session = null,
        Func<CancellationToken, Task<string?>>? activeDocument = null, IMediaProbe? mediaProbe = null, IBlenderRunner? blender = null, bool noFfmpeg = false,
        ISandboxRunner? sandbox = null, SandboxPython? sandboxPython = null, IOutlookClient? outlook = null) =>
    [
        new SolidWorksConnector { Backend = swx, Worker = worker, Session = session, ActiveDocument = activeDocument },
        new Mediator.Connectors.FreeCAD.FreeCadConnector(), new Mediator.Connectors.AutoCAD.AutoCadConnector(),
        new Mediator.Connectors.Inventor.InventorConnector(), new Mediator.Connectors.NX.NxConnector(),
        new Mediator.Connectors.Creo.CreoConnector(), new Mediator.Connectors.CATIA.CatiaConnector(),
        new PartsFromCodeConnector { Sandbox = sandbox, SandboxPython = sandboxPython },
        new CadFilesConnector(),
        new OfficeConnector { Outlook = outlook },
        new ResolveConnector { MediaProbe = mediaProbe },
        noFfmpeg ? new BlenderConnector { Blender = blender, Ffmpeg = null } : new BlenderConnector { Blender = blender },
        new TutorConnector(),
    ];
}
