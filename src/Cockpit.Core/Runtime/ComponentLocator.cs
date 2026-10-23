using System.Diagnostics;

namespace Cockpit.Core.Runtime;

/// <summary>How to start one of the Cockpit's own executables: an apphost (.exe) directly, or a .dll through `dotnet`.</summary>
public sealed record ComponentCommand(string FileName, IReadOnlyList<string> PrefixArguments)
{
    public string Display => PrefixArguments.Count == 0 ? FileName : $"{FileName} {string.Join(' ', PrefixArguments)}";

    public ProcessStartInfo ToStartInfo(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(FileName) { UseShellExecute = false };
        foreach (var a in PrefixArguments.Concat(arguments)) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>
/// Finds cockpit-mcp-bridge and cockpit-swx-worker (and, through <see cref="FindDesktopApp"/>, the desktop apps). Order: an explicit path, next to the running host
/// (the shipped layout), then the repository's own build output (so `dotnet run` of the CLI host works).
/// </summary>
public static class ComponentLocator
{
    public const string Bridge = "cockpit-mcp-bridge";
    public const string SwxWorker = "cockpit-swx-worker";

    private static readonly Dictionary<string, string> ProjectOf = new(StringComparer.Ordinal)
    {
        [Bridge] = "Cockpit.Bridge",
        [SwxWorker] = "Cockpit.SwxWorker",
    };

    /// <summary>
    /// V8: the desktop applications the Cockpit drives (SolidWorks, DaVinci Resolve, Blender) are not our components
    /// and are rarely on PATH; <see cref="DesktopAppLocator"/> finds them through the registry and the standard install
    /// folders. This is the one entry point the rest of the Core uses.
    /// </summary>
    public static DesktopAppInstall? FindDesktopApp(DesktopApp app) => DesktopAppLocator.Default.Find(app);

    public static ComponentCommand? Find(string name, string? explicitPath = null, string? baseDirectory = null)
    {
        if (explicitPath is not null) return FromFile(Path.GetFullPath(explicitPath));

        string baseDir = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var candidate in new[] { name + ".exe", name, name + ".dll" })
        {
            string path = Path.Combine(baseDir, candidate);
            if (File.Exists(path) && (candidate != name || !OperatingSystem.IsWindows())) return FromFile(path);
        }

        // Development fallback: <repo>/src/<Project>/bin/<Configuration>/<tfm>/<name>.dll
        if (ProjectOf.TryGetValue(name, out var project))
        {
            for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
            {
                var bin = new DirectoryInfo(Path.Combine(dir.FullName, "src", project, "bin"));
                if (!bin.Exists) continue;
                var dll = bin.EnumerateFiles(name + ".dll", SearchOption.AllDirectories)
                    .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (dll is not null) return FromFile(dll.FullName);
            }
        }
        return null;
    }

    private static ComponentCommand? FromFile(string path)
    {
        if (!File.Exists(path)) return null;
        return path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new ComponentCommand(DotnetHost(), [path])
            : new ComponentCommand(path, []);
    }

    /// <summary>The dotnet host running us when there is one (so the same runtime starts our .dll children).</summary>
    public static string DotnetHost()
    {
        string? self = Environment.ProcessPath;
        if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return self;
        return "dotnet";
    }
}
