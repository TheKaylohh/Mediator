using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Files;
using Mediator.Core.Runtime;
using Mediator.Core.Security;
using Mediator.Connectors.Tutor;

namespace Mediator.Tests;

/// <summary>
/// `Mediator.Tests.dll --bench &lt;bridge.dll&gt; &lt;out.json&gt;`: measurements on the machine it runs on, for
/// docs/v6/BENCHMARKS.md. These are measurements, not targets. They use real processes, pipes, SQLite and files.
/// </summary>
static class Benchmarks
{
    static string Temp(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-bench", tag + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    static JsonObject Stats(List<double> ms)
    {
        ms.Sort();
        double P(double q) => ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(q * ms.Count) - 1)];
        return new JsonObject
        {
            ["n"] = ms.Count, ["p50Ms"] = Math.Round(P(0.50), 3), ["p95Ms"] = Math.Round(P(0.95), 3), ["p99Ms"] = Math.Round(P(0.99), 3),
            ["maxMs"] = Math.Round(ms[^1], 3), ["meanMs"] = Math.Round(ms.Average(), 3),
        };
    }

    public static async Task<int> RunAsync(string bridgeDll, string outFile)
    {
        var results = new JsonObject
        {
            ["environment"] = new JsonObject
            {
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["dotnet"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ["cpus"] = Environment.ProcessorCount,
                ["cpuModel"] = File.Exists("/proc/cpuinfo") ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':')[1].Trim() : null,
                ["memTotal"] = File.Exists("/proc/meminfo") ? File.ReadLines("/proc/meminfo").First().Split(':')[1].Trim() : null,
                ["build"] = "Debug, MediatorVerify=true (SQLite shim over the system libsqlite3; not the shipping Microsoft.Data.Sqlite)",
                ["when"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            },
        };
        void Log(string name, JsonNode value) { results[name] = value; Console.WriteLine($"{name}: {value.ToJsonString()}"); }

        // 1. Core startup (state folder, 4 SQLite stores, bus listening): 5 cold-ish starts.
        var startup = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await using var rt = await OperationTests.Runtime(bridgeDll, Temp("start"));
            startup.Add(sw.Elapsed.TotalMilliseconds);
        }
        Log("coreStartup", Stats(startup));

        await using var runtime = await OperationTests.Runtime(bridgeDll, Temp("main"));
        var probe = new ProbeWriteProvider(TimeSpan.FromSeconds(30));
        runtime.Tools.Register(probe);
        Process.GetCurrentProcess().Refresh();
        Log("coreWorkingSetMiB", Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1));
        string work = Temp("work");

        // 2. Bridge process start + MCP initialize (what a CLI pays per session).
        var connect = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var job = await runtime.StartJobAsync(OperationTests.Spec("bench-conn-" + i, work));
            var sw = Stopwatch.StartNew();
            await using var c = await runtime.ConnectClientAsync(job);
            connect.Add(sw.Elapsed.TotalMilliseconds);
        }
        Log("bridgeStartAndInitialize", Stats(connect));

        var benchJob = await runtime.StartJobAsync(OperationTests.Spec("bench", work));
        await using var client = await runtime.ConnectClientAsync(benchJob);
        // 3. Read-only round trip through the real bridge and pipe (validation, gate auto, audit) — no side effects.
        for (int i = 0; i < 20; i++) await client.CallToolAsync(OperationsToolProvider.StatusTool, new JsonObject { ["operationId"] = "none" }, TimeSpan.FromSeconds(20));
        var ro = new List<double>();
        for (int i = 0; i < 500; i++)
        {
            var sw = Stopwatch.StartNew();
            await client.CallToolAsync(OperationsToolProvider.StatusTool, new JsonObject { ["operationId"] = "none" }, TimeSpan.FromSeconds(20));
            ro.Add(sw.Elapsed.TotalMilliseconds);
        }
        Log("mcpRoundTripReadOnly", Stats(ro));

        // 4. Write dispatch: durable operation record (5 synchronous=FULL transitions), auto approval, audit append.
        var wr = new List<double>();
        for (int i = 0; i < 300; i++)
        {
            var sw = Stopwatch.StartNew();
            await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = i }, TimeSpan.FromSeconds(20));
            wr.Add(sw.Elapsed.TotalMilliseconds);
        }
        Log("mcpRoundTripWriteWithOperationRecord", Stats(wr));
        var dispatchOverhead = new JsonObject
        {
            ["writeMinusReadP50Ms"] = Math.Round(((JsonObject)results["mcpRoundTripWriteWithOperationRecord"]!)["p50Ms"]!.GetValue<double>() - ((JsonObject)results["mcpRoundTripReadOnly"]!)["p50Ms"]!.GetValue<double>(), 3),
        };
        Log("writeDispatchOverhead", dispatchOverhead);

        // 5. Concurrency: 8 jobs × 40 read-only calls at once.
        var clients = new List<BridgeMcpClient>();
        for (int j = 0; j < 8; j++) clients.Add(await runtime.ConnectClientAsync(await runtime.StartJobAsync(OperationTests.Spec("bench-cc-" + j, work))));
        var cc = Stopwatch.StartNew();
        await Task.WhenAll(clients.Select(c => Task.Run(async () =>
        {
            for (int i = 0; i < 40; i++) await c.CallToolAsync(OperationsToolProvider.StatusTool, new JsonObject(), TimeSpan.FromSeconds(30));
        })));
        Log("concurrent8x40", new JsonObject { ["totalMs"] = Math.Round(cc.Elapsed.TotalMilliseconds, 1), ["callsPerSecond"] = Math.Round(320 / cc.Elapsed.TotalSeconds, 1) });
        foreach (var c in clients) await c.DisposeAsync();

        // 6. Audit append (redaction + hash chain + synchronous=FULL), standalone.
        await using (var audit = await AuditLog.OpenAsync(Path.Combine(Temp("audit"), "a.db")))
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++)
                await audit.RecordAsync(new AuditEntry("b", "t", "c", ToolRisk.Write, $"{{\"path\":\"C:\\\\x\\\\{i}.txt\",\"n\":{i}}}", "approved", "ok", null, 1));
            double ms = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var v = await audit.VerifyAsync();
            Log("auditAppend", new JsonObject { ["rows"] = 2000, ["perRowMs"] = Math.Round(ms / 2000, 3), ["verifyAllMs"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1), ["ok"] = v.Ok });
        }

        // 7. File journal: plan, execute and roll back 2000 small files (real renames, fsyncs, hashes).
        {
            string root = Temp("journal");
            Directory.CreateDirectory(Path.Combine(root, "in"));
            var moves = new List<MoveRequest>();
            for (int i = 0; i < 2000; i++)
            {
                string src = Path.Combine(root, "in", $"f{i:D5}.txt");
                File.WriteAllText(src, new string('x', 2048));
                moves.Add(new MoveRequest(src, Path.Combine(root, "out", $"d{i % 20}", $"f{i:D5}.txt")));
            }
            await using var journal = await FileJournalService.OpenAsync(Path.Combine(Temp("jdb"), "j.db"), new ProtectedPathPolicy([], []));
            var paths = new PathAuthorizer([root]);
            var sw = Stopwatch.StartNew();
            var plan = await journal.PlanAsync("b", moves, paths, null, CancellationToken.None);
            double planMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var exec = await journal.ExecuteAsync(plan.BatchId, "b", null, CancellationToken.None);
            double execMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var rb = await journal.RollbackAsync(plan.BatchId, "b", CancellationToken.None);
            double rbMs = sw.Elapsed.TotalMilliseconds;
            Log("journal2000Files", new JsonObject
            {
                ["planMs"] = Math.Round(planMs, 1), ["executeMs"] = Math.Round(execMs, 1), ["rollbackMs"] = Math.Round(rbMs, 1),
                ["executePerFileMs"] = Math.Round(execMs / 2000, 3), ["status"] = exec.Status, ["rollback"] = rb.Status,
            });
        }

        // 8. Path authorization (kernel-checked open) per call.
        {
            string root = Temp("auth");
            string f = Path.Combine(root, "a", "b", "c.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, "x");
            var paths = new PathAuthorizer([root]);
            var check = new List<double>();
            var open = new List<double>();
            for (int i = 0; i < 2000; i++)
            {
                var sw = Stopwatch.StartNew();
                paths.Check(f, PathAccess.Read);
                check.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                using (paths.OpenRead(f)) { }
                open.Add(sw.Elapsed.TotalMilliseconds);
            }
            Log("pathCheck", Stats(check));
            Log("pathOpenReadVerified", Stats(open));
        }

        // 9. Built-in PDF reader: a 400-page generated PDF (Flate content, ~2 KB text per page).
        {
            var ms = new MemoryStream();
            ms.Write("%PDF-1.5\n"u8);
            int pages = 400;
            var objs = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, pages).Select(i => $"{3 + 2 * i} 0 R")) + $"] /Count {pages} /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>",
            };
            var raw = new List<byte[]>();
            for (int i = 0; i < pages; i++)
            {
                var content = new StringBuilder("BT /F1 10 Tf ");
                for (int line = 0; line < 40; line++) content.Append(CultureInfo.InvariantCulture, $"72 {760 - 18 * line} Td (Page {i} line {line}: the determinant of A equals the product of its eigenvalues.) Tj ");
                content.Append("ET");
                var z = new MemoryStream();
                using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, true)) zs.Write(Encoding.ASCII.GetBytes(content.ToString()));
                objs.Add($"<< /Type /Page /Parent 2 0 R /Contents {4 + 2 * i} 0 R >>");
                objs.Add($"STREAM{raw.Count}");
                raw.Add(z.ToArray());
            }
            for (int n = 0; n < objs.Count; n++)
            {
                ms.Write(Encoding.ASCII.GetBytes($"{n + 1} 0 obj\n"));
                if (objs[n].StartsWith("STREAM", StringComparison.Ordinal))
                {
                    var data = raw[int.Parse(objs[n][6..], CultureInfo.InvariantCulture)];
                    ms.Write(Encoding.ASCII.GetBytes($"<< /Length {data.Length} /Filter /FlateDecode >>\nstream\n"));
                    ms.Write(data);
                    ms.Write("\nendstream"u8);
                }
                else ms.Write(Encoding.ASCII.GetBytes(objs[n]));
                ms.Write("\nendobj\n"u8);
            }
            ms.Write("trailer << /Root 1 0 R >>\n%%EOF\n"u8);
            var pdf = ms.ToArray();
            var times = new List<double>();
            int chars = 0;
            for (int r = 0; r < 3; r++)
            {
                var sw = Stopwatch.StartNew();
                var doc = PdfTextExtractor.Extract(pdf, 1, pages);
                times.Add(sw.Elapsed.TotalMilliseconds);
                chars = doc.Pages.Sum(p => p.Text.Length);
            }
            Log("pdf400Pages", new JsonObject { ["bytes"] = pdf.Length, ["textChars"] = chars, ["timing"] = Stats(times) });
        }

        File.WriteAllText(outFile, results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Wrote " + outFile);
        return 0;
    }
}
