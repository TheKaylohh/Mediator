using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Cockpit.Core.Bus;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Runtime;
using Cockpit.Core.Security;
using Microsoft.Data.Sqlite;

namespace Cockpit.Tests;

/// <summary>V6 Phases 4–5: audit tamper evidence, redaction, retention, crash consistency and the support bundle.</summary>
static class AuditTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-audit", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static AuditEntry E(string job, string args, string? detail = null) => new(job, "files_execute_batch", "c", ToolRisk.Write, args, "approved", "ok", detail, 3);

    static void Raw(string db, string sql)
    {
        using var c = new SqliteConnection($"Data Source={db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static async Task HashChainDetectsTampering()
    {
        foreach (var (name, attack, expect) in new (string, string, string)[]
        {
            ("edit", "DROP TRIGGER audit_no_update; UPDATE audit SET outcome = 'not_run' WHERE id = 3;", "edited"),
            ("delete", "DROP TRIGGER audit_no_delete; DELETE FROM audit WHERE id = 3;", "removed, inserted or reordered"),
            ("insert", "INSERT INTO audit (ts_utc, job_id, tool, call_id, risk, arguments, decision, outcome, detail, duration_ms, prev_hash, row_hash) " +
                       "SELECT ts_utc, job_id, tool, call_id, risk, '{}', decision, outcome, detail, duration_ms, prev_hash, row_hash FROM audit WHERE id = 2;", "removed, inserted or reordered"),
            ("unhashed", "INSERT INTO audit (ts_utc, job_id, tool, call_id, risk, arguments, decision, outcome, detail, duration_ms) VALUES ('t','j','x','c','Write','{}','approved','ok',NULL,1);", "without a hash"),
        })
        {
            string dir = TempDir(name), db = Path.Combine(dir, "audit.db");
            await using (var log = await AuditLog.OpenAsync(db))
            {
                for (int i = 0; i < 5; i++) await log.RecordAsync(E("j", $"{{\"n\":{i}}}"));
                var ok = await log.VerifyAsync();
                Check.True(ok.Ok && ok.RowsChecked == 5 && ok.Head is { Length: 64 }, $"{name}: intact chain verifies");
            }
            SqliteConnection.ClearAllPools();
            Raw(db, attack);
            await using (var log = await AuditLog.OpenAsync(db))
            {
                var bad = await log.VerifyAsync();
                Check.True(!bad.Ok && bad.Problem!.Contains(expect, StringComparison.Ordinal), $"{name}: detected ({bad.Problem}, row {bad.FirstBadId})");
            }
        }

        // The triggers still refuse ordinary edits (defence in depth; the chain is for when they are bypassed).
        string d2 = TempDir("trigger"), db2 = Path.Combine(d2, "audit.db");
        await using (var log = await AuditLog.OpenAsync(db2)) await log.RecordAsync(E("j", "{}"));
        SqliteConnection.ClearAllPools();
        try { Raw(db2, "UPDATE audit SET outcome='x';"); throw new Exception("Assertion failed: update allowed"); }
        catch (SqliteException ex) { Check.True(ex.Message.Contains("append-only", StringComparison.Ordinal), "UPDATE refused by trigger"); }
    }

    /// <summary>A V5 audit file (no hash columns) opens, keeps its rows, and the chain starts after them.</summary>
    public static async Task V5LogMigratesAndRotationContinuesChain()
    {
        string dir = TempDir("v5"), db = Path.Combine(dir, "audit.db");
        Raw(db, """
            CREATE TABLE audit (id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL, job_id TEXT NOT NULL, tool TEXT NOT NULL, call_id TEXT NOT NULL,
              risk TEXT NOT NULL, arguments TEXT NOT NULL, decision TEXT NOT NULL, outcome TEXT NOT NULL, detail TEXT, duration_ms INTEGER NOT NULL);
            INSERT INTO audit (ts_utc, job_id, tool, call_id, risk, arguments, decision, outcome, detail, duration_ms) VALUES ('2026-10-01','old','t','c','Write','{}','approved','ok',NULL,1);
            """);
        string head;
        await using (var log = await AuditLog.OpenAsync(db))
        {
            await log.RecordAsync(E("new", "{}"));
            var v = await log.VerifyAsync();
            Check.True(v.Ok && v.RowsChecked == 2, "V5 row kept; the chain starts at the first V6 row");
            head = (await log.HeadAsync());
        }
        SqliteConnection.ClearAllPools();
        // Retention: past the size limit the file is renamed (never deleted) and the new log continues from its head.
        await using (var log = await AuditLog.OpenAsync(db, new AuditOptions { RotateAtBytes = 1 }))
        {
            Check.True(log.RotatedTo is { } r && File.Exists(r), "old log archived, not deleted");
            await log.RecordAsync(E("after", "{}"));
            var v = await log.VerifyAsync();
            Check.True(v.Ok && v.RowsChecked == 1, "new log verifies and continues the archived chain's head");
        }
        SqliteConnection.ClearAllPools();
        Raw(db, "UPDATE audit_meta SET value = 'forged' WHERE key = 'genesis';");
        await using (var log = await AuditLog.OpenAsync(db, new AuditOptions()))
            Check.True(!(await log.VerifyAsync()).Ok, "a rotated log whose link to the previous head is changed fails verification");
    }

    /// <summary>Redaction: registered secrets, key formats, secret-named JSON fields, auth headers and VAR=value forms.</summary>
    public static async Task RedactionBeforeStorage()
    {
        Redactor.Register("cockpit-registered-secret-123");
        string args = """{"path":"C:\\Users\\me\\a.txt","apiKey":"plain-value","nested":{"Authorization":"Bearer abcdefghijklmnop"},"note":"key sk-ant-api03-ABCDEFGHIJKLMNOP and AIzaSyA1234567890123456789012345678901 and ghp_abcdefghijklmnopqrstuvwxyz0123 and cockpit-registered-secret-123","env":"GEMINI_API_KEY=supersecretvalue"}""";
        string dir = TempDir("redact"), db = Path.Combine(dir, "audit.db");
        await using (var log = await AuditLog.OpenAsync(db))
            await log.RecordAsync(E("r", args, "detail with Bearer zyxwvutsrqponmlk"));
        SqliteConnection.ClearAllPools();
        string all = Convert.ToBase64String(File.ReadAllBytes(db)) + File.ReadAllText(db, System.Text.Encoding.Latin1);
        if (File.Exists(db + "-wal")) all += File.ReadAllText(db + "-wal", System.Text.Encoding.Latin1);
        foreach (var secret in new[] { "plain-value", "abcdefghijklmnop", "sk-ant-api03-ABCDEFGHIJKLMNOP", "AIzaSyA1234567890123456789012345678901", "ghp_abcdefghijklmnopqrstuvwxyz0123",
                     "cockpit-registered-secret-123", "supersecretvalue", "zyxwvutsrqponmlk" })
            Check.True(!all.Contains(secret, StringComparison.Ordinal), $"'{secret[..6]}…' never reaches the audit file");
        await using var again = await AuditLog.OpenAsync(db);
        var row = (await again.ListAsync("r")).Single();
        Check.True(row.ArgumentsJson.Contains("C:\\\\Users\\\\me\\\\a.txt", StringComparison.Ordinal) && row.ArgumentsJson.Contains(Redactor.Mark, StringComparison.Ordinal),
            "paths and ordinary values are kept in the audit (it is the user's own record); secrets are marked");
    }

    /// <summary>Crash consistency: an appender killed with SIGKILL mid-stream leaves a verifiable chain (no torn row).</summary>
    public static async Task<int> ChildAppend(string db)
    {
        await using var log = await AuditLog.OpenAsync(db);
        for (int i = 0; ; i++) await log.RecordAsync(E("crash", $"{{\"i\":{i}}}"));
    }

    public static async Task CrashMidAppendStaysConsistent(string testsDll)
    {
        string dir = TempDir("crash"), db = Path.Combine(dir, "audit.db");
        for (int round = 0; round < 3; round++)
        {
            var psi = new ProcessStartInfo(ComponentLocator.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { testsDll, "--audit-child", db }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            await Task.Delay(700 + 300 * round);
            p.Kill(entireProcessTree: true);
            await p.WaitForExitAsync();
            SqliteConnection.ClearAllPools();
            await using var log = await AuditLog.OpenAsync(db);
            var v = await log.VerifyAsync();
            Check.True(v.Ok && v.RowsChecked > 0, $"round {round}: {v.RowsChecked} rows after SIGKILL, chain intact ({v.Problem})");
            await log.RecordAsync(E("after-crash", "{}"));
            Check.True((await log.VerifyAsync()).Ok, $"round {round}: appending after the crash continues the chain");
        }
    }

    /// <summary>The support bundle carries what is needed to troubleshoot and nothing secret or private.</summary>
    public static async Task SupportBundleIsClean(string bridgeDll)
    {
        string saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-api03-bundle-test-secret-value");
        try
        {
            string work = TempDir("private-folder-name");
            File.WriteAllText(Path.Combine(work, "thesis-draft.docx"), "PRIVATE FILE CONTENT");
            await using var runtime = await OperationTests.Runtime(bridgeDll);
            var probe = new ProbeWriteProvider(TimeSpan.FromSeconds(30));
            runtime.Tools.Register(probe);
            var job = await runtime.StartJobAsync(OperationTests.Spec("bundle-job", work));
            await using (var client = await runtime.ConnectClientAsync(job))
                await client.CallToolAsync(ProbeWriteProvider.Name, new JsonObject { ["x"] = 1, ["tag"] = Path.Combine(work, "thesis-draft.docx") }, TimeSpan.FromSeconds(20));
            await runtime.Audit.RecordAsync(new AuditEntry("bundle-job", "x", "c", ToolRisk.Write, "{\"token\":\"" + job.Grant.Token + "\"}", "approved", "ok",
                "used sk-ant-api03-bundle-test-secret-value at " + work, 1));

            string zip = Path.Combine(TempDir("bundle"), "support.zip");
            await SupportBundle.CreateAsync(runtime, zip, new JsonObject { ["check"] = "data dir " + runtime.Paths.DataDirectory });
            using var archive = ZipFile.OpenRead(zip);
            Check.True(archive.Entries.Select(e => e.Name).Order().SequenceEqual(new[] { "audit.json", "doctor.json", "environment.json", "journal.json", "manifest.json", "operations.json", "usage.json" }),
                "only the documented JSON files: " + string.Join(",", archive.Entries.Select(e => e.Name)));
            string text = string.Concat(archive.Entries.Select(e => new StreamReader(e.Open()).ReadToEnd()));
            foreach (var forbidden in new[] { "sk-ant-api03-bundle-test-secret-value", job.Grant.Token, "private-folder-name", "thesis-draft", "PRIVATE FILE CONTENT", runtime.Paths.DataDirectory })
                Check.True(!text.Contains(forbidden, StringComparison.Ordinal), $"bundle does not contain '{forbidden[..Math.Min(14, forbidden.Length)]}…'");
            var env = JsonNode.Parse(new StreamReader(archive.GetEntry("environment.json")!.Open()).ReadToEnd())!;
            Check.True((bool)env["variablesPresent"]!["ANTHROPIC_API_KEY"]!, "it says the key variable is present (name only)");
            var audit = JsonNode.Parse(new StreamReader(archive.GetEntry("audit.json")!.Open()).ReadToEnd())!;
            Check.True((bool)audit["chainOk"]! && ((string)audit["head"]!).Length == 64 && audit["recent"]!.AsArray().Count >= 2, "audit verification and head included");
            Check.True(text.Contains("<path#", StringComparison.Ordinal) && text.Contains(".docx>", StringComparison.Ordinal), "paths are anonymized but keep their extension");
        }
        finally { Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved.Length == 0 ? null : saved); }
    }
}
