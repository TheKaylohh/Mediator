using System.Diagnostics;
using System.Text.Json.Nodes;
using Cockpit.Core.Attachments;
using Cockpit.Core.Bus;
using Cockpit.Core.Files;
using Cockpit.Core.Runtime;
using Cockpit.Core.Security;

namespace Cockpit.Tests;

/// <summary>V7 attachments: drag-drop, paste and the Attach button feed these; read-only grants keep the V6 invariants.</summary>
static class AttachmentTests
{
    /// <summary>A real 1×1 PNG.</summary>
    public static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    static string TempDir(string tag)
    {
        string d = Path.Combine(Path.GetTempPath(), "cockpit-attach", tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static void Throws<T>(Action a, string what, string? contains = null) where T : Exception
    {
        try { a(); }
        catch (T ex)
        {
            if (contains is not null) Check.True(ex.Message.Contains(contains, StringComparison.Ordinal), $"{what}: message '{ex.Message}' mentions '{contains}'");
            return;
        }
        throw new Exception("Assertion failed: " + what + " (no " + typeof(T).Name + ")");
    }

    sealed class ShiftedTime(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + shift;
    }

    /// <summary>Snapshots, folders, pasted PNGs; links, FIFOs, system files, oversize and fake PNGs refused; discard and prune.</summary>
    public static Task StagingRules()
    {
        string data = TempDir("store");
        string user = TempDir("user");
        var store = new AttachmentStore(Path.Combine(data, "attachments"), maxFileBytes: 64 * 1024);

        // A file is copied into its own folder; the original and its folder are not granted.
        string notes = Path.Combine(user, "notes.txt");
        File.WriteAllText(notes, "torque spec: 12 Nm");
        var a = store.AddPath(notes);
        Check.Equal(AttachmentKind.File, a.Kind, "file kind");
        Check.True(a.GrantedRoot.StartsWith(store.Root, StringComparison.Ordinal) && a.GrantedRoot != store.Root, "granted root is the snapshot's own folder");
        Check.Equal("torque spec: 12 Nm", File.ReadAllText(a.Path), "snapshot content");
        Check.True(File.GetAttributes(a.Path).HasFlag(FileAttributes.ReadOnly), "snapshot is marked read-only");
        File.WriteAllText(notes, "changed later");
        Check.Equal("torque spec: 12 Nm", File.ReadAllText(a.Path), "snapshot does not follow later edits");

        var img = store.AddPath(WritePng(user, "shot.png"));
        Check.Equal(AttachmentKind.Image, img.Kind, "dropped .png is an image");
        Check.Equal("image/png", img.MediaType, "media type");

        // A folder is granted in place (its real path).
        string folder = Directory.CreateDirectory(Path.Combine(user, "drawings")).FullName;
        var f = store.AddPath(folder + Path.DirectorySeparatorChar);
        Check.Equal(AttachmentKind.Folder, f.Kind, "folder kind");
        Check.Equal(folder, f.GrantedRoot, "folder granted in place (trailing separator ignored)");

        if (OperatingSystem.IsLinux())
        {
            string secret = Path.Combine(TempDir("outside"), "secret.txt");
            File.WriteAllText(secret, "outside");
            string link = Path.Combine(user, "innocent.txt");
            File.CreateSymbolicLink(link, secret);
            Throws<AttachmentException>(() => store.AddPath(link), "a file symlink is refused", "link");
            string dirLink = Path.Combine(user, "dirlink");
            Directory.CreateSymbolicLink(dirLink, Path.GetDirectoryName(secret)!);
            Throws<AttachmentException>(() => store.AddPath(dirLink), "a folder symlink is refused", "link");

            // A link higher up the path is fine: the expected real path resolves the parent, not the file.
            string viaParent = Path.Combine(user, "aliasdir");
            Directory.CreateSymbolicLink(viaParent, folder);
            File.WriteAllText(Path.Combine(folder, "a.txt"), "x");
            var viaLink = store.AddPath(Path.Combine(viaParent, "a.txt"));
            Check.Equal("x", File.ReadAllText(viaLink.Path), "a file below a linked parent folder is snapshotted");

            string fifo = Path.Combine(user, "pipe");
            using (var mk = Process.Start("mkfifo", [fifo])) mk.WaitForExit();
            Throws<AttachmentException>(() => store.AddPath(fifo), "a FIFO is refused (and the open does not block)", "regular file");
            Throws<AttachmentException>(() => store.AddPath("/proc/self/environ"), "/proc is refused", "system folder");
            Throws<AttachmentException>(() => store.AddPath("/dev/zero"), "/dev is refused", "system folder");
        }

        string big = Path.Combine(user, "big.bin");
        File.WriteAllBytes(big, new byte[64 * 1024 + 1]);
        int before = Directory.GetDirectories(store.Root).Length;
        Throws<AttachmentException>(() => store.AddPath(big), "over the size cap is refused", "limit");
        Check.Equal(before, Directory.GetDirectories(store.Root).Length, "a refused copy leaves no folder behind");
        Throws<AttachmentException>(() => store.AddPath(Path.Combine(user, "missing.txt")), "a missing file is refused", "does not exist");
        Throws<AttachmentException>(() => store.AddPath(a.Path), "an attachment cannot be attached again", "already");

        // Secrets and whole folders: a fake home with credential folders, the data folder and a drive root.
        string home = TempDir("home");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", ".credentials.json"), "{}");
        Directory.CreateDirectory(Path.Combine(home, ".ssh"));
        File.WriteAllText(Path.Combine(home, ".ssh", "id_ed25519"), "key");
        string projects = Directory.CreateDirectory(Path.Combine(home, "projects", "bracket")).FullName;
        var guarded = new AttachmentStore(Path.Combine(data, "attachments"), home: home);
        Throws<AttachmentException>(() => guarded.AddPath(Path.Combine(home, ".claude", ".credentials.json")), "a CLI credential file is refused", "credentials");
        Throws<AttachmentException>(() => guarded.AddPath(Path.Combine(home, ".ssh")), "the .ssh folder is refused", "credentials");
        Throws<AttachmentException>(() => guarded.AddPath(home), "the whole home folder is refused", "whole user folder");
        Throws<AttachmentException>(() => guarded.AddPath(Path.Combine(home, "projects") + "/../"), "a '..' path is refused before it can normalize to the home folder", "relative segments");
        Throws<AttachmentException>(() => guarded.AddPath(data), "the folder holding the Cockpit's data is refused", "Cockpit's own data");
        Throws<AttachmentException>(() => guarded.AddPath(Path.GetPathRoot(data)!), "a drive root is refused", "whole drive");
        Check.Equal(AttachmentKind.Folder, guarded.AddPath(projects).Kind, "an ordinary project folder under home is fine");
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateSymbolicLink(Path.Combine(user, "tmp-alias"), home);
            Throws<AttachmentException>(() => guarded.AddPath(Path.Combine(user, "tmp-alias", ".ssh")), "a credential folder reached through a linked parent is refused", "credentials");
            Throws<AttachmentException>(() => guarded.AddPath(Path.Combine(user, "tmp-alias", ".ssh", "id_ed25519")), "a key file reached through a linked parent is refused", "credentials");
        }

        // Pasted images: PNG only, header checked.
        var pasted = store.AddPng(TinyPng);
        Check.True(Path.GetFileName(pasted.Path).StartsWith("pasted-", StringComparison.Ordinal) && pasted.Path.EndsWith(".png", StringComparison.Ordinal), "timestamped pasted-*.png");
        Check.True(File.ReadAllBytes(pasted.Path).AsSpan().SequenceEqual(TinyPng), "pasted bytes stored exactly");
        Throws<AttachmentException>(() => store.AddPng([0xFF, 0xD8, 0xFF, 0xE0, .. new byte[40]]), "JPEG bytes are not accepted as a pasted PNG", "PNG");
        Throws<AttachmentException>(() => store.AddPng(TinyPng[..20]), "a truncated PNG is refused", "PNG");

        // Discard: snapshots deleted, folders attached in place never touched.
        store.Discard(a);
        Check.True(!Directory.Exists(a.GrantedRoot), "discarded snapshot deleted");
        store.Discard(f);
        Check.True(Directory.Exists(folder), "discarding a folder attachment leaves the user's folder alone");
        Check.True(File.Exists(notes), "the user's original file is never touched");

        // Prune after the retention period.
        var later = new AttachmentStore(store.Root, new ShiftedTime(TimeSpan.FromDays(8)));
        Check.True(later.Prune() >= 2, "folders older than 7 days are pruned");
        Check.Equal(0, Directory.GetDirectories(store.Root).Length, "nothing left after pruning");
        return Task.CompletedTask;
    }

    static string WritePng(string dir, string name)
    {
        string p = Path.Combine(dir, name);
        File.WriteAllBytes(p, TinyPng);
        return p;
    }

    /// <summary>Read-only roots: read and list allowed; create, move, delete denied with read_only; never takes write access away.</summary>
    public static Task ReadOnlyRoots()
    {
        string w = TempDir("rw");
        string ro = TempDir("ro");
        File.WriteAllText(Path.Combine(ro, "spec.txt"), "spec");
        var paths = new PathAuthorizer([w], [ro]);

        using (var r = paths.OpenRead(Path.Combine(ro, "spec.txt"))) Check.Equal(4L, r.Length, "read inside a read-only root");
        Check.True(paths.Check(ro, PathAccess.List).Allowed, "list a read-only root");
        var create = paths.Check(Path.Combine(ro, "new.txt"), PathAccess.CreateFile);
        Check.True(!create.Allowed && create.Code == "read_only", "create denied with read_only");
        Throws<PathDeniedException>(() => paths.WriteNewText(Path.Combine(ro, "new.txt"), "x"), "WriteNewText denied");
        Throws<PathDeniedException>(() => paths.EnsureDirectory(Path.Combine(ro, "sub")), "mkdir denied");
        Throws<PathDeniedException>(() => paths.MoveNoReplace(Path.Combine(ro, "spec.txt"), Path.Combine(w, "spec.txt")), "moving out of a read-only root denied");
        Check.True(File.Exists(Path.Combine(ro, "spec.txt")) && !File.Exists(Path.Combine(w, "spec.txt")), "nothing moved");
        Check.Equal("read_only", paths.Check(Path.Combine(ro, "spec.txt"), PathAccess.Modify).Code, "modify denied with read_only");
        paths.WriteNewText(Path.Combine(w, "ok.txt"), "x");

        // A read-only root inside a writable one is dropped: write access is never taken away by attaching.
        string nested = Directory.CreateDirectory(Path.Combine(w, "nested")).FullName;
        paths.AddReadOnlyRoot(nested);
        Check.True(paths.Check(Path.Combine(nested, "n.txt"), PathAccess.CreateFile).Allowed, "attaching a folder inside a writable root keeps it writable");
        Check.True(!paths.ReadOnlyRoots.Contains(nested), "the nested read-only root was not added");

        // Added live: read-only immediately, and still contained (links inside it are refused as before).
        string later = TempDir("later");
        File.WriteAllText(Path.Combine(later, "l.txt"), "l");
        var live = new PathAuthorizer([w]);
        Check.True(!live.Check(Path.Combine(later, "l.txt"), PathAccess.Read).Allowed, "not readable before attaching");
        live.AddReadOnlyRoot(later);
        Check.True(live.Check(Path.Combine(later, "l.txt"), PathAccess.Read).Allowed, "readable after attaching");
        Check.Equal("read_only", live.Check(Path.Combine(later, "m.txt"), PathAccess.CreateFile).Code, "still read-only");
        if (OperatingSystem.IsLinux())
        {
            string outside = Path.Combine(TempDir("out"), "o.txt");
            File.WriteAllText(outside, "o");
            File.CreateSymbolicLink(Path.Combine(later, "escape.txt"), outside);
            Throws<PathDeniedException>(() => live.OpenRead(Path.Combine(later, "escape.txt")).Dispose(), "a link inside a read-only root is still refused");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Through the real bridge: listed and read (text and image content), other jobs cannot read them, the files
    /// tool cannot move them, attaching to a running job is audited, and a finished job cannot be attached to.
    /// </summary>
    public static async Task ThroughTheBus(string bridgeDll)
    {
        await using var rt = await OperationTests.Runtime(bridgeDll);
        string user = TempDir("bus-user");
        string work = TempDir("bus-work");
        File.WriteAllText(Path.Combine(user, "brief.md"), "# Bracket\nM6 holes, 40 mm pitch");
        var text = rt.StageAttachment(Path.Combine(user, "brief.md"));
        var png = rt.StagePastedImage(TinyPng);

        var spec = new JobSpec
        {
            Kind = "attach", Title = "attachments", Prompt = "Look at what I attached.", Tools = [FilesToolProvider.PlanMoves], Roots = [work], WorkingDirectory = work,
            Launch = LaunchMode.None, Lifetime = TimeSpan.FromMinutes(10), JobId = "attach-1", Attachments = [text, png],
        };
        var job = await rt.StartJobAsync(spec);
        Check.True(job.Spec.Prompt.Contains(text.Id, StringComparison.Ordinal) && job.Spec.Prompt.Contains(AttachmentToolProvider.ReadTool, StringComparison.Ordinal),
            "the prompt tells the model what is attached and how to read it");
        await using (var client = await rt.ConnectClientAsync(job))
        {
            var tools = await client.ListToolsAsync();
            Check.True(tools.Contains(AttachmentToolProvider.ListTool) && tools.Contains(AttachmentToolProvider.ReadTool), "attachment tools visible");
            var list = await client.CallToolAsync(AttachmentToolProvider.ListTool, new JsonObject(), TimeSpan.FromSeconds(20));
            Check.Equal(2, list.Structured!["attachments"]!.AsArray().Count, "two attachments listed");

            var read = await client.CallToolAsync(AttachmentToolProvider.ReadTool, new JsonObject { ["id"] = text.Id }, TimeSpan.FromSeconds(20));
            Check.True(!read.IsError && read.Text.Contains("40 mm pitch", StringComparison.Ordinal), "text attachment read");
            var image = await client.CallToolAsync(AttachmentToolProvider.ReadTool, new JsonObject { ["id"] = png.Id }, TimeSpan.FromSeconds(20));
            Check.True(!image.IsError && image.Images.Count == 1, "image returned as MCP image content");
            Check.Equal("image/png", image.Images[0].MediaType, "image mime type");
            Check.True(image.Images[0].Data.AsSpan().SequenceEqual(TinyPng), "image bytes intact over the bus");

            var move = await client.CallToolAsync(FilesToolProvider.PlanMoves, new JsonObject
            {
                ["moves"] = new JsonArray(new JsonObject { ["from"] = text.Path, ["to"] = Path.Combine(work, "brief.md") }),
            }, TimeSpan.FromSeconds(20));
            Check.True(move.Text.Contains("read_only", StringComparison.Ordinal) || move.Text.Contains("read-only", StringComparison.Ordinal),
                $"the files tool cannot move an attachment (got: {move.Text})");
            Check.True(File.Exists(text.Path), "attachment still in place");
        }

        // A second job (headless, no attachments) gets no attachment tools and cannot be attached to.
        var job2 = await rt.StartJobAsync(spec with { JobId = "attach-2", Attachments = [], Prompt = "" });
        Check.True(!job2.Grant.AllowedTools.Contains(AttachmentToolProvider.ReadTool), "no attachment tools without attachments (V6 tool lists unchanged)");
        string drawings = Directory.CreateDirectory(Path.Combine(user, "drawings")).FullName;
        File.WriteAllText(Path.Combine(drawings, "rev-b.txt"), "rev B");
        var folder = rt.StageAttachment(drawings);
        Check.True(!await rt.AttachToJobAsync("attach-2", folder), "a job without the attachment tools cannot be attached to");
        await using (var client2 = await rt.ConnectClientAsync(job2))
        {
            var foreign = await client2.CallToolAsync(AttachmentToolProvider.ReadTool, new JsonObject { ["id"] = text.Id }, TimeSpan.FromSeconds(20))
                .ContinueWith(t => t.Exception is null ? t.Result : null);
            Check.True(foreign is null || foreign.IsError, "another job cannot read this job's attachment");
        }

        // Attaching a folder to the running job that has the tools: read-only, audited.
        var toolsBefore = job.Grant.AllowedTools.ToHashSet();
        Check.True(await rt.AttachToJobAsync("attach-1", folder), "attach to a running job");
        await using (var client1 = await rt.ConnectClientAsync(job))
        {
            var listing = await client1.CallToolAsync(AttachmentToolProvider.ReadTool, new JsonObject { ["id"] = folder.Id }, TimeSpan.FromSeconds(20));
            Check.True(listing.Text.Contains("rev-b.txt", StringComparison.Ordinal), "folder listed");
            var inner = await client1.CallToolAsync(AttachmentToolProvider.ReadTool, new JsonObject { ["id"] = folder.Id, ["path"] = "rev-b.txt" }, TimeSpan.FromSeconds(20));
            Check.Equal("rev B", inner.Text, "file inside an attached folder read");
            var up = await client1.CallToolAsync(AttachmentToolProvider.ReadTool, new JsonObject { ["id"] = folder.Id, ["path"] = "../brief.md" }, TimeSpan.FromSeconds(20));
            Check.True(up.IsError, "'..' out of an attached folder is refused");
            var sideways = await client1.CallToolAsync(AttachmentToolProvider.ReadTool, new JsonObject { ["id"] = folder.Id, ["path"] = text.Path }, TimeSpan.FromSeconds(20));
            Check.True(sideways.IsError && sideways.Text.Contains("outside the attached folder", StringComparison.Ordinal), "an absolute path to another attachment is refused under this id");
            Check.Equal("read_only", job.Grant.Paths.Check(Path.Combine(drawings, "x.txt"), PathAccess.CreateFile).Code, "attached folder is read-only for the job");
        }
        var audit = await rt.Audit.ListAsync("attach-1");
        Check.True(audit.Any(r => r.Tool == "cockpit/attach" && r.ArgumentsJson.Contains("read_only", StringComparison.Ordinal)), "attaching is audited");
        Check.True(job.Grant.AllowedTools.SetEquals(toolsBefore), "attaching never changes the tool list");

        await rt.EndJobAsync("attach-1");
        Check.True(!await rt.AttachToJobAsync("attach-1", folder), "a finished job cannot be attached to");
        Check.True(Directory.Exists(drawings) && File.Exists(Path.Combine(drawings, "rev-b.txt")), "the user's folder is untouched");
    }
}
