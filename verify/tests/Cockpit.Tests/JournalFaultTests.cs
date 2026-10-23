using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Cockpit.Core.Files;
using Cockpit.Core.Security;

namespace Cockpit.Tests;

/// <summary>
/// V6 Phase 2c: the journal's state machine under real faults. Crashes are real: a child process runs the journal
/// with a fault point that calls Environment.FailFast at a named boundary (SIGABRT, no finally blocks, no flush),
/// then the parent reopens the journal, checks the invariants, resumes and rolls back. All fixtures are disposable
/// temp folders. Power loss (unflushed page cache) cannot be simulated here; see OPERATION_RECOVERY.md.
/// </summary>
static class JournalFaultTests
{
    public static string TestsDll = "";

    /// <summary>Child-process entry (Program.cs: --journal-child mode db arg boundary hit).</summary>
    public static async Task<int> ChildAsync(string[] a)
    {
        string mode = a[1], db = a[2], arg = a[3], boundary = a[4];
        int hit = int.Parse(a[5], System.Globalization.CultureInfo.InvariantCulture), seen = 0;
        var options = new JournalOptions
        {
            FaultPoint = b =>
            {
                if (b == boundary && ++seen == hit) Environment.FailFast("cockpit test: injected crash at " + b);
            },
        };
        await using var journal = await FileJournalService.OpenAsync(db, new ProtectedPathPolicy([], []), options);
        switch (mode)
        {
            case "plan":
                var spec = JsonSerializer.Deserialize<PlanSpec>(File.ReadAllText(arg))!;
                var plan = await journal.PlanAsync("job", spec.Moves.Select(m => new MoveRequest(m[0], m[1])).ToList(), new PathAuthorizer(spec.Roots), null, CancellationToken.None);
                Console.WriteLine(plan.BatchId);
                return 0;
            case "execute":
                var r = await journal.ExecuteAsync(arg, "job", null, CancellationToken.None);
                Console.WriteLine(r.Status);
                return 0;
            case "rollback":
                var rb = await journal.RollbackAsync(arg, "job", CancellationToken.None);
                Console.WriteLine(rb.Status);
                return 0;
        }
        return 2;
    }

    public sealed record PlanSpec(string[] Roots, string[][] Moves);

