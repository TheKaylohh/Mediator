using System.Diagnostics;
using Cockpit.Core.Browser;
using Cockpit.Core.Jobs;
using Cockpit.Core.Runtime;
using Cockpit.UI;

namespace Cockpit.Tests;

/// <summary>V11: "open X with claude in chrome" runs Claude Code with --chrome after Cockpit made sure Chrome is open.</summary>
static class BrowserTests
{
    public static void RoutingAndLaunch()
    {
        const string khan = "Open khan academy with claude in chrome. Pick a topic and find an mcq, answer each one and leave the browser open so i can submit";
        Check.True(JobSpecFactory.InferToolsAndCategory(khan, null) is ({ Count: 0 }, TaskCategory.Browser), "asking for chrome routes to Browser (no Cockpit tools)");
        Check.True(JobSpecFactory.InferToolsAndCategory("open the matrix chapter in my browser", null).Category == TaskCategory.Browser, "the browser wins over app keywords");
        Check.True(JobSpecFactory.InferToolsAndCategory("what is a determinant", null).Category != TaskCategory.Browser
            && JobSpecFactory.InferToolsAndCategory("chromatic aberration explained", null).Category != TaskCategory.Browser, "other prompts do not (whole words only)");
        Check.True(ProviderRouter.ParsePrefix("/chrome find the weather") is { Category: TaskCategory.Browser }, "/chrome prefix");

        var spec = JobSpecFactory.Assistant(null, khan, LaunchMode.Headless, [], TaskCategory.Browser);
        Check.True(JobSpecFactory.UsesChrome(spec) && spec.Roots.Count == 0 && spec.MaxTurns == JobSpecFactory.CadMaxTurns
            && spec.Prompt.StartsWith(ClaudeInChrome.Guidance, StringComparison.Ordinal) && spec.Prompt.Contains(khan, StringComparison.Ordinal),
            "a Browser job: no folders, the browser rules, the request, a long turn budget");

        string dir = Path.Combine(Path.GetTempPath(), "cockpit-tests", "chrome-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var ws = JobWorkspaceFactory.Create(dir, new Cockpit.Core.Bus.JobRegistry().Issue("job-c", [], [dir], TimeSpan.FromMinutes(5)),
            "bridge.exe", Cockpit.Protocol.BusEndpoint.Default, TimeSpan.FromMinutes(1));
        var args = new CliLaunchBuilder("claude", TimeSpan.FromMinutes(5)).ClaudeHeadless(ws, Path.GetTempPath(), 60, noLocalFiles: true, chrome: true).ArgumentList.ToList();
        string allowed = args[args.IndexOf("--allowedTools") + 1];
        Check.True(args.Contains("--chrome") && allowed.Contains("mcp__claude-in-chrome__navigate", StringComparison.Ordinal)
            && !allowed.Contains("javascript_tool", StringComparison.Ordinal) && !allowed.Contains("file_upload", StringComparison.Ordinal)
            && args.Contains("--disallowedTools"), "--chrome, the browser tools allowed (not scripts or uploads), local files still off: " + string.Join(" ", args));
        var plain = new CliLaunchBuilder("claude", TimeSpan.FromMinutes(5)).ClaudeHeadless(ws, Path.GetTempPath(), 20).ArgumentList;
        Check.True(!plain.Contains("--chrome"), "other jobs never get --chrome");
        Check.Equal("chrome · navigate", ClaudeInChrome.ShortName("mcp__claude-in-chrome__navigate"), "step names in the chat");
    }

    public static async Task StartsChromeOnceAndLight()
    {
        bool window = false;
        int starts = 0;
        ProcessStartInfo? started = null;
        var chrome = new ChromeSession(startTimeout: TimeSpan.FromSeconds(5), locate: () => @"C:\fake\chrome.exe", hasWindow: () => window,
            start: psi => { Interlocked.Increment(ref starts); started = psi; _ = Task.Delay(300).ContinueWith(_ => window = true); return null; });
        await Task.WhenAll(chrome.EnsureReadyAsync(CancellationToken.None), chrome.EnsureReadyAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        Check.True(starts == 1 && window, "two jobs asking at once start Chrome once");
        Check.True(started!.ArgumentList.Contains("--renderer-process-limit=4") && started.ArgumentList.Contains("about:blank"), "started light: fewer renderers, one blank page");
        await chrome.EnsureReadyAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Check.Equal(1, starts, "an open Chrome is used as it is");

        var missing = new ChromeSession(locate: () => null, hasWindow: () => false, start: _ => null);
        try { await missing.EnsureReadyAsync(CancellationToken.None); Check.True(false, "no Chrome must fail"); }
        catch (InvalidOperationException ex) { Check.True(ex.Message.Contains("COCKPIT_CHROME", StringComparison.Ordinal), "a missing Chrome says how to fix it"); }
        Check.True(ChromeSession.Locate(n => n == "COCKPIT_CHROME" ? @"C:\nope\chrome.exe" : null) is null || OperatingSystem.IsWindows(), "a wrong override is ignored");
    }
}
