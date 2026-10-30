using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Diagnostics;
using Mediator.Core.Files;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;
using Mediator.Core.Tools;

namespace Mediator.Core.Connectors;

/// <summary>
/// V18: a connector is one self-contained piece of Mediator (SolidWorks, FreeCAD, Office, Blender...), built as its own
/// assembly under src/Connectors/&lt;Name&gt; and shipped in &lt;app&gt;\connectors\&lt;Name&gt;. The core knows none of them: it asks
/// each connector for its tools, how chats reach it, the actions it offers, the desktop programs it drives and its checks.
/// Removing a connector's folder removes the feature; a future connector center adds folders.
/// </summary>
public interface IMediatorConnector
{
    /// <summary>Stable id ("solidworks"). Lower case letters, digits, '-'.</summary>
    string Id { get; }
    string DisplayName { get; }
    /// <summary>"CAD", "Productivity", "Creative", "Learning", "Development", "Other".</summary>
    string Category { get; }
    /// <summary>What it lets a chat do, in a few words.</summary>
    string Summary { get; }

    /// <summary>
    /// Called once per runtime (the app has one; tests create many): the connector creates its tool providers and anything
    /// they share. Hold per-runtime state in the returned object, not in the connector.
    /// </summary>
    ConnectorInstance Start(ConnectorContext context);

    /// <summary>The tool names a chat about this connector gets.</summary>
    IReadOnlyList<string> ChatTools { get; }

    /// <summary>
    /// Whether <paramref name="tool"/> is one of this connector's own tools. A saved chat holding one gets today's whole
    /// <see cref="ChatTools"/> (a chat from an older version never saw tools added since).
    /// </summary>
    bool OwnsTool(string tool) => ChatTools.Contains(tool, StringComparer.Ordinal);

    /// <summary>How free text reaches this connector (checked in <see cref="ConnectorRoute.Order"/> order; see JobSpecFactory.InferToolsAndCategory).</summary>
    IReadOnlyList<ConnectorRoute> Routes => [];

    /// <summary>Action tiles and CLI recipes ("Assemble SolidWorks parts").</summary>
    IReadOnlyList<ConnectorRecipe> Recipes => [];

    /// <summary>The desktop programs this connector drives (Connectors tab, Software Library).</summary>
    IReadOnlyList<ConnectorProgram> Programs => [];

    /// <summary>Whether a program of <see cref="Programs"/> is installed / running, checked when the Connectors tab opens.</summary>
    AppStatus ProbeProgram(string programId, ProgramProbe probe) => new(programId, programId, false, "not checked");

    /// <summary>Whether this connector reads attached files like <paramref name="path"/> (Office files as text, CAD files as a description).</summary>
    bool ReadsAttachment(string path) => false;

    /// <summary>The text the model gets for an attached file <see cref="ReadsAttachment"/> accepted. Throws <see cref="ConnectorFileException"/>.</summary>
    string DescribeAttachment(string fullPath, int maxChars) => throw new NotSupportedException();

    /// <summary>
    /// `mediator-cli doctor` lines. <paramref name="instance"/> is this connector started in the doctor's runtime (null when it
    /// did not start); <paramref name="deep"/> asks for checks that start programs (`--check-swx`).
    /// </summary>
    Task<IReadOnlyList<ConnectorCheck>> DoctorAsync(ConnectorInstance? instance, bool deep, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ConnectorCheck>>([]);

    /// <summary>Commands this connector adds to mediator-cli (the Tutor's `matrix`).</summary>
    IReadOnlyList<ConnectorCommand> Commands => [];
}

/// <summary>What a connector gets from the runtime when it starts.</summary>
/// <param name="Simulate">Tests and `--simulate`: start no desktop programs, use stand-ins.</param>
/// <param name="ProcessGroup">Child processes the connector keeps running belong here (killed with Mediator).</param>
public sealed record ConnectorContext(MediatorPaths Paths, IMediatorLog Log, JobRegistry Jobs, IProcessRunner ProcessRunner,
    IPythonRunner Python, ProtectedPathPolicy PathPolicy, bool Simulate, Platform.ChildProcessJob ProcessGroup)
{
    /// <summary>Where a connector keeps patched scripts and its own files: &lt;data&gt;\connectors\&lt;id&gt;.</summary>
    public string DataFolder(string connectorId) => Path.Combine(Paths.DataDirectory, "connectors", connectorId);
}

/// <summary>A connector started in one runtime: its tool providers, and its say on jobs before they start.</summary>
public class ConnectorInstance(IEnumerable<IToolProvider> providers) : IAsyncDisposable
{
    public IReadOnlyList<IToolProvider> Providers { get; } = providers.ToList();

    /// <summary>The connector this was started from (set by the runtime).</summary>
    public IMediatorConnector? Connector { get; internal set; }

    /// <summary>
    /// Before a job starts (after routing, before its folders are checked): e.g. start the program early, share the open
    /// document's folder. Returns the spec, changed or not.
    /// </summary>
    public virtual Task<JobSpec> PrepareJobAsync(JobSpec spec, CancellationToken ct) => Task.FromResult(spec);

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// One way free text reaches a connector. <see cref="Match"/> sees the lower-cased prompt and the program in front;
/// <see cref="RouteInput.HasWord"/> matches at the start of a word ("mate" matches "mates", not "estimate").
/// </summary>
/// <param name="Order">Lower first; the first match decides the category (SolidWorks 10, other CAD 20-29, parts from code 31,
/// CAD files 40, Office 50, Files 60 (built in), Resolve 70, Blender 80, Tutor 90).</param>
public sealed record ConnectorRoute(int Order, TaskCategory Category, IReadOnlyList<string> Tools, Func<RouteInput, bool> Match);

public sealed record RouteInput(string Text, string ActiveApp)
{
    public bool HasWord(string key)
    {
        for (int i = Text.IndexOf(key, StringComparison.Ordinal); i >= 0; i = Text.IndexOf(key, i + 1, StringComparison.Ordinal))
            if (i == 0 || !char.IsLetterOrDigit(Text[i - 1]) || !char.IsLetterOrDigit(key[0])) return true;
        return false;
    }

