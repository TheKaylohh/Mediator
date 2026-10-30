using System.Diagnostics;
using System.Text.Json.Nodes;
using Mediator.Core.Bus;
using Mediator.Core.Files;
using Mediator.Connectors.Resolve;
using Mediator.Core.Runtime;
using Mediator.Core.Security;
using Mediator.Core.Tools;
using Mediator.Connectors.Tutor;

namespace Mediator.Tests;

/// <summary>
/// V6 Phase 2a: filesystem authorization under attack. Everything here uses real symlinks, real bind mounts and
/// real concurrent swaps in disposable temp folders. Windows-only cases (junctions, ADS on NTFS, 8.3 names that
/// really exist) are in verify/windows and are NOT run here; the Windows lexical rules are tested on any OS.
/// </summary>
static class SecurityTests
{
    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "mediator-sec", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static Task PathSyntaxRules()
    {
        var w = PathSyntax.Windows;
        (string Path, string? Expect)[] windows =
        [
            (@"C:\Users\me\file.txt", null),
            (@"D:\", null),
            (@"D:\Archive\2024 report.pdf", null),
            (@"C:\a\tilde~name.txt", null),
            (@"C:/Users/me/forward.txt", null),
            (@"\\?\C:\Windows\x", "device or extended-length"),
            (@"\\.\PhysicalDrive0", "device or extended-length"),
            (@"\??\C:\x", "device or extended-length"),
            (@"//?/C:/x", "device or extended-length"),
            (@"\\server\share\x", "network (UNC)"),
            (@"//server/share/x", "network (UNC)"),
            (@"C:\a\b.txt:secret", "alternate data stream"),
            (@"C:\a\b.txt::$DATA", "alternate data stream"),
            (@"C:\a\file.", "ends with a dot or space"),
            (@"C:\a\file ", "ends with a dot or space"),
            (@"C:\a\dir.\x.txt", "ends with a dot or space"),
            (@"C:\a\ lead\x", "starts with a space"),
            (@"C:\a\CON", "reserved device name"),
            (@"C:\a\nul.txt", "reserved device name"),
            (@"C:\a\COM1.log", "reserved device name"),
            (@"C:\a\lpt9", "reserved device name"),
            (@"C:\PROGRA~1\x", "8.3 short name"),
            (@"C:\a\REPORT~2.PDF", "8.3 short name"),
            (@"C:\a\..\b", "relative segments"),
            (@"C:\a\.\b", "relative segments"),
            (@"relative\x", "absolute with a drive letter"),
            (@"C:a\b", "absolute with a drive letter"),
            (@"C:\a\b<c", "reserved character"),
            (@"C:\a\b*", "reserved character"),
            ("C:\\a\\b\0c", "NUL"),
            ("C:\\a\\b\u0001c", "control character"),
            ("", "empty"),
        ];
        foreach (var (p, expect) in windows)
        {
            string? got = w.Check(p);
            if (expect is null) Check.True(got is null, $"windows accepts '{p}' (got: {got})");
            else Check.True(got?.Contains(expect, StringComparison.Ordinal) == true, $"windows refuses '{p}' with '{expect}' (got: {got ?? "accepted"})");
        }

        var x = PathSyntax.Posix;
        Check.True(x.Check("/home/u/a.txt") is null, "posix accepts absolute");
        Check.True(x.Check("rel/a") is not null, "posix refuses relative");
        Check.True(x.Check("/a/../etc/passwd")?.Contains("relative segments", StringComparison.Ordinal) == true, "posix refuses ..");
        Check.True(x.Check("/a/\0b") is not null, "posix refuses NUL");

        // Component comparison: no prefix confusion, case rules per OS.
        Check.True(!w.IsSameOrInside(@"C:\data\Project\x", @"C:\data\Proj"), "Proj does not contain Project (windows)");
        Check.True(!x.IsSameOrInside("/data/Project/x", "/data/Proj"), "Proj does not contain Project (posix)");
        Check.True(w.IsSameOrInside(@"c:\DATA\proj\x", @"C:\data\Proj"), "windows comparison is case-insensitive");
        Check.True(!x.IsSameOrInside("/DATA/proj/x", "/data/proj"), "posix comparison is case-sensitive");
        Check.Equal(0, x.RelativeDepth("/data/proj", "/data/proj"), "root depth 0");
        Check.Equal(2, w.RelativeDepth(@"C:\data\proj\a\b", @"C:\data\proj"), "depth by component");
        Check.True(w.IsSameOrInside(@"D:\x", @"D:\"), "drive root contains its files");
        return Task.CompletedTask;
    }

    sealed record Layout(string Root, string Outside, string Secret);

    static Layout MakeLayout()
    {
        string root = TempDir("root"), outside = TempDir("outside");
        string secret = Path.Combine(outside, "secret.txt");
        File.WriteAllText(secret, "OUTSIDE-SECRET");
        Directory.CreateDirectory(Path.Combine(root, "real"));
        File.WriteAllText(Path.Combine(root, "real", "ok.txt"), "inside");
        File.CreateSymbolicLink(Path.Combine(root, "link_file"), secret);
        Directory.CreateSymbolicLink(Path.Combine(root, "link_dir"), outside);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateSymbolicLink(Path.Combine(root, "sub", "rel_link"), Path.Combine("..", "..", Path.GetFileName(outside)));
        File.CreateSymbolicLink(Path.Combine(root, "dangling"), "/nonexistent/mediator-" + Guid.NewGuid().ToString("N"));
        return new Layout(root, outside, secret);
    }

    static void Expect(PathVerdict v, string code, string what) =>
        Check.True(!v.Allowed && v.Code == code, $"{what}: expected {code}, got {(v.Allowed ? "allowed" : v.Code + " " + v.Reason)}");

    static void ExpectDenied(Action action, string code, string what)
    {
        try
        {
            action();
            throw new Exception($"Assertion failed: {what}: was allowed");
        }
        catch (PathDeniedException ex)
        {
            Check.True(ex.Verdict.Code == code, $"{what}: expected {code}, got {ex.Verdict.Code} ({ex.Verdict.Reason})");
        }
    }

    /// <summary>Symlinks: final, intermediate, relative, dangling; root protection; prefix confusion; no overwrite. Kernel path and fallback.</summary>
    public static Task SymlinkEscapes()
    {
        if (!OperatingSystem.IsLinux()) throw new SkipException("Linux symlink cases; Windows equivalents are in verify/windows");
        foreach (bool fallback in new[] { false, true })
        {
            PathAuthorizer.ForceFallback = fallback;
            try { SymlinkCases(fallback ? "fallback" : "openat2"); }
            finally { PathAuthorizer.ForceFallback = false; }
        }
        return Task.CompletedTask;
    }

    static void SymlinkCases(string mode)
    {
        var l = MakeLayout();
        var paths = new PathAuthorizer([l.Root]);
        string R(params string[] p) => Path.Combine([l.Root, .. p]);

        Expect(paths.Check(R("link_file"), PathAccess.Read), "reparse_point", $"{mode}: final-component file symlink");
        Expect(paths.Check(R("link_dir", "secret.txt"), PathAccess.Read), "reparse_point", $"{mode}: intermediate dir symlink");
        Expect(paths.Check(R("sub", "rel_link", "secret.txt"), PathAccess.Read), "reparse_point", $"{mode}: relative symlink two levels down");
        Expect(paths.Check(R("link_dir", "new.txt"), PathAccess.CreateFile), "reparse_point", $"{mode}: create through a dir symlink");
        Expect(paths.Check(R("dangling"), PathAccess.CreateFile), "reparse_point", $"{mode}: create over a dangling symlink");
        Expect(paths.Check(R("link_dir"), PathAccess.Modify), "reparse_point", $"{mode}: the link itself cannot be moved as a file");
        ExpectDenied(() => paths.OpenRead(R("link_file")).Dispose(), "reparse_point", $"{mode}: OpenRead of a symlink");
        ExpectDenied(() => paths.CreateNew(R("link_dir", "x.txt")).Dispose(), "reparse_point", $"{mode}: CreateNew through a symlink");
        ExpectDenied(() => paths.EnsureDirectory(R("link_dir", "made", "deeper")), "reparse_point", $"{mode}: EnsureDirectory through a symlink");
        Check.True(!File.Exists(Path.Combine(l.Outside, "x.txt")) && !Directory.Exists(Path.Combine(l.Outside, "made")), $"{mode}: nothing written outside");

        Expect(paths.Check(l.Outside + "/secret.txt", PathAccess.Read), "outside_roots", $"{mode}: plain outside path");
        Expect(paths.Check(l.Root + "/../" + Path.GetFileName(l.Outside) + "/secret.txt", PathAccess.Read), "syntax", $"{mode}: dot-dot escape");
        Expect(paths.Check(l.Root + "x/file", PathAccess.CreateFile), "outside_roots", $"{mode}: sibling with the root as a string prefix");

        Expect(paths.Check(l.Root, PathAccess.Modify), "root_itself", $"{mode}: root cannot be modified");
        Expect(paths.Check(l.Root, PathAccess.CreateDirectory), "root_itself", $"{mode}: root cannot be recreated");
        Check.True(paths.Check(l.Root, PathAccess.List).Allowed, $"{mode}: root can be listed");
        ExpectDenied(() => paths.RemoveEmptyDirectory(l.Root), "root_itself", $"{mode}: root cannot be removed");
        ExpectDenied(() => paths.MoveNoReplace(l.Root, l.Root + "-moved"), "root_itself", $"{mode}: root cannot be renamed");

        using (var s = new StreamReader(paths.OpenRead(R("real", "ok.txt")))) Check.Equal("inside", s.ReadToEnd(), $"{mode}: plain file reads");
        paths.WriteNewText(R("real", "new.txt"), "n");
        ExpectDenied(() => paths.WriteNewText(R("real", "new.txt"), "again"), "exists", $"{mode}: no overwrite");
        Check.Equal("n", File.ReadAllText(R("real", "new.txt")), $"{mode}: existing file untouched");
        Check.True(paths.EnsureDirectory(R("real", "a", "b", "c")) == R("real", "a", "b", "c") && Directory.Exists(R("real", "a", "b", "c")), $"{mode}: nested mkdir");
        paths.MoveNoReplace(R("real", "new.txt"), R("real", "a", "moved.txt"));
        Check.True(File.Exists(R("real", "a", "moved.txt")) && !File.Exists(R("real", "new.txt")), $"{mode}: move inside the root");
        ExpectDenied(() => paths.MoveNoReplace(R("real", "ok.txt"), R("real", "a", "moved.txt")), "exists", $"{mode}: move never overwrites");
        ExpectDenied(() => paths.MoveNoReplace(R("real", "ok.txt"), R("link_dir", "stolen.txt")), "reparse_point", $"{mode}: move into a linked folder");
        Check.True(!File.Exists(Path.Combine(l.Outside, "stolen.txt")) && File.Exists(R("real", "ok.txt")), $"{mode}: moved nothing out");
        Check.True(!paths.RemoveEmptyDirectory(R("real", "a")), $"{mode}: non-empty folder is not removed");

        // A root that is itself a symlink resolves to its real path once; both spellings then work.
        string alias = l.Root + "-alias";
        Directory.CreateSymbolicLink(alias, l.Root);
        var viaAlias = new PathAuthorizer([alias]);
        Check.Equal(l.Root, viaAlias.Roots.Single(), $"{mode}: root resolved to its real path");
        var v = viaAlias.Check(Path.Combine(alias, "real", "ok.txt"), PathAccess.Read);
        Check.True(v.Allowed && v.FullPath == R("real", "ok.txt"), $"{mode}: alias spelling maps onto the real root ({v.Message})");
        Expect(viaAlias.Check(Path.Combine(alias, "link_file"), PathAccess.Read), "reparse_point", $"{mode}: links below an aliased root still refused");

        // A root that does not exist is a configuration error, not an empty grant.
        try
        {
            _ = new PathAuthorizer([Path.Combine(l.Root, "missing")]);
            throw new Exception("Assertion failed: missing root accepted");
        }
        catch (ArgumentException) { }
    }

    /// <summary>A bind mount inside a root is not a symlink; it must still be refused (mountinfo walk + RESOLVE_NO_XDEV).</summary>
    public static Task BindMountInsideRoot()
    {
        if (!OperatingSystem.IsLinux()) throw new SkipException("Linux bind mounts");
        var l = MakeLayout();
        string mnt = Path.Combine(l.Root, "mnt");
        Directory.CreateDirectory(mnt);
        var bind = Process.Start(new ProcessStartInfo("mount", ["--bind", l.Outside, mnt]) { RedirectStandardError = true })!;
        string err = bind.StandardError.ReadToEnd();
        bind.WaitForExit();
        if (bind.ExitCode != 0) throw new SkipException($"cannot bind-mount here (needs CAP_SYS_ADMIN): {err.Trim()}");
        try
        {
            var paths = new PathAuthorizer([l.Root]);
            Expect(paths.Check(Path.Combine(mnt, "secret.txt"), PathAccess.Read), "crosses_mount", "read through a bind mount");
            Expect(paths.Check(Path.Combine(mnt, "new.txt"), PathAccess.CreateFile), "crosses_mount", "create through a bind mount");
            Expect(paths.Check(mnt, PathAccess.Modify), "crosses_mount", "the mount point itself");
            ExpectDenied(() => paths.CreateNew(Path.Combine(mnt, "x.txt")).Dispose(), "crosses_mount", "CreateNew through a bind mount");
            Check.True(!File.Exists(Path.Combine(l.Outside, "x.txt")), "nothing written through the mount");
        }
        finally
        {
            Process.Start("umount", [mnt]).WaitForExit();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// TOCTOU: a thread swaps a folder inside the root for a symlink to an outside folder as fast as it can while the
    /// main thread creates and reads files through it. Nothing may ever be written outside, and no read may return
    /// outside content. Run for the kernel-enforced path (openat2) and the detect-and-undo fallback.
    /// </summary>
    public static async Task SwapRace()
    {
        if (!OperatingSystem.IsLinux()) throw new SkipException("Linux symlink race; the Windows junction race is in verify/windows");
        foreach (bool fallback in new[] { false, true })
        {
            var l = MakeLayout();
            string swap = Path.Combine(l.Root, "swap");
            Directory.CreateDirectory(swap);
            File.WriteAllText(Path.Combine(swap, "secret.txt"), "inside");
            var paths = new PathAuthorizer([l.Root]);
            using var stop = new CancellationTokenSource();
            long swaps = 0;
            var swapper = Task.Run(() =>
            {
                string real = Path.Combine(l.Root, "swap-real");
                string link = Path.Combine(l.Root, "swap-link");
                Directory.Move(swap, real);
                Directory.CreateSymbolicLink(link, l.Outside);
                while (!stop.IsCancellationRequested)
                {
                    // rename(2) is atomic: 'swap' is always either the real folder or the link.
                    Directory.Move(real, swap);
                    Directory.Move(swap, real);
                    Directory.Move(link, swap); // renames the link itself, not its target
                    Directory.Move(swap, link);
                    swaps++;
                }
                Directory.Move(real, swap);
            });

            PathAuthorizer.ForceFallback = fallback;
            int created = 0, denied = 0, readsInside = 0, readsDenied = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                for (int i = 0; sw.ElapsedMilliseconds < 1500; i++)
                {
                    try { paths.CreateNew(Path.Combine(swap, $"f{i}.txt")).Dispose(); created++; }
                    catch (PathDeniedException) { denied++; }
                    catch (IOException) { denied++; } // folder briefly missing mid-swap
                    try
                    {
                        using var s = new StreamReader(paths.OpenRead(Path.Combine(swap, "secret.txt")));
                        string text = s.ReadToEnd();
                        Check.True(text == "inside", $"race ({(fallback ? "fallback" : "openat2")}): a read returned outside content");
                        readsInside++;
                    }
                    catch (PathDeniedException) { readsDenied++; }
                    catch (IOException) { readsDenied++; }
                }
            }
            finally
            {
                PathAuthorizer.ForceFallback = false;
                stop.Cancel();
                await swapper;
            }
            var leaked = Directory.GetFiles(l.Outside, "f*.txt");
            Check.True(leaked.Length == 0, $"race ({(fallback ? "fallback" : "openat2")}): {leaked.Length} file(s) ended up outside the root");
            Check.True(swaps > 100 && created > 0 && denied > 0, $"race actually raced: {swaps} swaps, {created} created inside, {denied} refused");
            Console.WriteLine($"      race {(fallback ? "fallback" : "openat2 ")}: {swaps} swaps; creates {created} inside / {denied} refused; reads {readsInside} inside / {readsDenied} refused; 0 outside");
        }
    }

    /// <summary>Providers go through the authorizer: a PDF behind a symlink and a timeline naming outside media are refused over the bus.</summary>
    public static async Task ProvidersRefuseLinks(string bridgeDll)
    {
        if (!OperatingSystem.IsLinux()) throw new SkipException("uses Linux symlinks");
        var l = MakeLayout();
        File.WriteAllBytes(Path.Combine(l.Outside, "book.pdf"), TestPdf.Build());
        File.CreateSymbolicLink(Path.Combine(l.Root, "book.pdf"), Path.Combine(l.Outside, "book.pdf"));
        File.WriteAllText(Path.Combine(l.Outside, "private.mp4"), "x");
        Directory.CreateDirectory(Path.Combine(l.Root, "edits"));
        string fcpxml = Path.Combine(l.Root, "edits", "evil.fcpxml");
        File.WriteAllText(fcpxml, $"""<?xml version="1.0"?><!DOCTYPE fcpxml><fcpxml version="1.9"><resources><asset id="r1"><media-rep kind="original-media" src="{new Uri(Path.Combine(l.Outside, "private.mp4")).AbsoluteUri}"/></asset></resources></fcpxml>""");

        await using var runtime = await MediatorRuntime.CreateAsync(new RuntimeOptions
        {
            Paths = new MediatorPaths(TempDir("data")),
            Bus = new BusOptions { PipeName = "mediator-sec-" + Guid.NewGuid().ToString("N")[..10] },
            Bridge = new ComponentCommand(ComponentLocator.DotnetHost(), [bridgeDll]),
            Python = new FakePythonRunner((_, _) => new ProcessResult(1, "", "", false), available: false),
            Connectors = TestConnectors.All(mediaProbe: new FakeMediaProbe(new()), blender: new FakeBlender()),
        });
        runtime.Approvals.Target = new AutoApprovalPrompt();

        var tutor = await runtime.StartJobAsync(TutorConnector.Tutor(l.Root, "chapter 1", LaunchMode.None) with { JobId = "sec-tutor" });
        await using (var client = await runtime.ConnectClientAsync(tutor))
        {
            var r = await client.CallToolAsync(TutorToolProvider.ExtractTool, new JsonObject { ["pdfPath"] = Path.Combine(l.Root, "book.pdf"), ["firstPage"] = 1, ["lastPage"] = 1 }, TimeSpan.FromSeconds(30));
            Check.True(r.IsError && r.Text.Contains("[path:reparse_point]", StringComparison.Ordinal), "PDF behind a symlink refused: " + r.Text);
        }
        var resolve = await runtime.StartJobAsync(ResolveConnector.ResolveEdit(l.Root, "x", LaunchMode.None) with { JobId = "sec-resolve" });
        await using (var client = await runtime.ConnectClientAsync(resolve))
        {
            var r = await client.CallToolAsync(ResolveToolProvider.ApplyTool, new JsonObject { ["fcpxmlPath"] = fcpxml, ["projectName"] = "p" }, TimeSpan.FromSeconds(30));
            Check.True(r.IsError && r.Text.Contains("[path:outside_roots]", StringComparison.Ordinal), "timeline media outside the roots refused: " + r.Text);
            Check.True(!Directory.EnumerateFiles(Path.Combine(l.Root, "edits"), "*.py").Any(), "no Resolve script was written");
        }
        var audit = await runtime.Audit.ListAsync("sec-tutor");
        Check.True(audit.Any(a => a.Detail?.Contains("[path:reparse_point]", StringComparison.Ordinal) == true), "the refusal is in the audit trail with its rule");
    }

    /// <summary>Journal: sources through links are rejected at plan time; a destination folder swapped for a link after approval stops that move.</summary>
    public static async Task JournalRefusesLinks()
    {
        if (!OperatingSystem.IsLinux()) throw new SkipException("uses Linux symlinks");
        var l = MakeLayout();
        string inbox = Path.Combine(l.Root, "inbox");
        string dest = Path.Combine(l.Root, "dest");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(inbox, "a.pdf"), "A");
        File.WriteAllText(Path.Combine(inbox, "b.pdf"), "B");
        var paths = new PathAuthorizer([l.Root]);
        await using var journal = await FileJournalService.OpenAsync(Path.Combine(TempDir("db"), "j.db"), new ProtectedPathPolicy([], []));

        var plan = await journal.PlanAsync("job", [
            new(Path.Combine(l.Root, "link_dir", "secret.txt"), Path.Combine(dest, "stolen.txt")),
            new(Path.Combine(inbox, "a.pdf"), Path.Combine(l.Root, "link_dir", "a.pdf")),
            new(Path.Combine(inbox, "b.pdf"), Path.Combine(dest, "b.pdf")),
        ], paths, null, CancellationToken.None);
        Check.Equal(2, plan.Rejected.Count, "both link moves rejected at plan time");
        Check.True(plan.Rejected.All(r => r.Reason.Contains("reparse_point", StringComparison.Ordinal)), "rejected for the link: " + string.Join("; ", plan.Rejected.Select(r => r.Reason)));

        // After approval, the destination folder is replaced with a link to the outside folder.
        Directory.Move(dest, dest + "-gone");
        Directory.CreateSymbolicLink(dest, l.Outside);
        var exec = await journal.ExecuteAsync(plan.BatchId, "job", null, CancellationToken.None);
        Check.Equal(1, exec.Failed, "the swapped move failed");
        Check.True(File.Exists(Path.Combine(inbox, "b.pdf")) && !File.Exists(Path.Combine(l.Outside, "b.pdf")), "file stayed in place; nothing reached the outside folder");
    }

    /// <summary>
    /// Windows only (SKIP elsewhere): real junctions (no privilege needed, which is why they matter), a junction race,
    /// a real alternate data stream and a real 8.3 short name. NOT RUN in the Linux environment where V6 was built.
    /// </summary>
    public static async Task WindowsJunctions()
    {
        if (!OperatingSystem.IsWindows()) throw new SkipException("needs Windows (NTFS junctions, ADS, 8.3 names)");
        string root = TempDir("wroot"), outside = TempDir("woutside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "OUTSIDE");
        void Junction(string link, string target)
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { RedirectStandardOutput = true, UseShellExecute = false })!;
            p.WaitForExit();
            Check.True(p.ExitCode == 0, "mklink /J " + link);
        }
        Junction(Path.Combine(root, "j"), outside);
        var paths = new PathAuthorizer([root]);
        Expect(paths.Check(Path.Combine(root, "j", "secret.txt"), PathAccess.Read), "reparse_point", "read through a junction");
        Expect(paths.Check(Path.Combine(root, "j", "new.txt"), PathAccess.CreateFile), "reparse_point", "create through a junction");
        File.WriteAllText(Path.Combine(root, "plain.txt"), "p");
        File.WriteAllText(Path.Combine(root, "plain.txt") + ":hidden", "ads");
        Expect(paths.Check(Path.Combine(root, "plain.txt") + ":hidden", PathAccess.Read), "syntax", "a real ADS is refused by name");

        // Junction swap race: the handle check must catch every create that the swap redirected.
        string swap = Path.Combine(root, "swap");
        Directory.CreateDirectory(swap);
        using var stop = new CancellationTokenSource();
        var swapper = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { Directory.Delete(swap); Junction(swap, outside); Directory.Delete(swap); Directory.CreateDirectory(swap); }
                catch (IOException) { }
            }
        });
        var sw = Stopwatch.StartNew();
        for (int i = 0; sw.ElapsedMilliseconds < 3000; i++)
        {
            try { paths.CreateNew(Path.Combine(swap, $"f{i}.txt")).Dispose(); }
            catch (PathDeniedException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        stop.Cancel();
        await swapper;
        Check.True(Directory.GetFiles(outside, "f*.txt").Length == 0, "no file left outside after the junction race");
    }
}
