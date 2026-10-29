using System.Diagnostics;
using Mediator.Core.Diagnostics;
using Mediator.Core.Runtime;

namespace Mediator.Core.Browser;

/// <summary>
/// What a Browser job may do in the user's Chrome through Claude Code's built-in Claude in Chrome integration
/// (`claude --chrome`, MCP server "claude-in-chrome", which needs the Claude in Chrome extension in that Chrome).
/// The CLI connects to the browser itself; Mediator only makes sure Chrome is running and limits the tools.
/// </summary>
public static class ClaudeInChrome
{
    public const string ServerName = "claude-in-chrome";
    public const string ToolPrefix = "mcp__" + ServerName + "__";

    /// <summary>
    /// Allowed without a prompt (a headless run cannot answer one): reading, navigating, clicking and typing in tabs.
    /// Left out: javascript_tool (runs code in the user's signed-in pages), file_upload/upload_image (send local files),
    /// gif_creator (records the screen), network/console capture and extension shortcuts.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedTools =
    [
        "tabs_context_mcp", "tabs_create_mcp", "tabs_close_mcp", "navigate", "read_page", "get_page_text", "find",
        "form_input", "computer", "resize_window", "list_connected_browsers", "select_browser", "switch_browser",
    ];

    public static string AllowedToolsArgument => string.Join(",", AllowedTools.Select(t => ToolPrefix + t));

    /// <summary>"mcp__claude-in-chrome__navigate" → "chrome · navigate" for the chat's step list; other names unchanged.</summary>
    public static string ShortName(string tool) => tool.StartsWith(ToolPrefix, StringComparison.Ordinal) ? "chrome · " + tool[ToolPrefix.Length..] : tool;

    /// <summary>The rules every Browser job gets: safety in the user's signed-in browser, and a light footprint.</summary>
    public const string Guidance =
        "You drive the user's own Chrome (signed in as them) with the claude-in-chrome tools. " +
        "This run cannot show questions (no AskUserQuestion): if several browsers are connected, call list_connected_browsers and " +
        "select_browser the one the user named, else the isLocal one with the latest connectedAt, and say in your answer which you used; " +
        "if none is local, stop and ask the user to pick one in the chat. " +
        "Then call tabs_context_mcp, open ONE new tab with tabs_create_mcp and do all the work in it; close any other tab you open. " +
        "Read pages with get_page_text, find or read_page; take a screenshot (computer) only when you must see the layout, since " +
        "screenshots are slow and heavy. Never submit, buy, send, post, delete or change settings unless the user asked for exactly that; " +
        "when they want to review (\"leave it open\", \"so I can submit\"), stop before the final submit and leave the tab open. " +
        "If a site needs a login, a CAPTCHA or a permission you lack, stop and tell the user. Never trigger alert or confirm dialogs. " +
        "Do not read, list or change local files. Finish with what you did and which tab you left open.";
}

/// <summary>
/// The user's Chrome, started on demand for Browser jobs like SolidWorks for CAD jobs: never a second copy, a start in
/// progress is joined, and a Chrome that is already open is simply used. Chrome is heavy, so when Mediator starts it, it
/// starts with one blank window and fewer renderer processes (<see cref="StartArguments"/>); a Chrome the user opened
/// is never restarted or changed. It is left open after the job so the user can see and finish the work.
/// </summary>
public sealed class ChromeSession : IDesktopAppSession
{
    public static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Time for the Claude in Chrome extension to connect after a fresh start, before the CLI looks for it.</summary>
    public static readonly TimeSpan ExtensionGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Flags for a Chrome that Mediator starts: no first-run or default-browser pages, at most 4 renderer processes and one
    /// per site, and a blank page instead of restoring every old tab.
    /// </summary>
    public static readonly IReadOnlyList<string> StartArguments =
        ["--no-first-run", "--no-default-browser-check", "--renderer-process-limit=4", "--process-per-site", "about:blank"];

    private readonly Func<string?> _locate;
    private readonly Func<bool> _hasWindow;
    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly TimeSpan _startTimeout;
    private readonly IMediatorLog? _log;
    private readonly object _gate = new();
    private Task? _starting;

    public ChromeSession(IMediatorLog? log = null, TimeSpan? startTimeout = null,
        Func<string?>? locate = null, Func<bool>? hasWindow = null, Func<ProcessStartInfo, Process?>? start = null)
    {
        _log = log;
        _startTimeout = startTimeout ?? DefaultStartTimeout;
        _locate = locate ?? (() => Locate(Environment.GetEnvironmentVariable));
        _hasWindow = hasWindow ?? HasChromeWindow;
        _start = start ?? Process.Start;
    }

    public Task EnsureReadyAsync(CancellationToken ct)
    {
        Task start;
        lock (_gate)
        {
            if (_starting is not { IsCompleted: false }) _starting = Task.Run(StartAsync, CancellationToken.None);
            start = _starting;
        }
        return start.WaitAsync(ct);
    }

    public void WarmUp()
    {
        if (_hasWindow()) return;
        _ = EnsureReadyAsync(CancellationToken.None).ContinueWith(
            t => _log?.Info("Chrome warm-up failed: " + t.Exception?.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private async Task StartAsync()
    {
        if (_hasWindow()) return;
        string exe = _locate() ?? throw new InvalidOperationException(
            "Google Chrome was not found. Install it (with the Claude in Chrome extension) or set MEDIATOR_CHROME to chrome.exe.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in StartArguments) psi.ArgumentList.Add(a);
        // A Chrome running in the background (no window) takes the request and opens a window; this process then exits.
        using (_start(psi)) { }
        _log?.Info("Started Chrome for a browser job.");
        var deadline = DateTime.UtcNow + _startTimeout;
        while (!_hasWindow())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Chrome did not open a window within {_startTimeout.TotalSeconds:0} s.");
            await Task.Delay(250).ConfigureAwait(false);
        }
        await Task.Delay(ExtensionGrace).ConfigureAwait(false);
    }

    /// <summary>True when a Chrome window is open (background-only Chrome processes do not count).</summary>
    public static bool HasChromeWindow()
    {
        var all = Process.GetProcessesByName("chrome");
        try { return all.Any(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch (InvalidOperationException) { return false; } }); }
        finally { foreach (var p in all) p.Dispose(); }
    }

    /// <summary>chrome.exe: MEDIATOR_CHROME, the App Paths registration, then the standard install folders. Null if none.</summary>
    public static string? Locate(Func<string, string?> env)
    {
        if (env("MEDIATOR_CHROME") is { Length: > 0 } forced && File.Exists(forced.Trim('"'))) return Path.GetFullPath(forced.Trim('"'));
        if (OperatingSystem.IsWindows())
        {
            const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe";
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try { if (hive.OpenSubKey(appPaths)?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"'); }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        foreach (var root in new[] { env("ProgramFiles"), env("ProgramFiles(x86)"), env("LOCALAPPDATA") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string p = Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
