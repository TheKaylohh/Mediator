using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mediator.Core.Bus;
using Mediator.Core.Jobs;
using Mediator.Core.Platform;
using Mediator.Core.Runtime;

namespace Mediator.Core.Connectors;

/// <summary>The last health ping of a custom server: when, whether it answered, and the tools it listed.</summary>
public sealed record McpPing(DateTimeOffset At, bool Ok, string? Error, JsonArray Tools, long ElapsedMs);

/// <summary>
/// A custom stdio MCP server the user registered in the Connectors tab. Only a command and its arguments are stored:
/// no environment variables, so no secret ever lands in mediator.mcp.json (wrap the server in a script that reads
/// its own secrets if it needs them).
/// </summary>
public sealed record CustomMcpServer(string Name, string Command, IReadOnlyList<string> Args, bool Enabled, McpPing? LastPing)
{
    public IReadOnlyList<string> ToolNames => LastPing?.Tools.Select(t => (string?)t?["name"]).Where(n => n is not null).Select(n => n!).ToArray() ?? [];
}

public sealed class CustomMcpException(string message) : Exception(message);

/// <summary>
/// The custom MCP servers (V7). Rules, so a custom server never gets more than the user explicitly approves:
/// <list type="bullet">
/// <item>Its tools are registered as <c>ext_&lt;server&gt;_&lt;tool&gt;</c> with risk <see cref="ToolRisk.Destructive"/>:
/// every call shows an approval card with the server, the command and the arguments, and can never be auto-approved.</item>
/// <item>Each call starts the server fresh, with API-key and token variables removed, in an empty working folder, inside
/// the Mediator's kill-on-close process group; it ends when the call ends.</item>
/// <item>Only enabled servers whose last ping succeeded are registered. Removing or disabling one unregisters its tools at once.</item>
/// <item>The registry file is written atomically and a malformed file is never overwritten.</item>
/// </list>
/// </summary>
public sealed partial class CustomMcpService
{
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);
    public const int MaxServers = 16;
    public const int MaxToolsPerServer = 64;

    private readonly string _file;
    private readonly string _runDirectory;
    private readonly ToolRegistry _tools;
    private readonly ChildProcessJob? _group;
    private readonly object _lock = new();
    private readonly Dictionary<string, ExternalMcpToolProvider> _registered = new(StringComparer.Ordinal);
    private List<CustomMcpServer> _servers = [];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,15}$")]
    private static partial Regex ServerNamePattern();
    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex ToolNameJunk();

    /// <param name="file">mediator.mcp.json in the data folder.</param>
    public CustomMcpService(string file, ToolRegistry tools, ChildProcessJob? group = null)
    {
        _file = Path.GetFullPath(file);
        _runDirectory = Path.Combine(Path.GetDirectoryName(_file)!, "mcp-runs");
        _tools = tools;
        _group = group;
        LoadError = Load();
        lock (_lock) foreach (var s in _servers) Sync(s);
    }

    /// <summary>Why the file could not be read (it is left untouched and nothing is registered), or null.</summary>
    public string? LoadError { get; }
    public string FilePath => _file;

    public event Action? Changed;

    public IReadOnlyList<CustomMcpServer> Servers { get { lock (_lock) return _servers.ToArray(); } }

    /// <summary>Tool names of enabled, healthy servers: added to every new job's allow-list (each call still needs approval).</summary>
    public IReadOnlyList<string> EnabledToolNames
    {
        get { lock (_lock) return _registered.Values.SelectMany(p => p.Tools).Select(t => t.Name).ToArray(); }
    }

    public static string ExposedName(string server, string tool)
    {
        string t = ToolNameJunk().Replace(tool, "_");
        string name = $"ext_{server.Replace('-', '_')}_{t}";
        return name.Length <= 64 ? name : name[..64];
    }

    /// <summary>Validates, pings (15 s) and saves. A server that fails the ping is saved disabled with the error, so the user sees why.</summary>
    public async Task<CustomMcpServer> AddAsync(string name, string command, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        if (LoadError is not null) throw new CustomMcpException($"{_file} could not be read ({LoadError}); fix or remove it first.");
        name = name.Trim().ToLowerInvariant();
        if (!ServerNamePattern().IsMatch(name)) throw new CustomMcpException("Name: 1–16 characters, lowercase letters, digits and '-', starting with a letter or digit.");
        lock (_lock)
        {
            if (_servers.Any(s => s.Name == name)) throw new CustomMcpException($"A server named '{name}' already exists.");
            if (_servers.Count >= MaxServers) throw new CustomMcpException($"At most {MaxServers} custom servers.");
        }
        if (args.Count > 32 || args.Any(a => a.Length > 1000 || a.Contains('\0'))) throw new CustomMcpException("Too many or too long arguments.");
        string resolved = ResolveCommand(command);
        var ping = await PingAsync(resolved, args, ct).ConfigureAwait(false);
        var server = new CustomMcpServer(name, resolved, args.ToArray(), ping.Ok, ping);
        lock (_lock)
        {
            if (_servers.Any(s => s.Name == name)) throw new CustomMcpException($"A server named '{name}' already exists.");
            _servers.Add(server);
            Save();
            Sync(server);
        }
        Changed?.Invoke();
        return server;
    }

    /// <summary>Pings a registered server again and refreshes its tool list.</summary>
    public async Task<CustomMcpServer> RefreshAsync(string name, CancellationToken ct = default)
    {
        var server = Find(name);
        var ping = await PingAsync(server.Command, server.Args, ct).ConfigureAwait(false);
        return Replace(name, s => s with { LastPing = ping });
    }

    public CustomMcpServer SetEnabled(string name, bool enabled) => Replace(name, s => s with { Enabled = enabled });

    public void Remove(string name)
    {
        lock (_lock)
        {
            var server = _servers.FirstOrDefault(s => s.Name == name) ?? throw new CustomMcpException($"No server named '{name}'.");
            _servers.Remove(server);
            Save();
            Unsync(name);
        }
        Changed?.Invoke();
    }

    private CustomMcpServer Find(string name)
    {
        lock (_lock) return _servers.FirstOrDefault(s => s.Name == name) ?? throw new CustomMcpException($"No server named '{name}'.");
    }

    private CustomMcpServer Replace(string name, Func<CustomMcpServer, CustomMcpServer> change)
    {
        CustomMcpServer updated;
        lock (_lock)
        {
            int i = _servers.FindIndex(s => s.Name == name);
            if (i < 0) throw new CustomMcpException($"No server named '{name}'.");
            updated = change(_servers[i]);
            _servers[i] = updated;
            Save();
            Sync(updated);
        }
        Changed?.Invoke();
        return updated;
    }

    /// <summary>Registers the server's tools when it is enabled and healthy, else makes sure none are registered.</summary>
    private void Sync(CustomMcpServer server)
    {
        Unsync(server.Name);
        if (!server.Enabled || server.LastPing is not { Ok: true } ping) return;
        var provider = new ExternalMcpToolProvider(server, ping.Tools, this);
        if (provider.Tools.Count == 0) return;
        _tools.Register(provider);
        _registered[server.Name] = provider;
    }

    private void Unsync(string name)
    {
        if (_registered.Remove(name, out var old)) _tools.Unregister(old);
    }

    // ── Processes ────────────────────────────────────────────────────────────────────────────────────

    private static string ResolveCommand(string command)
    {
        command = command.Trim().Trim('"');
        if (command.Length == 0) throw new CustomMcpException("Enter the command that starts the server (for example npx, uvx, python or a full path).");
        if (Path.IsPathFullyQualified(command))
        {
            if (!File.Exists(command)) throw new CustomMcpException($"'{command}' does not exist.");
            return command;
        }
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            throw new CustomMcpException("Give a command name found on PATH, or a full path.");
        return CliLaunchBuilder.ResolveOnPath(command) ?? throw new CustomMcpException($"'{command}' was not found on PATH.");
    }

    /// <summary>Variables never passed to a custom server: billing keys and anything that looks like a credential.</summary>
    public static bool IsScrubbed(string variable) =>
        GeminiLaunchBuilder.VariablesToScrub(false).Contains(variable, StringComparer.OrdinalIgnoreCase)
        || variable.StartsWith("MEDIATOR_", StringComparison.OrdinalIgnoreCase)
        || new[] { "TOKEN", "SECRET", "PASSWORD", "PASSWD", "API_KEY", "APIKEY", "CREDENTIAL", "PRIVATE_KEY" }
            .Any(w => variable.Contains(w, StringComparison.OrdinalIgnoreCase));

    internal ProcessStartInfo StartInfo(string command, IReadOnlyList<string> args)
    {
        // The user typed the command and arguments; the model's input goes over stdin as JSON, never on this line.
        var psi = CliLaunchBuilder.WrapForShim(command, args);
        foreach (var key in psi.Environment.Keys.Where(IsScrubbed).ToList()) psi.Environment.Remove(key);
        string dir = Path.Combine(_runDirectory, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        psi.WorkingDirectory = dir;
        return psi;
    }

    internal static void CleanRunDirectory(ProcessStartInfo psi)
    {
        try { Directory.Delete(psi.WorkingDirectory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal ChildProcessJob? Group => _group;

    /// <summary>Starts the server, initializes, lists its tools, stops it. Never throws for a server problem: the result says what went wrong.</summary>
    public async Task<McpPing> PingAsync(string command, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var psi = StartInfo(command, args);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(PingTimeout);
            await using var client = await BridgeMcpClient.StartAsync(psi, _group, "mediator", PingTimeout, timeout.Token).ConfigureAwait(false);
            var tools = await client.ListToolDefinitionsAsync(PingTimeout, timeout.Token).ConfigureAwait(false);
            var kept = new JsonArray();
            foreach (var t in tools.Take(MaxToolsPerServer))
                if (t is JsonObject o && (string?)o["name"] is { Length: > 0 })
                    kept.Add(new JsonObject
                    {
                        ["name"] = (string?)o["name"],
                        ["description"] = Truncate((string?)o["description"] ?? "", 1000),
                        ["inputSchema"] = o["inputSchema"] is JsonObject schema ? schema.DeepClone() : new JsonObject { ["type"] = "object" },
                    });
            return new McpPing(DateTimeOffset.UtcNow, true, null, kept, clock.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is McpClientException or OperationCanceledException or TimeoutException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            string why = ex is OperationCanceledException or TimeoutException ? $"no answer within {PingTimeout.TotalSeconds:0} s" : ex.Message;
            return new McpPing(DateTimeOffset.UtcNow, false, Truncate(why, 500), [], clock.ElapsedMilliseconds);
        }
        finally
        {
            CleanRunDirectory(psi);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ── File ─────────────────────────────────────────────────────────────────────────────────────────

    private string? Load()
    {
        if (!File.Exists(_file)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_file)) as JsonObject ?? throw new JsonException("not a JSON object");
            var list = new List<CustomMcpServer>();
            foreach (var n in root["servers"] as JsonArray ?? [])
            {
                if (n is not JsonObject o) continue;
                string name = (string?)o["name"] ?? "";
                string command = (string?)o["command"] ?? "";
                if (!ServerNamePattern().IsMatch(name) || command.Length == 0 || list.Any(s => s.Name == name)) continue;
                var args = (o["args"] as JsonArray ?? []).Select(a => (string?)a ?? "").ToArray();
                McpPing? ping = o["lastPing"] is JsonObject p
                    ? new McpPing(DateTimeOffset.Parse((string?)p["at"] ?? "2000-01-01T00:00:00Z", CultureInfo.InvariantCulture), (bool?)p["ok"] ?? false,
                        (string?)p["error"], p["tools"]?.DeepClone() as JsonArray ?? [], (long?)p["ms"] ?? 0)
                    : null;
                list.Add(new CustomMcpServer(name, command, args, (bool?)o["enabled"] ?? false, ping));
            }
            _servers = list;
            return null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or IOException)
        {
            return ex.Message;
        }
    }

    private void Save()
    {
        var arr = new JsonArray();
        foreach (var s in _servers)
            arr.Add(new JsonObject
            {
                ["name"] = s.Name,
                ["command"] = s.Command,
                ["args"] = new JsonArray(s.Args.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["enabled"] = s.Enabled,
                ["lastPing"] = s.LastPing is { } p ? new JsonObject
                {
                    ["at"] = p.At.ToString("O", CultureInfo.InvariantCulture),
                    ["ok"] = p.Ok,
                    ["error"] = p.Error,
                    ["ms"] = p.ElapsedMs,
                    ["tools"] = p.Tools.DeepClone(),
                } : null,
            });
        var doc = new JsonObject
        {
            ["$comment"] = "Mediator custom MCP servers. Every tool call needs your approval. Do not put secrets here: environment variables are not supported.",
            ["servers"] = arr,
        };
        AtomicFile.WriteAllText(_file, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>Writes a small file of the Mediator's own (settings, registries) via a temp file and a rename, so a crash never leaves half a file.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(content);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}

/// <summary>
/// The tools of one custom server, proxied through the Mediator's gate. Every tool is Destructive (approval every
/// time, never automatic). Each call starts the server fresh, makes the one call, and stops it.
/// </summary>
public sealed class ExternalMcpToolProvider : IToolProvider
{
    public const long MaxImageBytes = 5L * 1024 * 1024;
    private readonly CustomMcpServer _server;
    private readonly CustomMcpService _service;
    private readonly Dictionary<string, string> _originalNames = new(StringComparer.Ordinal);

    internal ExternalMcpToolProvider(CustomMcpServer server, JsonArray tools, CustomMcpService service)
    {
        _server = server;
        _service = service;
        var list = new List<ToolDescriptor>();
        foreach (var t in tools)
        {
            string original = (string?)t?["name"] ?? "";
            string exposed = CustomMcpService.ExposedName(server.Name, original);
            if (original.Length == 0 || !ToolRegistry.IsValidName(exposed) || _originalNames.ContainsKey(exposed)) continue;
            _originalNames[exposed] = original;
            var schema = t!["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
            list.Add(new ToolDescriptor(exposed,
                $"[Custom MCP server '{server.Name}', runs on the user's PC; every call needs the user's approval] {(string?)t["description"]}",
                (JsonObject)schema.DeepClone(), ToolRisk.Destructive, null, CustomMcpService.CallTimeout));
        }
        Tools = list;
    }

    public string ServerName => _server.Name;
    public IReadOnlyList<ToolDescriptor> Tools { get; }

    public Task<JsonNode?> BuildPreviewAsync(ToolCallContext call, CancellationToken ct)
    {
        string args = call.Arguments.GetRawText();
        if (args.Length > 4000) args = args[..4000] + "…";
        string commandLine = string.Join(' ', new[] { _server.Command }.Concat(_server.Args));
        return Task.FromResult<JsonNode?>(new JsonObject
        {
            ["kind"] = "external_mcp",
            ["title"] = $"Custom tool {_originalNames[call.Tool.Name]} on '{_server.Name}'",
            ["summary"] = $"Runs on your PC, outside the Mediator's file rules: {commandLine}",
            ["selectable"] = false,
            ["rows"] = new JsonArray(
                new JsonObject { ["key"] = "server", ["primary"] = _server.Name, ["detail"] = commandLine },
                new JsonObject { ["key"] = "arguments", ["primary"] = "arguments", ["detail"] = args }),
        });
    }

    public async Task<ToolResult> ExecuteAsync(ToolCallContext call, CancellationToken ct)
    {
        var args = JsonNode.Parse(call.Arguments.GetRawText()) as JsonObject ?? [];
        var psi = _service.StartInfo(_server.Command, _server.Args);
        try
        {
            BridgeMcpClient client;
            try { client = await BridgeMcpClient.StartAsync(psi, _service.Group, "mediator", CustomMcpService.PingTimeout, ct).ConfigureAwait(false); }
            catch (McpClientException ex) { return ToolResult.Fail($"The custom server '{_server.Name}' did not start: {ex.Message}"); }
            await using (client.ConfigureAwait(false))
            {
                try
                {
                    var r = await client.CallToolAsync(_originalNames[call.Tool.Name], args, CustomMcpService.CallTimeout, ct).ConfigureAwait(false);
                    var images = r.Images.Where(i => i.Data.Length <= MaxImageBytes && i.MediaType.StartsWith("image/", StringComparison.Ordinal)).ToArray();
                    return new ToolResult(r.IsError, r.Text, r.Structured) { Images = images };
                }
                catch (McpClientException ex) { return ToolResult.Fail($"The custom server '{_server.Name}' failed: {ex.Message}"); }
            }
        }
        finally
        {
            CustomMcpService.CleanRunDirectory(psi);
        }
    }
}
