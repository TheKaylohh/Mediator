using System.Reflection;
using System.Runtime.Loader;
using Mediator.Core.Diagnostics;

namespace Mediator.Core.Connectors;

/// <summary>
/// V18: finds the installed connectors. Each one is a folder under &lt;app&gt;\connectors holding Mediator.Connector.&lt;Name&gt;.dll
/// (plus anything it ships: scripts, the SolidWorks worker). A connector loads in its own load context, so its own
/// dependencies never clash with another's, while the types it shares with the app (Mediator.Core) come from the app.
/// A folder that fails to load is skipped and reported, never fatal. Removing a folder removes the feature.
/// </summary>
public static class ConnectorLoader
{
    public const string FolderName = "connectors";
    public const string AssemblyPrefix = "Mediator.Connector.";

    /// <summary>One loaded connector folder, or why it did not load.</summary>
    public sealed record LoadResult(string Folder, IReadOnlyList<IMediatorConnector> Connectors, string? Error);

    /// <summary>
    /// The connectors next to the app (&lt;baseDirectory&gt;\connectors), else, in a development checkout, the connector
    /// projects' build output (src/Connectors/*/bin). Sets nothing: the host passes the result to <see cref="ConnectorCatalog.Set"/>.
    /// </summary>
    public static IReadOnlyList<LoadResult> LoadAll(string? baseDirectory = null, IMediatorLog? log = null)
    {
        string baseDir = baseDirectory ?? AppContext.BaseDirectory;
        var results = new List<LoadResult>();
        foreach (var dll in Candidates(baseDir))
        {
            var result = Load(dll);
            results.Add(result);
            if (result.Error is { } error) log?.Warn($"Connector {Path.GetFileName(result.Folder)} was not loaded: {error}");
        }
        return results;
    }

    /// <summary>The connector assemblies to load: one per folder, the installed ones first.</summary>
    internal static IEnumerable<string> Candidates(string baseDir)
    {
        string installed = Path.Combine(baseDir, FolderName);
        if (Directory.Exists(installed))
            return Directory.EnumerateDirectories(installed).Order(StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, AssemblyPrefix + Path.GetFileName(d) + ".dll")).Where(File.Exists).ToList();
        // Development: <repo>/src/Connectors/<Name>/bin/<Configuration>/<tfm>/Mediator.Connector.<Name>.dll, newest build.
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            var root = new DirectoryInfo(Path.Combine(dir.FullName, "src", "Connectors"));
            if (!root.Exists) continue;
            return root.EnumerateDirectories().Order(Comparer<DirectoryInfo>.Create((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(d => new DirectoryInfo(Path.Combine(d.FullName, "bin")) is { Exists: true } bin
                    ? bin.EnumerateFiles(AssemblyPrefix + d.Name + ".dll", SearchOption.AllDirectories)
                        .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                    : null)
                .OfType<string>().ToList();
        }
        return [];
    }

    /// <summary>Loads one connector assembly and creates every public <see cref="IMediatorConnector"/> in it.</summary>
    public static LoadResult Load(string assemblyPath)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        try
        {
            var context = new ConnectorLoadContext(assemblyPath);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            var connectors = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMediatorConnector).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
                .Select(t => (IMediatorConnector)Activator.CreateInstance(t)!)
                .ToList();
            return connectors.Count == 0
                ? new LoadResult(folder, [], $"{Path.GetFileName(assemblyPath)} holds no connector")
                : new LoadResult(folder, connectors, null);
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or ReflectionTypeLoadException or TypeLoadException
            or MissingMethodException or TargetInvocationException or FileLoadException or UnauthorizedAccessException)
        {
            return new LoadResult(folder, [], ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// The connector's own dependencies come from its folder; anything the app already has (Mediator.Core, Mediator.Protocol,
    /// the framework) comes from the app, so a connector and the app share one copy of every contract type.
    /// </summary>
    private sealed class ConnectorLoadContext(string mainAssembly) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssembly), isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            if (Default.Assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(name, a.GetName()))) return null;
            try
            {
                if (Default.LoadFromAssemblyName(name) is { }) return null; // the app has it: share it
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException) { }
            return _resolver.ResolveAssemblyToPath(name) is { } path ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
            _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
