using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mediator.Core.Bus;

public enum GateDecision { Approved, Denied, TimedOut, Cancelled }

/// <summary>What the approval UI shows: tool, risk, a one-line summary, raw arguments and a structured preview.</summary>
public sealed record ApprovalRequest(string JobId, string CallId, string Tool, ToolRisk Risk, string Summary, JsonElement Arguments, JsonNode? Preview);

/// <summary>
/// The user's answer. <see cref="ApprovedRows"/> narrows an approval to some rows of a selectable preview
/// (for example 3 of 4 mates); null means "everything shown". <see cref="KillJob"/> asks the host to end the
/// whole job; the call itself is treated as denied.
/// </summary>
public sealed record ApprovalResponse(bool Approved, IReadOnlySet<string>? ApprovedRows = null, bool KillJob = false)
{
    public static readonly ApprovalResponse Deny = new(false);
    public static readonly ApprovalResponse ApproveAll = new(true);
}

/// <summary>Implemented by the overlay or console. Must honour cancellation (close the dialog) when the token fires.</summary>
public interface IApprovalPrompt
{
    Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>
/// Approves without asking, up to a risk ceiling (default Write: Destructive calls are still denied). For
/// tests and for the CLI's explicit `--yes`; every decision is still audited by the bus as "approved".
/// </summary>
public sealed class AutoApprovalPrompt(ToolRisk maxRisk = ToolRisk.Write) : IApprovalPrompt
{
    private int _asked;
    public int Asked => Volatile.Read(ref _asked);

    public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _asked);
        return Task.FromResult(request.Risk <= maxRisk ? ApprovalResponse.ApproveAll : ApprovalResponse.Deny);
    }
}

/// <summary>Lets a host create the gate before the UI exists (the overlay ViewModel needs the runtime, and vice versa).</summary>
public sealed class SwappableApprovalPrompt : IApprovalPrompt
{
    private volatile IApprovalPrompt? _target;
    public SwappableApprovalPrompt(IApprovalPrompt? initial = null) => _target = initial;
    public IApprovalPrompt? Target { get => _target; set => _target = value; }
    public Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct) =>
        _target?.AskAsync(request, ct) ?? Task.FromResult(ApprovalResponse.Deny); // no UI attached: deny, never auto-approve
}

/// <param name="Preview">The preview the user approved (null for auto-approval). Providers that bind an approval to exact content compare against it.</param>
public sealed record GateOutcome(GateDecision Decision, IReadOnlySet<string>? ApprovedRows = null, bool KillRequested = false, JsonNode? Preview = null);

/// <summary>
/// The one place approval policy is decided. Enforcement happens here in the Core, never in the bridge
/// and never in the prompt: the bridge can be run by the agent itself, and prompts can be ignored.
/// </summary>
public sealed class PermissionGate
{
    private readonly IApprovalPrompt _prompt;

    public PermissionGate(IApprovalPrompt prompt) => _prompt = prompt;

    public static ApprovalMode EffectiveMode(JobGrant job, ToolDescriptor tool)
    {
        var fallback = tool.Risk == ToolRisk.ReadOnly ? ApprovalMode.Auto : ApprovalMode.Ask;
        var mode = job.ToolModes.TryGetValue(tool.Name, out var m) ? m : fallback;
        // A grant may tighten Destructive to Deny but never loosen it to Auto.
        if (tool.Risk == ToolRisk.Destructive && mode == ApprovalMode.Auto) mode = ApprovalMode.Ask;
        return mode;
    }

    /// <summary>Raised when the user chose "kill job" in an approval; the host ends the job.</summary>
    public event Action<string>? KillRequested;

    public async Task<GateOutcome> CheckAsync(ToolCallContext call, Func<CancellationToken, Task<JsonNode?>> buildPreview, CancellationToken ct)
    {
        switch (EffectiveMode(call.Job, call.Tool))
        {
            case ApprovalMode.Deny: return new GateOutcome(GateDecision.Denied);
            case ApprovalMode.Auto: return new GateOutcome(GateDecision.Approved);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(call.Job.ApprovalTimeout);
        try
        {
            var preview = await buildPreview(timeout.Token).ConfigureAwait(false);
            var request = new ApprovalRequest(call.Job.JobId, call.CallId, call.Tool.Name, call.Tool.Risk,
                Summarize(call), call.Arguments, preview);
            var response = await _prompt.AskAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.KillJob)
            {
                KillRequested?.Invoke(call.Job.JobId);
                return new GateOutcome(GateDecision.Denied, null, KillRequested: true);
            }
            return response.Approved
                ? new GateOutcome(GateDecision.Approved, response.ApprovedRows, Preview: preview)
                : new GateOutcome(GateDecision.Denied);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GateOutcome(GateDecision.TimedOut);
        }
        catch (OperationCanceledException)
        {
            return new GateOutcome(GateDecision.Cancelled);
        }
    }

    private static string Summarize(ToolCallContext call)
    {
        string args = call.Arguments.ValueKind == JsonValueKind.Object ? call.Arguments.GetRawText() : "{}";
        if (args.Length > 240) args = args[..240] + "…";
        return $"{call.Tool.Name} ({call.Tool.Risk}) for job {call.Job.JobId}: {args}";
    }
}
