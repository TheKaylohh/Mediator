using System.Collections.ObjectModel;
using System.Globalization;
using Mediator.Core.Jobs;
using Mediator.Core.Runtime;

namespace Mediator.UI;

/// <summary>One subscription's card on the Usage tab.</summary>
public sealed class SubscriptionUsageViewModel(string provider, string name) : ObservableObject
{
    private bool _hasMeter;
    private double _fiveHourPercent;
    private string _fiveHourText = "";
    private double _weekPercent;
    private string _weekText = "";
    private string _localText = "";
    private string _note = "";
    private bool _needsAttention;

    public string Provider { get; } = provider;
    public string Name { get; } = name;
    /// <summary>True when the provider's own 5-hour / weekly meter was read (Claude); false shows only the note.</summary>
    public bool HasMeter { get => _hasMeter; internal set => SetProperty(ref _hasMeter, value); }
    public double FiveHourPercent { get => _fiveHourPercent; internal set => SetProperty(ref _fiveHourPercent, value); }
    public string FiveHourText { get => _fiveHourText; internal set => SetProperty(ref _fiveHourText, value); }
    public double WeekPercent { get => _weekPercent; internal set => SetProperty(ref _weekPercent, value); }
    public string WeekText { get => _weekText; internal set => SetProperty(ref _weekText, value); }
    /// <summary>What Mediator's own runs reported in the last 5 hours and 7 days.</summary>
    public string LocalText { get => _localText; internal set => SetProperty(ref _localText, value); }
    public string Note { get => _note; internal set => SetProperty(ref _note, value); }
    /// <summary>80 % or more of a window used, or rate-limited now.</summary>
    public bool NeedsAttention { get => _needsAttention; internal set => SetProperty(ref _needsAttention, value); }
}

/// <summary>
/// V14: the Usage tab. Claude: the plan's own 5-hour and weekly meter (<see cref="ClaudePlanUsage"/>), the numbers
/// Claude Code's /usage shows. Google AI Pro has no published meter, so its card shows what Mediator measured. Nothing
/// here runs a model: refreshing costs no tokens and does not count against any limit.
/// </summary>
public sealed class UsageViewModel : ObservableObject
{
    public static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly IMediatorRuntime _runtime;
    private readonly ClaudePlanUsage? _claude;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private bool _isBusy;
    private string _status = "";

    public UsageViewModel(IMediatorRuntime runtime, ClaudePlanUsage? claude, IUiDispatcher ui, TimeProvider? time = null)
    {
        _runtime = runtime;
        _claude = claude;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        Claude = new SubscriptionUsageViewModel(MediatorOverlayViewModel.ClaudeProvider, "Claude Pro (Claude Code)");
        Gemini = new SubscriptionUsageViewModel(MediatorOverlayViewModel.GeminiProvider, "Google AI Pro (Antigravity CLI)");
        Subscriptions.Add(Claude);
        Subscriptions.Add(Gemini);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(force: true), () => !IsBusy);
    }

    public SubscriptionUsageViewModel Claude { get; }
    public SubscriptionUsageViewModel Gemini { get; }
    public ObservableCollection<SubscriptionUsageViewModel> Subscriptions { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RefreshCommand.Refresh(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    /// <param name="force">The refresh button: read the Claude meter again even within its one-minute cache.</param>
    public async Task RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        _ui.Post(() => IsBusy = true);
        try
        {
            var now = _time.GetUtcNow();
            ClaudePlanSnapshot? plan = _claude is null ? null : await _claude.GetAsync(force, ct).ConfigureAwait(false);
            var cards = new List<(SubscriptionUsageViewModel Card, string Local, DateTimeOffset? Cooldown)>();
            foreach (var card in Subscriptions)
            {
                var five = await _runtime.UsageSinceAsync(card.Provider, now - FiveHours).ConfigureAwait(false);
                var week = await _runtime.UsageSinceAsync(card.Provider, now - Week).ConfigureAwait(false);
                var cooldown = await _runtime.GetCooldownAsync(card.Provider).ConfigureAwait(false);
                cards.Add((card, LocalSummary(five, week), cooldown));
            }
            _ui.Post(() =>
            {
                foreach (var (card, local, cooldown) in cards)
                {
                    card.LocalText = local;
                    string limited = cooldown is { } until && until > now ? "Rate-limited until " + Clock(until, now) + ". " : "";
                    if (card == Claude) ApplyClaude(card, plan, limited, now);
                    else
                    {
                        card.HasMeter = false;
                        card.Note = limited + "Google does not publish Google AI Pro usage, so there is no plan meter; below is what Mediator's own runs reported.";
                        card.NeedsAttention = limited.Length > 0;
                    }
                }
                Status = "Updated " + now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " · no tokens used";
            });
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    private static void ApplyClaude(SubscriptionUsageViewModel card, ClaudePlanSnapshot? plan, string limited, DateTimeOffset now)
    {
        if (plan is null || plan.Problem is not null)
        {
            card.HasMeter = false;
            card.Note = limited + (plan?.Problem ?? "The plan meter is not available in this host.");
            card.NeedsAttention = limited.Length > 0;
            return;
        }
        card.HasMeter = true;
        card.FiveHourPercent = plan.FiveHour?.Percent ?? 0;
        card.FiveHourText = WindowText("5-hour window", plan.FiveHour, now);
        card.WeekPercent = plan.Week?.Percent ?? 0;
        card.WeekText = WindowText("Week", plan.Week, now);
        card.Note = limited + (plan.Plan is { Length: > 0 } p ? $"Plan: {char.ToUpperInvariant(p[0])}{p[1..]}. " : "")
            + "Includes claude.ai and every Claude Code session, not only Mediator.";
        card.NeedsAttention = limited.Length > 0 || card.FiveHourPercent >= 80 || card.WeekPercent >= 80;
    }

    internal static string WindowText(string label, PlanWindow? w, DateTimeOffset now) => w is null
        ? label + ": not reported"
        : $"{label}: {w.Percent.ToString("0", CultureInfo.InvariantCulture)}% used" + (w.ResetsAt is { } r ? " · resets " + Clock(r, now) : "");

    /// <summary>"14:05" today, "Tue 14:05" within a week, else "Nov 3 14:05" (local time).</summary>
    internal static string Clock(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        string fmt = local.Date == today ? "HH:mm" : local.Date < today.AddDays(7) ? "ddd HH:mm" : "MMM d HH:mm";
        return local.ToString(fmt, CultureInfo.InvariantCulture);
    }

    internal static string LocalSummary(UsageLedger.ProviderTotals? five, UsageLedger.ProviderTotals? week)
    {
        if (five is null || week is null) return "";
        static string Part(UsageLedger.ProviderTotals t)
        {
            if (t.Runs == 0) return "no runs";
            long tokens = (t.Tokens.InputTokens ?? 0) + (t.Tokens.OutputTokens ?? 0) + (t.Tokens.CacheCreationInputTokens ?? 0);
            string s = $"{t.Runs} run{(t.Runs == 1 ? "" : "s")}, {Compact(tokens)} tokens";
            if (t.Tokens.CacheReadInputTokens is long cached and > 0) s += $" (+{Compact(cached)} cached)";
            if (t.UnknownUsageRuns > 0) s += $", {t.UnknownUsageRuns} unreported";
            return s;
        }
        return $"Mediator's runs · last 5 h: {Part(five)} · last 7 days: {Part(week)}";
    }

    public static string Compact(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
}