    public bool AnyWord(IEnumerable<string> keys) => keys.Any(HasWord);

    /// <summary>The program in front (process name) contains <paramref name="process"/>.</summary>
    public bool AppIs(string process) => ActiveApp.Contains(process, StringComparison.Ordinal);
}

/// <summary>What an action tile or a CLI recipe is run with.</summary>
/// <param name="Prompt">What the user typed (may be empty: the recipe uses its own default).</param>
/// <param name="Output">A file the CLI named with --output, when the recipe writes one.</param>
public sealed record RecipeRequest(string? Folder, string Prompt, LaunchMode Launch, string? Output = null);

/// <summary>An action tile and CLI recipe ("Assemble SolidWorks parts"): builds the job for a request.</summary>
/// <param name="Order">Tile position: SolidWorks 10-20, Organize 30 (built in), Resolve 40, Blender 50, Tutor 60, Claude Code 100 (built in).</param>
/// <param name="Keywords">Words that suggest this tile while typing.</param>
public sealed record ConnectorRecipe(string Id, int Order, string Title, string Description, string Glyph, TaskCategory Category, bool NeedsFolder,
    IReadOnlyList<string> Keywords, IReadOnlyList<string> Tools, Func<RecipeRequest, JobSpec> Build);

/// <summary>One `mediator-cli doctor` line.</summary>
/// <param name="Status">"ok", "warn" or "fail".</param>
public sealed record ConnectorCheck(string Name, string Status, string Detail);

/// <summary>
/// A mediator-cli command a connector adds. <see cref="Run"/> gets the positional arguments and the --option lookup, writes
/// to the console and returns the exit code; it throws <see cref="ArgumentException"/> for a usage error.
/// </summary>
public sealed record ConnectorCommand(string Name, string Usage, Func<IReadOnlyList<string>, Func<string, string?>, int> Run);

/// <summary>A desktop program a connector drives: how the Software Library and the Connectors tab show it.</summary>
/// <param name="Process">Process name that routes a chat to it when it is in front ("sldworks"), or null.</param>
/// <param name="PromptPrefix">What a new chat about it starts with ("/office "), or null.</param>
public sealed record ConnectorProgram(string Id, string Name, string Glyph, string Summary, string Category, string Capabilities,
    string? Process = null, string? PromptPrefix = null);

/// <summary>
/// The connectors this process loaded. Set once at start-up by the host (<see cref="ConnectorLoader"/>); job routing,
/// action tiles, attachments and the Software Library read it. Tests set it to the connectors they need.
/// </summary>
public static class ConnectorCatalog
{
    private static IReadOnlyList<IMediatorConnector> _all = [];

    public static IReadOnlyList<IMediatorConnector> All => Volatile.Read(ref _all);

    public static void Set(IEnumerable<IMediatorConnector> connectors) =>
        Volatile.Write(ref _all, connectors.GroupBy(c => c.Id, StringComparer.Ordinal).Select(g => g.First()).ToList());

    public static IMediatorConnector? Find(string id) => All.FirstOrDefault(c => c.Id == id);

    public static T? Get<T>() where T : class, IMediatorConnector => All.OfType<T>().FirstOrDefault();

    public static IEnumerable<ConnectorRecipe> Recipes => All.SelectMany(c => c.Recipes).OrderBy(r => r.Order);

    public static ConnectorRecipe? Recipe(string id) => Recipes.FirstOrDefault(r => r.Id == id);

    public static IEnumerable<ConnectorProgram> Programs => All.SelectMany(c => c.Programs);

    public static ConnectorProgram? Program(string id) => Programs.FirstOrDefault(p => p.Id == id);

    /// <summary>The installed/running state of a program some connector drives, or "not checked" when none does.</summary>
    public static AppStatus ProbeProgram(string programId, ProgramProbe probe) =>
        All.FirstOrDefault(c => c.Programs.Any(p => p.Id == programId)) is { } owner
            ? owner.ProbeProgram(programId, probe)
            : new AppStatus(programId, programId, false, "no connector for it is installed");

    public static ConnectorCommand? Command(string name) => All.SelectMany(c => c.Commands).FirstOrDefault(c => c.Name == name);

    /// <summary>The routes of every connector, lowest order first.</summary>
    public static IEnumerable<ConnectorRoute> Routes => All.SelectMany(c => c.Routes).OrderBy(r => r.Order);

    /// <summary>The connector that reads attachments like <paramref name="path"/>, or null.</summary>
    public static IMediatorConnector? AttachmentReader(string path) => All.FirstOrDefault(c => c.ReadsAttachment(path));

    /// <summary>A short JSON listing (id, name, category, tools) for diagnostics.</summary>
    public static JsonArray Describe() => new(All.Select(c => (JsonNode)new JsonObject
    {
        ["id"] = c.Id, ["name"] = c.DisplayName, ["category"] = c.Category, ["tools"] = c.ChatTools.Count,
    }).ToArray());
}

/// <summary>A file a connector was asked to read is not what it expected (damaged, unsupported, too large).</summary>
public class ConnectorFileException(string message) : Exception(message);
