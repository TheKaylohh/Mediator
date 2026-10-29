using System.Diagnostics;
using SolidWorks.Interop.sldworks;

namespace Cockpit.SwxWorker;

public sealed record SwxConnection(SldWorks App, bool LaunchedByCockpit);

/// <summary>
/// How the host obtains a SolidWorks application object. Production uses the Running Object Table;
/// tests inject a fake object model. Called on the host's COM thread.
/// </summary>
public interface ISwxConnector
{
    SwxConnection Connect(SwxHostOptions options);
}

/// <summary>Attaches to the user's running SolidWorks through the ROT, or launches it if allowed.</summary>
public sealed class RotSwxConnector : ISwxConnector
{
    public const string ProgId = "SldWorks.Application";

    public SwxConnection Connect(SwxHostOptions options)
    {
        if (Environment.IsPrivilegedProcess)
            throw new SwxException("cockpit_elevated", "The Cockpit worker is running as administrator. Run the Cockpit normally so it can see SolidWorks in the user session.");

        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (PInvokeRot.TryGetActiveObject(ProgId, out var instance, out int hr) && instance is SldWorks sw)
                return new SwxConnection(sw, LaunchedByCockpit: false);
            if (hr == PInvokeRot.CO_E_CLASSSTRING)
                throw new SwxException("not_installed", "SolidWorks is not registered on this machine (ProgID SldWorks.Application not found).");

            var running = Process.GetProcessesByName("SLDWORKS");
            try
            {
                if (running.Length == 0)
                {
                    if (options.AllowLaunch) return Launch();
                    throw new SwxException("not_running", "SolidWorks is not running. Start it, then retry.");
                }
                foreach (var p in running)
                {
                    if (ProcessIntegrity.IsInaccessibleOrMoreElevated(p.Id))
                        throw new SwxException("elevation_mismatch",
                            $"SolidWorks (PID {p.Id}) is running as administrator or as another user, so it is invisible to the Cockpit. Restart SolidWorks normally (not 'Run as administrator').");
                }
            }
            finally
            {
                foreach (var p in running) p.Dispose();
            }

            if (clock.Elapsed > options.AttachWait)
                throw new SwxException("attach_timeout",
                    $"SolidWorks is running but did not register for automation within {options.AttachWait.TotalSeconds:0} s. It may still be starting or showing a dialog.");
            Thread.Sleep(1000); // SolidWorks registers in the ROT only after its startup finishes
        }
    }

    private static SwxConnection Launch()
    {
        var type = Type.GetTypeFromProgID(ProgId, throwOnError: true)!;
        var sw = (SldWorks)Activator.CreateInstance(type)!;
        // A visible instance is never a hidden zombie: the user can see it and close it if the worker dies.
        sw.Visible = true;
        return new SwxConnection(sw, LaunchedByCockpit: true);
    }
}