    sealed record Fixture(string Root, string Db, Dictionary<string, string> Original, List<MoveRequest> Moves);

    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    static Fixture Make(int files = 6)
    {
        string root = Path.Combine(Path.GetTempPath(), "cockpit-fault", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "inbox"));
        var original = new Dictionary<string, string>();
        var moves = new List<MoveRequest>();
        for (int i = 0; i < files; i++)
        {
            string src = Path.Combine(root, "inbox", $"doc{i}.pdf");
            File.WriteAllText(src, $"content {i} " + new string((char)('a' + i), 100 + i));
            original[src] = Hash(src);
            // Destinations in two new folders (one nested), so mkdir ops are exercised too.
            moves.Add(new MoveRequest(src, Path.Combine(root, i % 2 == 0 ? "sorted" : Path.Combine("sorted", "odd"), $"doc{i}.pdf")));
        }
        return new Fixture(root, Path.Combine(root + "-db", "journal.db"), original, moves);
    }

    static async Task<int> Child(params string[] args)
    {
        var psi = new ProcessStartInfo(Cockpit.Core.Runtime.ComponentLocator.DotnetHost()) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(TestsDll);
        psi.ArgumentList.Add("--journal-child");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    /// <summary>Every original file exists exactly once, at its source or its destination, with its original content.</summary>
    static void ExactlyOnce(Fixture f, string when)
    {
        var all = Directory.EnumerateFiles(f.Root, "*", SearchOption.AllDirectories).ToList();
        Check.Equal(f.Original.Count, all.Count, $"{when}: no file lost or duplicated");
        foreach (var m in f.Moves)
        {
            bool atSrc = File.Exists(m.From), atDst = File.Exists(m.To);
            Check.True(atSrc ^ atDst, $"{when}: {Path.GetFileName(m.From)} is at exactly one of source/destination (src {atSrc}, dst {atDst})");
            Check.Equal(f.Original[m.From], Hash(atSrc ? m.From : m.To), $"{when}: {Path.GetFileName(m.From)} content unchanged");
        }
    }

    static async Task<BatchStatus> StatusAfterReopen(Fixture f, string batch)
    {
        await using var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], []));
        return await j.GetStatusAsync(batch, "job", CancellationToken.None);
    }

    /// <summary>Crash at every execute/rollback boundary (first and a middle hit), then recover, resume, roll back fully.</summary>
    public static async Task CrashAtEveryBoundary()
    {
        int scenarios = 0;
        foreach (string boundary in FileJournalService.Boundaries.Where(b => b != "plan.before_commit"))
        {
            bool inRollback = boundary.StartsWith("rollback", StringComparison.Ordinal) || boundary.StartsWith("undo", StringComparison.Ordinal);
            foreach (int hit in boundary is "move.after_fingerprint" or "move.after_rename" or "undo.after_rename" or "mkdir.after_create" ? new[] { 1, 2 } : new[] { 1 })
            {
                scenarios++;
                string tag = $"{boundary}#{hit}";
                var f = Make();
                string batch;
                await using (var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], [])))
                {
                    var plan = await j.PlanAsync("job", f.Moves, new PathAuthorizer([f.Root]), null, CancellationToken.None);
                    Check.Equal(0, plan.Rejected.Count, tag + ": plan clean");
                    batch = plan.BatchId;
                    if (inRollback) Check.Equal("committed", (await j.ExecuteAsync(batch, "job", null, CancellationToken.None)).Status, tag + ": executed before the rollback crash");
                }

                int code = await Child(inRollback ? "rollback" : "execute", f.Db, batch, boundary, hit.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Check.True(code != 0, $"{tag}: the child really crashed (exit {code})");
                ExactlyOnce(f, tag + " after crash");

                // Reopen: recovery must leave no batch in a transient state, and op states must match the disk.
                var st = await StatusAfterReopen(f, batch);
                Check.True(st.Status is not ("executing" or "rolling_back"), $"{tag}: recovered status is stable ({st.Status})");

                await using (var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], [])))
                {
                    if (!inRollback)
                    {
                        var resumed = await j.ExecuteAsync(batch, "job", null, CancellationToken.None);
                        Check.Equal("committed", resumed.Status, $"{tag}: resume completes the batch ({string.Join("; ", resumed.Errors)})");
                        Check.True(f.Moves.All(m => File.Exists(m.To) && !File.Exists(m.From)), $"{tag}: every file at its destination after resume");
                    }
                    var rb = await j.RollbackAsync(batch, "job", CancellationToken.None);
                    Check.True(rb.Conflicts.Count == 0, $"{tag}: rollback without conflicts ({string.Join("; ", rb.Conflicts)})");
                }
                Check.True(f.Moves.All(m => File.Exists(m.From) && !File.Exists(m.To)), $"{tag}: every file back at its source");
                Check.True(!Directory.Exists(Path.Combine(f.Root, "sorted")), $"{tag}: folders the batch created are gone again");
                ExactlyOnce(f, tag + " final");
            }
        }

        // Plan: a crash inside the plan transaction leaves no batch at all.
        {
            var f = Make();
            string spec = Path.Combine(f.Root + "-db", "spec.json");
            Directory.CreateDirectory(Path.GetDirectoryName(spec)!);
            File.WriteAllText(spec, JsonSerializer.Serialize(new PlanSpec([f.Root], f.Moves.Select(m => new[] { m.From, m.To }).ToArray())));
            int code = await Child("plan", f.Db, spec, "plan.before_commit", "1");
            Check.True(code != 0, "plan: the child crashed");
            await using var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], []));
            Check.Equal(0, (await j.ListBatchesAsync("job")).Count, "plan crash: the uncommitted batch does not exist");
            ExactlyOnce(f, "plan crash");
            scenarios++;
        }
        Console.WriteLine($"      {scenarios} crash scenarios across {FileJournalService.Boundaries.Count} boundaries: all recovered, resumed and rolled back exactly");
    }

    /// <summary>A file changed after approval is skipped; one changed between fingerprint and rename is not rolled back over.</summary>
    public static async Task ConcurrentModification()
    {
        var f = Make(3);
        await using (var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], [])))
        {
            var plan = await j.PlanAsync("job", f.Moves, new PathAuthorizer([f.Root]), null, CancellationToken.None);
            File.AppendAllText(f.Moves[1].From, " edited after approval");
            var r = await j.ExecuteAsync(plan.BatchId, "job", null, CancellationToken.None);
            Check.True(File.Exists(f.Moves[1].From) && !File.Exists(f.Moves[1].To), "the edited file was not moved");
            Check.True(r.Skipped >= 1 && r.Errors.Any(e => e.Contains("changed since the move was approved", StringComparison.Ordinal)), "reported as changed since approval: " + string.Join("; ", r.Errors));
        }

        // Modified in the gap between the write-ahead fingerprint and the rename: rollback must refuse to move it back blind.
        var g = Make(2);
        string target = g.Moves[0].From;
        var opts = new JournalOptions { FaultPoint = b => { if (b == "move.after_fingerprint" && File.Exists(target)) File.AppendAllText(target, " racing edit"); } };
        await using (var j = await FileJournalService.OpenAsync(g.Db, new ProtectedPathPolicy([], []), opts))
        {
            var plan = await j.PlanAsync("job", g.Moves, new PathAuthorizer([g.Root]), null, CancellationToken.None);
            await j.ExecuteAsync(plan.BatchId, "job", null, CancellationToken.None);
            var rb = await j.RollbackAsync(plan.BatchId, "job", CancellationToken.None);
            Check.True(rb.Conflicts.Any(c => c.Contains("changed since it was moved", StringComparison.Ordinal)), "rollback refused the file that changed: " + string.Join("; ", rb.Conflicts));
            Check.True(File.Exists(g.Moves[0].To) && File.ReadAllText(g.Moves[0].To).EndsWith("racing edit", StringComparison.Ordinal), "the edited file stays where it is, edit intact");
        }
    }

    /// <summary>Stale rollback: a later batch moved the file on; rolling back the older one first conflicts, in order it works.</summary>
    public static async Task StaleRollbackAndSecondProcess()
    {
        var f = Make(1);
        string a = f.Moves[0].From, b = Path.Combine(f.Root, "b.pdf"), c = Path.Combine(f.Root, "c.pdf");
        await using (var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], [])))
        {
            var p1 = await j.PlanAsync("job", [new(a, b)], new PathAuthorizer([f.Root]), null, CancellationToken.None);
            await j.ExecuteAsync(p1.BatchId, "job", null, CancellationToken.None);
            var p2 = await j.PlanAsync("job", [new(b, c)], new PathAuthorizer([f.Root]), null, CancellationToken.None);
            await j.ExecuteAsync(p2.BatchId, "job", null, CancellationToken.None);
            var stale = await j.RollbackAsync(p1.BatchId, "job", CancellationToken.None);
            Check.True(stale.Conflicts.Single().Contains("no longer at the destination", StringComparison.Ordinal) && File.Exists(c), "stale rollback conflicts and moves nothing");
            await j.RollbackAsync(p2.BatchId, "job", CancellationToken.None);
            var again = await j.RollbackAsync(p1.BatchId, "job", CancellationToken.None);
            Check.True(again.Conflicts.Count == 0 && File.Exists(a) && !File.Exists(b) && !File.Exists(c), "newest first, then the older batch: back to the start");

            // A second Cockpit process (here: a second open) cannot use the journal concurrently.
            try
            {
                await using var second = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], []));
                throw new Exception("Assertion failed: a second journal instance opened the same database");
            }
            catch (JournalException ex) when (ex.Message.Contains("in use by another Cockpit process", StringComparison.Ordinal)) { }
        }
    }

    /// <summary>Permission failure (real, as uid 65534): the unmovable file fails, the rest commit, rollback restores what moved.</summary>
    public static async Task PermissionFailure()
    {
        if (!OperatingSystem.IsLinux() || !Cockpit.Core.Isolation.BubblewrapSandbox.RunningAsRoot) throw new SkipException("needs root to run the child as uid 65534");
        if (Cockpit.Core.Jobs.CliLaunchBuilder.ResolveOnPath("setpriv") is not string setpriv) throw new SkipException("setpriv missing");
        // The child runs the test binary as uid 65534, so every folder above it must be searchable by others.
        for (var d = new DirectoryInfo(Path.GetDirectoryName(TestsDll)!); d is not null; d = d.Parent)
            if ((File.GetUnixFileMode(d.FullName) & UnixFileMode.OtherExecute) == 0)
                throw new SkipException($"{d.FullName} is not searchable by other users, so uid 65534 cannot start the test binary; run the suite from a checkout outside it");
        var f = Make(4);
        string dbDir = Path.GetDirectoryName(f.Db)!;
        Directory.CreateDirectory(dbDir);
        string lockedDir = Path.Combine(f.Root, "inbox-locked");
        Directory.CreateDirectory(lockedDir);
        string locked = Path.Combine(lockedDir, "locked.pdf");
        File.WriteAllText(locked, "cannot move me");
        f.Original[locked] = Hash(locked);
        f.Moves.Add(new MoveRequest(locked, Path.Combine(f.Root, "sorted", "locked.pdf")));
        string batch;
        await using (var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], [])))
            batch = (await j.PlanAsync("job", f.Moves, new PathAuthorizer([f.Root]), null, CancellationToken.None)).BatchId;
        // Everything belongs to uid 65534 except the locked folder, which stays root-owned and read-only.
        foreach (var path in Directory.EnumerateFileSystemEntries(f.Root, "*", SearchOption.AllDirectories).Append(f.Root).Append(dbDir).Concat(Directory.EnumerateFiles(dbDir)))
            if (!path.StartsWith(lockedDir, StringComparison.Ordinal)) Cockpit.Core.Platform.LinuxFs.Chown(path, 65534, 65534);
        File.SetUnixFileMode(lockedDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var psi = new ProcessStartInfo(setpriv) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in new[] { "--reuid=65534", "--regid=65534", "--clear-groups", "--", Cockpit.Core.Runtime.ComponentLocator.DotnetHost(), TestsDll, "--journal-child", "execute", f.Db, batch, "none", "1" })
            psi.ArgumentList.Add(x);
        psi.Environment["DOTNET_CLI_HOME"] = "/tmp";
        psi.Environment["HOME"] = "/tmp";
        using var p = Process.Start(psi)!;
        string stdout = await p.StandardOutput.ReadToEndAsync(), stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Check.True(p.ExitCode == 0 && stdout.Trim() == "partially_committed", $"run as uid 65534: partially committed (exit {p.ExitCode}: {stdout} {stderr})");
        Check.True(File.Exists(locked), "the file in the read-only folder stayed put");
        Check.True(f.Moves.Where(m => m.From != locked).All(m => File.Exists(m.To)), "the other files moved");
        await using (var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], [])))
        {
            var st = await j.GetStatusAsync(batch, "job", CancellationToken.None);
            Check.True(st.OpStates.GetValueOrDefault("failed") == 1, "exactly one op failed: " + string.Join(",", st.OpStates.Select(kv => kv.Key + "=" + kv.Value)));
            var rb = await j.RollbackAsync(batch, "job", CancellationToken.None);
            Check.True(rb.Conflicts.Count == 0, "rollback restores what moved");
        }
        ExactlyOnce(f, "permission failure");
        Check.True(f.Moves.All(m => File.Exists(m.From)), "all files at their sources again");
    }

    /// <summary>Disk full (real, a 2 MiB tmpfs for the journal): the journal stops before moving files it cannot record; after space is freed it reconciles and finishes.</summary>
    public static async Task DiskFullJournal()
    {
        if (!OperatingSystem.IsLinux() || !Cockpit.Core.Isolation.BubblewrapSandbox.RunningAsRoot) throw new SkipException("needs root to mount a small tmpfs");
        var f = Make(40);
        string dbDir = Path.GetDirectoryName(f.Db)!;
        Directory.CreateDirectory(dbDir);
        var mount = Process.Start("mount", ["-t", "tmpfs", "-o", "size=1m", "tmpfs", dbDir])!;
        await mount.WaitForExitAsync();
        if (mount.ExitCode != 0) throw new SkipException("cannot mount tmpfs here");
        string filler = Path.Combine(dbDir, "filler.bin");
        try
        {
            string batch;
            await using (var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], [])))
            {
                batch = (await j.PlanAsync("job", f.Moves, new PathAuthorizer([f.Root]), null, CancellationToken.None)).BatchId;
                // Fill the volume to the last byte.
                try
                {
                    using var fs = new FileStream(filler, FileMode.Create);
                    var buf = new byte[4096];
                    while (true) fs.Write(buf);
                }
                catch (IOException) { }
                try
                {
                    var r = await j.ExecuteAsync(batch, "job", null, CancellationToken.None);
                    // If SQLite still had room in its WAL, the run may finish; that is acceptable only if it is consistent.
                    Console.WriteLine($"      disk full: execute returned {r.Status} (the WAL had room)");
                }
                catch (JournalException ex)
                {
                    Check.True(ex.Message.Contains("could not record progress", StringComparison.Ordinal), "disk full is reported as a journal failure: " + ex.Message);
                    Console.WriteLine("      disk full: execute stopped with: " + ex.Message[..Math.Min(120, ex.Message.Length)]);
                }
            }
            ExactlyOnce(f, "disk full");
            File.Delete(filler);
            await using (var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], [])))
            {
                var st = await j.GetStatusAsync(batch, "job", CancellationToken.None);
                Check.True(st.Status is not ("executing" or "rolling_back"), "reconciled after space was freed: " + st.Status);
                if (st.Status != "committed")
                {
                    var r = await j.ExecuteAsync(batch, "job", null, CancellationToken.None);
                    Check.Equal("committed", r.Status, "finishes once space is back");
                }
                Check.True(f.Moves.All(m => File.Exists(m.To)), "every file moved exactly once");
                await j.RollbackAsync(batch, "job", CancellationToken.None);
            }
            ExactlyOnce(f, "disk full final");
        }
        finally
        {
            Process.Start("umount", ["-l", dbDir])!.WaitForExit();
        }
    }

    /// <summary>Windows only: a file held open by another process (sharing violation) fails its op without touching the rest.</summary>
    public static Task LockedFileWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("mandatory file locks are a Windows behaviour (NOT RUN here)");
        return LockedFileWindowsImpl();
    }

    static async Task LockedFileWindowsImpl()
    {
        var f = Make(3);
        await using var j = await FileJournalService.OpenAsync(f.Db, new ProtectedPathPolicy([], []));
        var plan = await j.PlanAsync("job", f.Moves, new PathAuthorizer([f.Root]), null, CancellationToken.None);
        using (new FileStream(f.Moves[1].From, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = await j.ExecuteAsync(plan.BatchId, "job", null, CancellationToken.None);
            Check.True(r.Failed == 1 && r.Status == "partially_committed", "locked file failed, others moved");
        }
        var again = await j.ExecuteAsync(plan.BatchId, "job", null, CancellationToken.None);
        Check.True(again.Status is "committed" or "partially_committed", "resume after unlock");
    }
}
