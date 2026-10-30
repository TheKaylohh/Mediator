using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mediator.Core.Runtime;

/// <summary>Which Mediator this is: the version name, and a build id that changes with every compile.</summary>
public static class MediatorVersion
{
    public const string Name = "V17";
    /// <summary>The Core assembly's module id: different for every build, so an old and a new build never look alike.</summary>
    public static string Build { get; } = typeof(MediatorVersion).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];
    public static string Display => $"{Name} (build {Build})";
}

/// <summary>
/// V16.1: the overlay running in the tray, recorded in the data folder when it starts. A newly started build compares itself
/// with it: the same build just says "already running"; a different one (an older version still in the tray, which then
/// answered every chat with its old tools) offers to replace it. V15 and earlier wrote no record: no record while another
/// overlay holds the single-instance lock means an older version.
/// </summary>
public sealed record RunningInstance(int ProcessId, string? ExePath, string Version, string Build, DateTimeOffset StartedAt)
{
    public const string FileName = "instance.json";

    public bool IsThisBuild => Build == MediatorVersion.Build;

    public static RunningInstance Current() =>
        new(Environment.ProcessId, Environment.ProcessPath, MediatorVersion.Name, MediatorVersion.Build, DateTimeOffset.Now);

    public static void Write(string dataDirectory, RunningInstance? instance = null)
    {
        var i = instance ?? Current();
        var json = new JsonObject
        {
            ["pid"] = i.ProcessId, ["exe"] = i.ExePath, ["version"] = i.Version, ["build"] = i.Build,
            ["startedAt"] = i.StartedAt.ToString("O", CultureInfo.InvariantCulture),
        };
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(Path.Combine(dataDirectory, FileName), json.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The recorded instance, or null when there is none (or it cannot be read).</summary>
    public static RunningInstance? Read(string dataDirectory)
    {
        try
        {
            string path = Path.Combine(dataDirectory, FileName);
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            return new RunningInstance((int?)o["pid"] ?? 0, (string?)o["exe"], (string?)o["version"] ?? "?", (string?)o["build"] ?? "",
                DateTimeOffset.TryParse((string?)o["startedAt"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : DateTimeOffset.MinValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return null; }
    }

    /// <summary>How the running overlay is described to the user ("Mediator V16 (build …) from C:\…", or an older version).</summary>
    public static string Describe(RunningInstance? running) => running is null
        ? "an older Mediator (V15 or earlier)"
        : $"Mediator {running.Version} (build {running.Build}){(running.ExePath is { Length: > 0 } exe ? " from " + exe : "")}";

    /// <summary>
    /// The other overlay processes to close so this build can start: the recorded one if it is still alive, else (an older
    /// version recorded nothing) every other process with this executable's name. Never this process.
    /// </summary>
    public static IReadOnlyList<Process> OthersToReplace(RunningInstance? running, string processName)
    {
        var found = new List<Process>();
        if (running is { ProcessId: > 0 } r && r.ProcessId != Environment.ProcessId)
        {
            try
            {
                var p = Process.GetProcessById(r.ProcessId);
                if (string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) found.Add(p); else p.Dispose();
            }
            catch (ArgumentException) { } // already gone
        }
        if (found.Count == 0)
            found.AddRange(Process.GetProcessesByName(processName).Where(p => p.Id != Environment.ProcessId));
        return found;
    }
}
