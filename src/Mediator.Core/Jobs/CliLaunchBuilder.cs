using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Mediator.Protocol;

namespace Mediator.Core.Jobs;

/// <summary>
/// Builds the process launches for each CLI. Both modes route tool calls through the same bridge, so the
/// interactive terminal fallback gets the same gate, leases and journal as headless runs.
/// </summary>
public sealed class CliLaunchBuilder
{
    /// <summary>
    /// Variables that silently switch a CLI from your subscription login to pay-per-token or a different
    /// account/tier. They are removed from every child environment unless a feature is explicitly opted in.
    /// </summary>
    public static readonly string[] BillingVariables =
    [
        "ANTHROPIC_API_KEY", "CLAUDE_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX",
        "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI", "GOOGLE_CLOUD_PROJECT",
        "OPENAI_API_KEY",
    ];

    private readonly string _claudeExecutable;
    private readonly TimeSpan _toolTimeout;

    /// <param name="claudeExecutable">Full path to claude.exe (native install) or claude.cmd (npm shim).</param>
    /// <param name="toolTimeout">Client-side MCP tool timeout; must be longer than the bridge timeout.</param>
    public CliLaunchBuilder(string claudeExecutable, TimeSpan toolTimeout)
    {
        _claudeExecutable = claudeExecutable;
        _toolTimeout = toolTimeout;
    }

    /// <summary>
    /// The variables to remove for a job. Empty only when the job was explicitly opted into metered billing
    /// (<see cref="Bus.JobGrant.AllowMeteredBilling"/>); then the user's API keys pass through unchanged.
    /// </summary>
    public static IReadOnlyList<string> VariablesToScrub(bool allowMeteredBilling) => allowMeteredBilling ? [] : BillingVariables;

    /// <summary>Which billing variables are set in an environment (for the overlay's red banner).</summary>
    public static IReadOnlyList<string> FindBillingVariables(IDictionary environment) =>
        BillingVariables.Where(v => environment.Contains(v) && !string.IsNullOrEmpty(environment[v] as string)).ToList();

