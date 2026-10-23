using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Platform;
using Cockpit.Protocol;

namespace Cockpit.Core.Swx;

public sealed class SwxWorkerException : Exception
{
    /// <summary>Set by the worker when SolidWorks shows a modal dialog during a call (see SwxDialogs.cs).</summary>
    public const string ModalDialogBlocking = "MODAL_DIALOG_BLOCKING";
    /// <summary>V8: the worker was built without the SolidWorks interop (SOLIDWORKS_NOT_INSTALLED); every call fails with this code.</summary>
    public const string SolidWorksNotInstalled = "solidworks_not_installed";

    public string Code { get; }
    /// <summary>Extra error detail from the worker, e.g. <c>{title, text, buttons}</c> for a blocking dialog.</summary>
    public JsonObject? Detail { get; }

    public SwxWorkerException(string code, string message, JsonObject? detail = null) : base(message)
    {
        Code = code;
        Detail = detail;
    }
}

public sealed record SwxWorkerOptions(string ExecutablePath, IReadOnlyList<string> Arguments)
{
    /// <summary>Extra time beyond the worker's own timeout before the Core kills it.</summary>
    public TimeSpan KillGrace { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Owns the cockpit-swx-worker process. COM calls into SolidWorks cannot be aborted, so the only reliable
/// way to recover from a hung call is to end the process that made it; this client does that and starts a
/// fresh worker on the next call. SolidWorks itself is never killed.
/// </summary>
public sealed class SwxWorkerClient : ISwxBackend, IAsyncDisposable
{
    private readonly SwxWorkerOptions _options;
    private readonly ChildProcessJob _job;
    private readonly ICockpitLog _log;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private Process? _process;
    private long _nextId;

    public SwxWorkerClient(SwxWorkerOptions options, ChildProcessJob job, ICockpitLog? log = null)
    {
        _options = options;
        _job = job;
        _log = log ?? NullLog.Instance;
    }

    public async Task<JsonNode?> CallAsync(string method, JsonObject parameters, TimeSpan timeout, CancellationToken ct)
    {
        var process = await EnsureStartedAsync(ct).ConfigureAwait(false);
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            string line = new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
                ["timeoutMs"] = (long)timeout.TotalMilliseconds,
            }.ToJsonString();

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await process.StandardInput.WriteLineAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            return await tcs.Task.WaitAsync(timeout + _options.KillGrace, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Kill($"no answer to '{method}' within {timeout + _options.KillGrace:g}");
            throw new SwxWorkerException("timeout", $"SolidWorks did not finish '{method}' in time. The worker was restarted; check SolidWorks for an open dialog.");
        }
        catch (IOException ex)
        {
            throw new SwxWorkerException("worker_unavailable", $"The SolidWorks worker is not running: {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Kill switch for the overlay: ends the worker (and any call it is stuck in) immediately.</summary>
    public void Kill(string reason)
    {
        var p = _process;
        if (p is null) return;
        _log.Warn($"Killing SolidWorks worker (PID {p.Id}): {reason}");
        try { p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    private async Task<Process> EnsureStartedAsync(CancellationToken ct)
    {
        var current = _process;
        if (current is { HasExited: false }) return current;

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } p) return p;

            var psi = new ProcessStartInfo(_options.ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Without explicit encodings Windows uses the OEM code page; UTF-8 must be BOM-free.
                StandardInputEncoding = Utf8.NoBom,
                StandardOutputEncoding = Utf8.NoBom,
                StandardErrorEncoding = Utf8.NoBom,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.ExecutablePath)) ?? Environment.CurrentDirectory,
            };
            foreach (var a in _options.Arguments) psi.ArgumentList.Add(a);

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _stderrTail.Enqueue(e.Data);
                while (_stderrTail.Count > 40 && _stderrTail.TryDequeue(out string? _)) { }
            };
            process.Exited += (_, _) => OnExited(process);
            if (!process.Start()) throw new SwxWorkerException("start_failed", $"Could not start {_options.ExecutablePath}.");
            _job.Assign(process); // dies with the Core, never orphaned
            process.BeginErrorReadLine();
            _process = process;
            _ = Task.Run(() => ReadLoopAsync(process));
            _log.Info($"Started SolidWorks worker PID {process.Id}");
            return process;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                if (line.Length == 0) continue;
                JsonObject? msg;
                try { msg = JsonNode.Parse(line) as JsonObject; }
                catch (System.Text.Json.JsonException) { _log.Warn($"Worker wrote non-JSON: {line}"); continue; }
                if (msg is null) continue;

                if (msg["event"] is JsonNode ev)
                {
                    _log.Warn($"Worker event {(string?)ev}: {(string?)msg["reason"]}");
                    continue;
                }
                if (msg["id"] is not JsonValue idValue || !idValue.TryGetValue(out long id) || !_pending.TryGetValue(id, out var tcs))
                    continue; // late answer for a call that already timed out

                if (msg["error"] is JsonObject err)
                    tcs.TrySetException(new SwxWorkerException((string?)err["code"] ?? "error", (string?)err["message"] ?? "Unknown worker error.",
                        err["dialog"]?.DeepClone() as JsonObject));
                else
                    tcs.TrySetResult(msg["result"]?.DeepClone());
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Process ended; OnExited fails whatever is pending.
        }
    }

    private void OnExited(Process process)
    {
        int code;
        try { code = process.ExitCode; } catch (InvalidOperationException) { code = -1; }
        string tail = string.Join(" | ", _stderrTail.TakeLast(5));
        string why = code == 3 ? "a SolidWorks call hung and the worker restarted itself" : $"worker exited with code {code}";
        _log.Warn($"SolidWorks worker PID {process.Id} exited ({why}). {tail}");
        if (ReferenceEquals(_process, process)) _process = null;
        foreach (var kv in _pending)
            kv.Value.TrySetException(new SwxWorkerException("worker_exited", $"The SolidWorks worker stopped ({why}). The operation's effect is unknown; check SolidWorks. {tail}"));
    }

    public async ValueTask DisposeAsync()
    {
        var p = _process;
        if (p is null) return;
        try
        {
            p.StandardInput.Close(); // graceful: the worker closes the parts it opened, then exits
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(40)).Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
        {
            Kill("did not exit within 40 s of shutdown");
        }
        p.Dispose();
    }
}
