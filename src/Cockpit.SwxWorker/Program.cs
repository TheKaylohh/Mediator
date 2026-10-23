using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cockpit.Protocol;
using Cockpit.Protocol.Swx;
using Cockpit.SwxWorker;

// cockpit-swx-worker: a child process of the Cockpit Core (inside its kill-on-close job object).
// Protocol: one JSON object per line on stdin/stdout.
//   request:  {"id":1,"method":"part_cards","params":{...},"timeoutMs":300000}
//   response: {"id":1,"result":{...}}  or  {"id":1,"error":{"code":"...","message":"..."}}
// Methods: health, part_cards {paths}, build_assembly {plan}, shutdown.
// Exit codes: 0 normal, 2 bad arguments, 3 a COM call wedged the STA thread (Core restarts the worker).
// Built without the SolidWorks interop (SOLIDWORKS_NOT_INSTALLED), every method answers
//   {"id":1,"error":{"code":"solidworks_not_installed","message":"SolidWorks assemblies not installed on this machine: …"}}

var options = new SwxHostOptions();
try
{
    for (int i = 0; i < args.Length; i++)
    {
        options = args[i] switch
        {
            "--allow-launch" => options with { AllowLaunch = true },
            "--attach-wait-seconds" => options with { AttachWait = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture)) },
            "--busy-retry-seconds" => options with { BusyRetryWindow = TimeSpan.FromSeconds(double.Parse(args[++i], CultureInfo.InvariantCulture)) },
            _ => throw new ArgumentException($"Unknown argument '{args[i]}'."),
        };
    }
}
catch (Exception ex) when (ex is ArgumentException or FormatException or IndexOutOfRangeException)
{
    Console.Error.WriteLine($"cockpit-swx-worker: {ex.Message}");
    return 2;
}

await using var stdio = StdioJsonLines.OpenProcessStdio();
// SwxComHost, or SwxUnavailableHost when this worker was built without the SolidWorks interop (V8).
using var host = SwxHostFactory.Create(options);
if (!SwxHostFactory.HasSolidWorksInterop) Console.Error.WriteLine("[swx-worker] built without the SolidWorks interop: every call answers solidworks_not_installed");

host.Wedged += reason =>
{
    // The STA thread is stuck inside SolidWorks and cannot be aborted. Ending the process releases our COM
    // proxies; SolidWorks itself keeps running. The Core sees exit code 3 and starts a fresh worker.
    Console.Error.WriteLine($"[swx-worker] wedged: {reason}");
    try
    {
        var note = new JsonObject { ["event"] = "wedged", ["reason"] = reason }.ToJsonString();
        stdio.WriteLineAsync(note).AsTask().Wait(TimeSpan.FromSeconds(2));
    }
    catch (Exception) { /* exiting anyway */ }
    Environment.Exit(3);
};

var inflight = new List<Task>();
while (await stdio.ReadLineAsync(CancellationToken.None) is string line)
{
    inflight.RemoveAll(t => t.IsCompleted);
    string request = line; // one capture per request
    inflight.Add(Task.Run(() => HandleAsync(request)));
}

// stdin closed: the Core is shutting down or restarting us.
try { await Task.WhenAll(inflight).WaitAsync(TimeSpan.FromSeconds(30)); } catch (Exception) { }
try { await host.ShutdownAsync(TimeSpan.FromSeconds(30)); } catch (Exception ex) { Console.Error.WriteLine($"[swx-worker] shutdown: {ex.Message}"); }
return 0;

async Task HandleAsync(string line)
{
    JsonNode? id = null;
    try
    {
        var request = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("Request must be an object.");
        id = request["id"]?.DeepClone();
        string method = (string?)request["method"] ?? throw new JsonException("Missing method.");
        var @params = request["params"] as JsonObject ?? new JsonObject();
        var timeout = TimeSpan.FromMilliseconds((double?)request["timeoutMs"] ?? 300_000);

        JsonNode? result = method switch
        {
            "health" => JsonValue.Create(await host.HealthAsync(CancellationToken.None)),
            "part_cards" => JsonSerializer.SerializeToNode(
                await host.ExtractPartCardsAsync(
                    @params["paths"]?.Deserialize(SwxJsonContext.Default.StringArray) ?? throw new JsonException("part_cards needs paths."),
                    timeout, CancellationToken.None),
                SwxJsonContext.Default.PartCardBatch),
            "build_assembly" => JsonSerializer.SerializeToNode(
                await host.BuildAssemblyAsync(
                    @params["plan"]?.Deserialize(SwxJsonContext.Default.ResolvedAssemblyPlan) ?? throw new JsonException("build_assembly needs plan."),
                    timeout, CancellationToken.None),
                SwxJsonContext.Default.BuildResult),
            "import_step" => JsonSerializer.SerializeToNode(
                await host.ImportStepAsync(
                    @params["paths"]?.Deserialize(SwxJsonContext.Default.StringArray) ?? throw new JsonException("import_step needs paths."),
                    (string?)@params["outputFolder"] ?? throw new JsonException("import_step needs outputFolder."),
                    timeout, CancellationToken.None),
                SwxJsonContext.Default.StepImportBatch),
            _ => throw new SwxException("unknown_method", $"Unknown method '{method}'."),
        };
        await Reply(new JsonObject { ["id"] = id, ["result"] = result });
    }
    catch (Exception ex)
    {
        if (ex is SwxBlockedByModalDialogException blocked)
        {
            await Reply(new JsonObject
            {
                ["id"] = id,
                ["error"] = new JsonObject
                {
                    ["code"] = SwxBlockedByModalDialogException.Code,
                    ["message"] = blocked.Message,
                    ["dialog"] = new JsonObject
                    {
                        ["title"] = blocked.Dialog.Title,
                        ["text"] = blocked.Dialog.Text,
                        ["buttons"] = new JsonArray(blocked.Dialog.Buttons.Select(b => (JsonNode)b).ToArray()),
                    },
                },
            });
            return;
        }
        (string code, string message) = ex switch
        {
            SwxException s => (s.Code, s.Message),
            StaQueueBusyException => ("busy", ex.Message),
            StaCallTimeoutException => ("timeout", ex.Message),
            StaQueueWedgedException => ("wedged", ex.Message),
            JsonException => ("bad_request", ex.Message),
            COMException com => ("com_error", $"0x{com.HResult:X8}: {com.Message}"),
            _ => ("exception", $"{ex.GetType().Name}: {ex.Message}"),
        };
        await Reply(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
    }
}

async Task Reply(JsonObject message)
{
    try { await stdio.WriteLineAsync(message.ToJsonString()); }
    catch (IOException) { /* Core went away */ }
}