    /// <summary>
    /// Finds an executable on PATH honouring PATHEXT. Process.Start("claude") fails when only the npm shim
    /// claude.cmd exists, because CreateProcess does not run .cmd files by bare name.
    /// </summary>
    public static string? ResolveOnPath(string name)
    {
        string[] exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts.OrderBy(e => e.Equals(".EXE", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                string candidate = Path.Combine(dir.Trim('"'), name + ext.ToLowerInvariant());
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Headless Claude Code with a JSON event stream. Write the prompt to stdin, then close it.
    /// <paramref name="resumeSessionId"/> (V9) continues that session with `--resume`; it must be a safe token
    /// (<see cref="SessionIds.IsSafe"/>), since it is the one value here that came from a previous run's output.
    /// </summary>
    /// <param name="webSearch">V10 Search jobs: Claude's WebSearch and WebFetch are allowed without a prompt.</param>
    /// <param name="noLocalFiles">V10 Chat and Search jobs: Claude's own file and shell tools are disallowed, so the run
    /// cannot read anything on the machine (it would otherwise read its working folder without asking).</param>
    /// <summary>Claude Code's built-in tools that touch the local machine (files, shell, notebooks).</summary>
    public static readonly string[] ClaudeLocalTools = ["Bash", "Read", "Write", "Edit", "MultiEdit", "Glob", "Grep", "LS", "NotebookEdit", "NotebookRead"];

    public ProcessStartInfo ClaudeHeadless(JobWorkspace ws, string workingDirectory, int maxTurns, string? model = null, bool allowMeteredBilling = false,
        string? resumeSessionId = null, bool webSearch = false, bool noLocalFiles = false, string? effort = null, bool chrome = false)
    {
        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose",
            // Live progress while a long answer or a big tool call (a CAD recipe) is written; otherwise minutes look frozen.
            "--include-partial-messages",
            "--mcp-config", ws.ClaudeMcpConfigPath, "--strict-mcp-config",
            // Our gate is authoritative, so Claude's own prompt for these tools would only duplicate it.
            "--allowedTools", $"mcp__{JobWorkspaceFactory.ServerName}" + (webSearch ? ",WebSearch,WebFetch" : "") + (chrome ? "," + Browser.ClaudeInChrome.AllowedToolsArgument : ""),
            "--max-turns", maxTurns.ToString(CultureInfo.InvariantCulture),
        };
        if (noLocalFiles) { args.Add("--disallowedTools"); args.Add(string.Join(",", ClaudeLocalTools)); }
        // V11: Claude in Chrome (the CLI connects to the extension in the user's Chrome).
        if (chrome) args.Add("--chrome");
        if (model is not null) { args.Add("--model"); args.Add(model); }
        if (ModelCatalog.IsEffort(effort)) { args.Add("--effort"); args.Add(effort!); }
        if (resumeSessionId is not null) { args.Add("--resume"); args.Add(RequireSafeSession(resumeSessionId)); }

        var psi = WrapForShim(_claudeExecutable, args);
        psi.WorkingDirectory = workingDirectory;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardInputEncoding = Utf8.NoBom;
        psi.StandardOutputEncoding = Utf8.NoBom;
        psi.StandardErrorEncoding = Utf8.NoBom;
        foreach (var v in VariablesToScrub(allowMeteredBilling)) psi.Environment.Remove(v);
        psi.Environment["MCP_TOOL_TIMEOUT"] = ((long)_toolTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        return psi;
    }

    /// <summary>
    /// Interactive Claude Code in a new Windows Terminal tab (the billing-safe fallback). The environment is
    /// scrubbed inside a generated PowerShell script because a tab opened by wt.exe does not inherit the
    /// Mediator's environment. The script is UTF-8 WITH a BOM: Windows PowerShell 5.1 reads BOM-less scripts
    /// as ANSI and would mangle a non-ASCII prompt.
    /// </summary>
    /// <param name="hasBrief">V6: the user's task is never put on a command line (visible to other processes). When the job has
    /// one, the first message is a fixed instruction to fetch it from mediator_job_brief over the authenticated bus.</param>
    public ProcessStartInfo ClaudeInteractive(JobWorkspace ws, string workingDirectory, InteractiveSession session, bool hasBrief, bool allowMeteredBilling = false, string? resumeSessionId = null, bool chrome = false)
    {
        string command = $"& {Ps(_claudeExecutable)} --mcp-config {Ps(ws.ClaudeMcpConfigPath)} --strict-mcp-config --allowedTools {Ps("mcp__" + JobWorkspaceFactory.ServerName)}"
            + (resumeSessionId is not null ? " --resume " + Ps(RequireSafeSession(resumeSessionId)) : "")
            + (chrome ? " --chrome" : "")
            + (hasBrief ? " " + Ps(JobBriefToolProvider.Instruction) : "");
        string script = InteractiveSession.WriteScript(Path.Combine(ws.Root, "launch-claude.ps1"), ws.Root, VariablesToScrub(allowMeteredBilling), allowMeteredBilling,
            new Dictionary<string, string> { ["MCP_TOOL_TIMEOUT"] = ((long)_toolTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) },
            workingDirectory, command, session.Nonce);
        return TerminalStartInfo(script, $"Mediator {ws.JobId}", workingDirectory);
    }

    /// <summary>
    /// A new Windows Terminal tab running the script (or a plain console window without wt.exe). The returned process
    /// is only a launcher: wt.exe usually exits within milliseconds, so callers must watch <see cref="InteractiveSession"/>.
    /// </summary>
    public static ProcessStartInfo TerminalStartInfo(string script, string title, string workingDirectory)
    {
        string[] ps = ["powershell.exe", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", script];
        if (ResolveOnPath("wt") is string wt)
        {
            var psi = new ProcessStartInfo(wt) { UseShellExecute = false };
            // wt.exe treats ';' as a command separator; escape it inside arguments.
            foreach (var a in new[] { "-w", "new", "new-tab", "--title", title, "-d", workingDirectory }.Concat(ps))
                psi.ArgumentList.Add(a.Replace(";", "\\;", StringComparison.Ordinal));
            return psi;
        }
        var console = new ProcessStartInfo(ps[0]) { UseShellExecute = true }; // new console window
        foreach (var a in ps.Skip(1)) console.ArgumentList.Add(a);
        return console;
    }

    /// <summary>npm shims (.cmd/.bat) run through cmd; prompts never go on this command line.</summary>
    public static ProcessStartInfo WrapForShim(string executable, IEnumerable<string> args)
    {
        ProcessStartInfo psi;
        if (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // npm shims are batch files: run them through cmd. Prompts never go on this command line (stdin only),
            // so cmd's metacharacters (& | ^ %) cannot be injected through model or user text.
            psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe");
            foreach (var a in new[] { "/d", "/s", "/c", executable }) psi.ArgumentList.Add(a);
        }
        else
        {
            psi = new ProcessStartInfo(executable);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string Ps(string value) => InteractiveSession.Ps(value);

    private static string RequireSafeSession(string id) =>
        SessionIds.IsSafe(id) ? id : throw new ArgumentException($"'{id}' is not a valid session id (letters, digits, '-' and '_' only).", nameof(id));
}
