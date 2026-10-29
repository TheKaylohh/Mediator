using System.Net;
using System.Text;
using Mediator.Core.Jobs;
using Mediator.UI;

namespace Mediator.Tests;

/// <summary>V14: the Usage tab. Claude's plan meter read without a model call; Mediator's own runs from the ledger.</summary>
static class UsageTests
{
    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    const string Body = """
        {"five_hour":{"utilization":28.0,"resets_at":"2026-10-29T15:09:59.864190+00:00"},
         "seven_day":{"utilization":81.5,"resets_at":"2026-11-03T18:59:59+00:00"},"seven_day_opus":null,"extra_usage":{"is_enabled":false}}
        """;

    static string ClaudeDir(long expiresAtMs, string token = "tok-secret")
    {
        string dir = RuntimeTests.TempDir();
        File.WriteAllText(Path.Combine(dir, ".credentials.json"),
            "{\"claudeAiOauth\":{\"accessToken\":\"" + token + "\",\"refreshToken\":\"r\",\"expiresAt\":" + expiresAtMs + ",\"subscriptionType\":\"pro\"}}");
        return dir;
    }

    public static async Task ClaudePlanMeter()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-10-29T12:00:00Z"));
        string dir = ClaudeDir(clock.Now.AddHours(2).ToUnixTimeMilliseconds());
        var http = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        var meter = new ClaudePlanUsage(http, n => n == "CLAUDE_CONFIG_DIR" ? dir : null, clock);

        var snap = await meter.GetAsync();
        Check.True(snap.Problem is null && snap.Plan == "pro" && snap.FiveHour?.Percent == 28 && snap.Week?.Percent == 81.5
            && snap.Week.ResetsAt == DateTimeOffset.Parse("2026-11-03T18:59:59Z"), "5-hour and weekly utilization and resets parsed");
        var req = http.Seen.Single();
        Check.True(req.RequestUri!.ToString() == ClaudePlanUsage.Endpoint && req.Headers.Authorization?.Parameter == "tok-secret"
            && req.Headers.GetValues("anthropic-beta").Single() == "oauth-2025-04-20", "one GET to Anthropic with Claude Code's token");

        clock.Now = clock.Now.AddSeconds(30);
        await meter.GetAsync();
        Check.Equal(1, http.Seen.Count, "reopening the tab within a minute sends nothing");
        await meter.GetAsync(force: true);
        clock.Now = clock.Now.AddSeconds(61);
        await meter.GetAsync();
        Check.Equal(3, http.Seen.Count, "Refresh, or a minute later, reads again");

        string expired = ClaudeDir(clock.Now.AddMinutes(-1).ToUnixTimeMilliseconds());
        var http2 = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        var stale = await new ClaudePlanUsage(http2, n => n == "CLAUDE_CONFIG_DIR" ? expired : null, clock).GetAsync();
        Check.True(stale.Problem!.Contains("renews", StringComparison.Ordinal) && http2.Seen.Count == 0, "an expired token is reported, not sent and not refreshed");

        var none = await new ClaudePlanUsage(http2, n => n == "CLAUDE_CONFIG_DIR" ? RuntimeTests.TempDir() : null, clock).GetAsync();
        Check.True(none.Problem!.Contains("Sign In", StringComparison.Ordinal), "not signed in says where to sign in");

        var denied = await new ClaudePlanUsage(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)), n => n == "CLAUDE_CONFIG_DIR" ? dir : null, clock).GetAsync();
        Check.True(denied.Problem is not null && denied.Plan == "pro", "a refused token is a readable problem");
        var offline = await new ClaudePlanUsage(new FakeHandler(_ => throw new HttpRequestException("no network")), n => n == "CLAUDE_CONFIG_DIR" ? dir : null, clock).GetAsync();
        Check.True(offline.Problem!.Contains("offline", StringComparison.Ordinal), "offline is a readable problem");
        var odd = await new ClaudePlanUsage(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }), n => n == "CLAUDE_CONFIG_DIR" ? dir : null, clock).GetAsync();
        Check.True(odd.Problem is not null, "a changed response shape is reported, not shown as 0%");
        Check.True(!(denied.Problem + offline.Problem + stale.Problem).Contains("tok-secret", StringComparison.Ordinal), "the token never appears in messages");
    }

    public static async Task UsageTab(string bridgeDll)
    {
        await using var runtime = await OperationTests.Runtime(bridgeDll);
        var run = new RunResultEvent(null, "success", false, "ok", 3, 1000, 0.12, new UsageTotals(1_200, 800, 50_000, 300), null);
        await runtime.Usage.RecordRunAsync("job-u1", MediatorOverlayViewModel.ClaudeProvider, "sonnet", run);
        await runtime.Usage.RecordRunAsync("job-u2", MediatorOverlayViewModel.ClaudeProvider, "sonnet", run);
        await runtime.Usage.SetCooldownAsync(MediatorOverlayViewModel.GeminiProvider, DateTimeOffset.UtcNow.AddHours(1), "quota");

        string dir = ClaudeDir(DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds());
        var meter = new ClaudePlanUsage(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body) }),
            n => n == "CLAUDE_CONFIG_DIR" ? dir : null);
        var vm = new UsageViewModel(runtime, meter, new InlineDispatcher());
        await vm.RefreshAsync();
        Check.True(vm.Claude.HasMeter && vm.Claude.FiveHourPercent == 28 && vm.Claude.FiveHourText.StartsWith("5-hour window: 28% used · resets", StringComparison.Ordinal)
            && vm.Claude.WeekText.StartsWith("Week: 82% used", StringComparison.Ordinal), "Claude card: both meters: " + vm.Claude.FiveHourText + " / " + vm.Claude.WeekText);
        Check.True(vm.Claude.NeedsAttention, "81.5% of the week is flagged");
        Check.True(vm.Claude.LocalText.Contains("2 runs, 4.6k tokens (+100k cached)", StringComparison.Ordinal), "Mediator's own runs counted: " + vm.Claude.LocalText);
        Check.True(!vm.Gemini.HasMeter && vm.Gemini.Note.StartsWith("Rate-limited until", StringComparison.Ordinal) && vm.Gemini.NeedsAttention
            && vm.Gemini.LocalText.Contains("no runs", StringComparison.Ordinal), "Google AI Pro: no published meter, cooldown shown: " + vm.Gemini.Note);
        Check.True(vm.Status.Contains("no tokens used", StringComparison.Ordinal), "the tab says refreshing costs nothing");

        var noMeter = new UsageViewModel(runtime, null, new InlineDispatcher());
        await noMeter.RefreshAsync();
        Check.True(!noMeter.Claude.HasMeter && noMeter.Claude.Note.Length > 0, "without a meter the Claude card still explains itself");
        Check.Equal("1.5M", UsageViewModel.Compact(1_500_000), "compact numbers");
    }
}
