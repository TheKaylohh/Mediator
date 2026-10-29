using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Security;

namespace Cockpit.Core.Attachments;

/// <summary>
/// cockpit_attachments and cockpit_read_attachment (V7): how a job sees what the user dropped, pasted or picked.
/// Both are read-only. Every read goes through the job's own <see cref="PathAuthorizer"/> (the attachment's folder is
/// a read-only root of that job), so a job can only read attachments it was given, and never write to them.
/// Images come back as MCP image content, so a vision-capable model sees them.
/// </summary>
public sealed class AttachmentToolProvider : IToolProvider
{
    public const string ListTool = "cockpit_attachments";
    public const string ReadTool = "cockpit_read_attachment";
    public static readonly string[] Names = [ListTool, ReadTool];

    public const int MaxTextChars = 200_000;
    /// <summary>Images larger than this are described, not sent (model-side limits are around 5 MB).</summary>
    public const long MaxInlineImageBytes = 5L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Attachment>> _byJob = new(StringComparer.Ordinal);

    public void Add(string jobId, Attachment a) => _byJob.GetOrAdd(jobId, _ => new(StringComparer.Ordinal))[a.Id] = a;
    public IReadOnlyList<Attachment> For(string jobId) =>
        _byJob.TryGetValue(jobId, out var d) ? d.Values.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray() : [];
    public void Remove(string jobId) => _byJob.TryRemove(jobId, out _);

    public IReadOnlyList<ToolDescriptor> Tools { get; } =
    [
        new(ListTool, "Lists the files, folders and images the user attached to this job (dropped, pasted or picked in the AI Cockpit). Read them with cockpit_read_attachment.",
            (JsonObject)JsonNode.Parse("""{"type":"object","properties":{},"additionalProperties":false}""")!, ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(5)),
        new(ReadTool, "Reads an attachment by id. Images are returned as images. For a folder attachment, give 'path' relative to the folder to read a file, or omit it to list the folder. Attachments are read-only.",
            (JsonObject)JsonNode.Parse("""{"type":"object","properties":{"id":{"type":"string"},"path":{"type":"string"}},"required":["id"],"additionalProperties":false}""")!,
            ToolRisk.ReadOnly, null, TimeSpan.FromSeconds(30)),
    ];

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct) => Task.FromResult<JsonNode?>(null);

    public Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        try
        {
            return Task.FromResult(call.Tool.Name == ListTool ? List(call.Job) : Read(call.Job, call.Arguments));
        }
        catch (PathDeniedException ex) { return Task.FromResult(ToolResult.Fail(ex.Verdict.Message)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Fail("The attachment could not be read: " + ex.Message));
        }
    }

    private ToolResult List(JobGrant job)
    {
        var items = For(job.JobId);
        var arr = new JsonArray();
        foreach (var a in items)
            arr.Add(new JsonObject
            {
                ["id"] = a.Id,
                ["kind"] = a.Kind.ToString().ToLowerInvariant(),
                ["name"] = a.Name,
                ["bytes"] = a.Kind == AttachmentKind.Folder ? null : a.Bytes,
                ["mediaType"] = a.MediaType,
            });
        string text = items.Count == 0
            ? "The user has not attached anything to this job."
            : string.Join("\n", items.Select(a => $"{a.Id}  {a.Kind.ToString().ToLowerInvariant(),-6}  {a.Name}"));
        return ToolResult.Ok(text, new JsonObject { ["attachments"] = arr });
    }

    private ToolResult Read(JobGrant job, JsonElement args)
    {
        string id = args.GetProperty("id").GetString() ?? "";
        string? rel = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (!_byJob.TryGetValue(job.JobId, out var mine) || !mine.TryGetValue(id, out var a))
            return ToolResult.Fail($"No attachment '{id}' on this job. Call {ListTool} to see the ids.");

        if (a.Kind == AttachmentKind.Folder)
        {
            string target = string.IsNullOrEmpty(rel) ? a.Path : Path.Combine(a.Path, rel);
            var verdict = job.Paths.Check(target, PathAccess.List);
            if (!job.Paths.Syntax.IsSameOrInside(verdict.FullPath, a.GrantedRoot))
                return ToolResult.Fail($"'{rel}' is outside the attached folder {a.Name}.");
            if (Directory.Exists(verdict.FullPath))
            {
                var listed = job.Paths.VerifyExisting(target, PathAccess.List);
                if (!listed.Allowed) return ToolResult.Fail(listed.Message);
                var entries = new DirectoryInfo(listed.FullPath).EnumerateFileSystemInfos().Take(500)
                    .Select(e => e is DirectoryInfo ? e.Name + "/" : $"{e.Name}  ({((FileInfo)e).Length} bytes)").OrderBy(x => x, StringComparer.Ordinal);
                return ToolResult.Ok($"{a.Name}/{rel}\n" + string.Join("\n", entries));
            }
            return ReadFile(job, target, Path.GetFileName(target), AttachmentStore.MediaTypeOf(target));
        }
        if (!string.IsNullOrEmpty(rel)) return ToolResult.Fail("'path' is only for folder attachments.");
        return ReadFile(job, a.Path, a.Name, a.MediaType);
    }

    private static ToolResult ReadFile(JobGrant job, string path, string name, string? mediaType)
    {
        using var fs = job.Paths.OpenRead(path);
        long length = fs.Length;
        if (mediaType is not null)
        {
            if (length > MaxInlineImageBytes)
                return ToolResult.Fail($"{name} is an image of {length / 1024} KiB, over the {MaxInlineImageBytes / (1024 * 1024)} MiB that can be sent to the model. Ask the user for a smaller one.");
            var bytes = new byte[length];
            fs.ReadExactly(bytes);
            return new ToolResult(false, $"{name} ({mediaType}, {length} bytes)") { Images = [new ToolImage(mediaType, bytes)] };
        }
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxTextChars + 1];
        int n = reader.ReadBlock(buffer, 0, buffer.Length);
        string text = new(buffer, 0, Math.Min(n, MaxTextChars));
        if (text.Contains('\0')) return ToolResult.Fail($"{name} is a binary file ({length} bytes); it cannot be shown as text.");
        return ToolResult.Ok(n > MaxTextChars ? text + $"\n… [truncated: {name} is longer than {MaxTextChars} characters]" : text);
    }
}
