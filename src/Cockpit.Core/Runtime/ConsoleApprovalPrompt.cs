using System.Globalization;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;

namespace Cockpit.Core.Runtime;

/// <summary>
/// Approval in a terminal: shows the preview rows and asks
/// <c>[A]pprove all / [S]elect / [D]eny / [K]ill job</c>. Select (only for selectable previews) takes the row
/// numbers to keep, e.g. "1 2 4" or "1-3,5". Approvals are asked one at a time; a request whose approval
/// timeout fires while waiting is answered "deny". Reader and writer are injectable for tests.
/// </summary>
public sealed class ConsoleApprovalPrompt : IApprovalPrompt
{
    private readonly TextReader _in;
    private readonly TextWriter _out;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly int _maxRowsShown;

    public ConsoleApprovalPrompt(TextReader? input = null, TextWriter? output = null, int maxRowsShown = 40)
    {
        _in = input ?? Console.In;
        _out = output ?? Console.Out;
        _maxRowsShown = maxRowsShown;
    }

    public sealed record PreviewRow(string Key, string Primary, string? Detail, double? Confidence);

    public static (string? Title, string? Summary, bool Selectable, IReadOnlyList<PreviewRow> Rows) ReadPreview(JsonNode? preview)
    {
        var rows = preview?["rows"] is JsonArray arr
            ? arr.OfType<JsonObject>().Select(r => new PreviewRow((string?)r["key"] ?? "", (string?)r["primary"] ?? "", (string?)r["detail"], (double?)r["confidence"])).ToList()
            : [];
        return ((string?)preview?["title"], (string?)preview?["summary"], (bool?)preview?["selectable"] ?? false, rows);
    }

    public async Task<ApprovalResponse> AskAsync(ApprovalRequest request, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (title, summary, selectable, rows) = ReadPreview(request.Preview);
            _out.WriteLine();
            _out.WriteLine(new string('─', 72));
            _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"APPROVAL  job {request.JobId}  ·  {request.Tool}  ·  risk {request.Risk}"));
            _out.WriteLine(title ?? request.Summary);
            if (summary is not null) _out.WriteLine(summary);
            for (int i = 0; i < rows.Count && i < _maxRowsShown; i++)
            {
                var r = rows[i];
                string conf = r.Confidence is double c ? string.Create(CultureInfo.InvariantCulture, $"  [{c:0.00}]") : "";
                _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {i + 1,3}. {r.Primary}{conf}"));
                if (!string.IsNullOrEmpty(r.Detail)) _out.WriteLine($"       {r.Detail}");
            }
            if (rows.Count > _maxRowsShown) _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  … and {rows.Count - _maxRowsShown} more"));

            while (true)
            {
                _out.Write(selectable && rows.Count > 0 ? "[A]pprove all / [S]elect / [D]eny / [K]ill job > " : "[A]pprove / [D]eny / [K]ill job > ");
                _out.Flush();
                string? line = await ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) return ApprovalResponse.Deny; // input closed
                switch (line.Trim().ToUpperInvariant())
                {
                    case "A": return ApprovalResponse.ApproveAll;
                    case "D": return ApprovalResponse.Deny;
                    case "K": return new ApprovalResponse(false, KillJob: true);
                    case "S" when selectable && rows.Count > 0:
                        _out.Write("Rows to keep (e.g. 1 2 4 or 1-3), empty = cancel > ");
                        _out.Flush();
                        string? pick = await ReadLineAsync(ct).ConfigureAwait(false);
                        if (pick is null) return ApprovalResponse.Deny;
                        if (pick.Trim().Length == 0) continue;
                        var keep = ParseSelection(pick, rows.Count);
                        if (keep is null)
                        {
                            _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Use numbers from 1 to {rows.Count}."));
                            continue;
                        }
                        if (keep.Count == 0) return ApprovalResponse.Deny;
                        var keys = keep.Select(i => rows[i - 1].Key).ToHashSet(StringComparer.Ordinal);
                        _out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Approved {keys.Count} of {rows.Count}."));
                        return new ApprovalResponse(true, keys);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _out.WriteLine();
            _out.WriteLine("(approval timed out or the job ended: denied)");
            throw;
        }
        finally
        {
            _one.Release();
        }
    }

    private static readonly char[] SelectionSeparators = [' ', ',', ';'];

    /// <summary>"1 2 4", "1-3,5" → 1-based row numbers; null if any part is out of range or malformed.</summary>
    public static IReadOnlySet<int>? ParseSelection(string text, int count)
    {
        var result = new SortedSet<int>();
        foreach (var part in text.Split(SelectionSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] ends = part.Split('-');
            if (ends.Length > 2) return null;
            if (!int.TryParse(ends[0], NumberStyles.None, CultureInfo.InvariantCulture, out int a)) return null;
            int b = a;
            if (ends.Length == 2 && !int.TryParse(ends[1], NumberStyles.None, CultureInfo.InvariantCulture, out b)) return null;
            if (a < 1 || b > count || a > b) return null;
            for (int i = a; i <= b; i++) result.Add(i);
        }
        return result;
    }

    // Console.In reads are not truly cancellable. A read abandoned by a timed-out approval is kept and reused by
    // the next prompt, so the user's next line is not swallowed by a reader nobody is waiting on.
    private Task<string?>? _pendingRead;

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var read = _pendingRead ??= _in.ReadLineAsync();
        string? line = await read.WaitAsync(ct).ConfigureAwait(false);
        _pendingRead = null;
        return line;
    }
}
