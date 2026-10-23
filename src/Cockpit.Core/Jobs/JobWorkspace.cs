using System.Globalization;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Runtime;
using Cockpit.Protocol;

namespace Cockpit.Core.Jobs;

public sealed record JobWorkspace(string JobId, string Root, string ArtifactsDir, string ClaudeMcpConfigPath, string GeminiSettingsPath);

/// <summary>
/// Creates a job's private folder and the MCP configs that point each CLI at cockpit-mcp-bridge with this
/// job's ID and token. V6: the token is passed to the bridge in its environment (the configs' "env" block),
/// never in its arguments, so it does not appear in any process command line. The config files are owner-only
/// on Unix; on Windows they sit in the user's profile. The token is bound server-side to this job's grant and
/// dies with the job.
/// </summary>
public static class JobWorkspaceFactory
{
    public const string ServerName = "cockpit";

    public static JobWorkspace Create(string workspacesRoot, JobGrant grant, string bridgeExecutable, BusEndpoint endpoint, TimeSpan bridgeTimeout) =>
        Create(workspacesRoot, grant, new ComponentCommand(bridgeExecutable, []), endpoint, bridgeTimeout);

    /// <summary>The bridge's arguments for a job, after any prefix (e.g. the .dll path when started through dotnet).</summary>
    public static IReadOnlyList<string> BridgeArguments(JobGrant grant, BusEndpoint endpoint, TimeSpan bridgeTimeout) =>
        ["--job-id", grant.JobId, "--endpoint", endpoint.ToString(),
         "--timeout-seconds", ((int)bridgeTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture)];

    public const string TokenVariable = "COCKPIT_JOB_TOKEN";

    /// <summary>The bridge's environment for a job: the token, and nothing else.</summary>
    public static IReadOnlyDictionary<string, string> BridgeEnvironment(JobGrant grant) =>
        new Dictionary<string, string>(StringComparer.Ordinal) { [TokenVariable] = grant.Token };

    private static void WriteOwnerOnly(string path, string content)
    {
        File.WriteAllText(path, content, Utf8.NoBom);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static JobWorkspace Create(string workspacesRoot, JobGrant grant, ComponentCommand bridge, BusEndpoint endpoint, TimeSpan bridgeTimeout)
    {
        string root = Path.Combine(Path.GetFullPath(workspacesRoot), grant.JobId);
        string artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifacts);

        string bridgeExecutable = bridge.FileName;
        var args = new JsonArray(bridge.PrefixArguments.Concat(BridgeArguments(grant, endpoint, bridgeTimeout)).Select(a => (JsonNode)a).ToArray());

        // Claude Code: passed with --mcp-config <file> --strict-mcp-config so no other server leaks in.
        string claudePath = Path.Combine(root, "mcp.claude.json");
        var claude = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = bridgeExecutable,
                    ["args"] = args.DeepClone(),
                    ["env"] = new JsonObject { [TokenVariable] = grant.Token },
                },
            },
        };
        WriteOwnerOnly(claudePath, claude.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        // Gemini CLI: project settings in <cwd>/.gemini/settings.json. "trust": true skips Gemini's own
        // confirmation because the Cockpit gate is the authority; "timeout" (ms) must exceed the bridge's.
        string geminiDir = Path.Combine(root, ".gemini");
        Directory.CreateDirectory(geminiDir);
        string geminiPath = Path.Combine(geminiDir, "settings.json");
        var gemini = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [ServerName] = new JsonObject
                {
                    ["command"] = bridgeExecutable,
                    ["args"] = args.DeepClone(),
                    ["env"] = new JsonObject { [TokenVariable] = grant.Token },
                    ["timeout"] = (long)(bridgeTimeout + TimeSpan.FromSeconds(30)).TotalMilliseconds,
                    ["trust"] = true,
                },
            },
        };
        WriteOwnerOnly(geminiPath, gemini.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return new JobWorkspace(grant.JobId, root, artifacts, claudePath, geminiPath);
    }
}
