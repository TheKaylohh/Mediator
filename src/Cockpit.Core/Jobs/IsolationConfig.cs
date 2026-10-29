using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Cockpit.Core.Jobs;

/// <summary>
/// Restricted WSL2 distro for code-CAD, media and PDF tools and Linux coding jobs: no automatic Windows
/// drive mounts, no Windows interop, one explicit exchange folder. Repos live on the distro's own ext4
/// disk (fast) as git worktrees; only artifacts cross through the exchange folder.
/// </summary>
public static class WslIsolation
{
    /// <summary>/etc/wsl.conf for the cockpit distro. Apply with `wsl --terminate &lt;distro&gt;`.</summary>
    public static string RenderWslConf(string defaultUser) => $"""
        [automount]
        enabled = false
        mountFsTab = true

        [interop]
        enabled = false
        appendWindowsPath = false

        [user]
        default = {defaultUser}
        """;

    /// <summary>
    /// /etc/fstab line binding one Windows folder into the distro. Windows paths use forward slashes and
    /// spaces become \040. Mount options are fixed at distro start, so this is configured once, not per job.
    /// </summary>
    public static string RenderFstabLine(string windowsFolder, string linuxMountPoint, bool readOnly, int uid = 1000, int gid = 1000)
    {
        string src = Path.GetFullPath(windowsFolder).Replace('\\', '/').Replace(" ", "\\040", StringComparison.Ordinal);
        string mnt = linuxMountPoint.Replace(" ", "\\040", StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture, $"{src} {mnt} drvfs {(readOnly ? "ro" : "rw")},noatime,uid={uid},gid={gid},metadata 0 0");
    }

    /// <summary>
    /// %UserProfile%\.wslconfig. The memory cap is the real fix for "SolidWorks plus WSL exhausts RAM":
    /// by default WSL2 may take up to half of physical memory. Mirrored networking (Windows 11 22H2+)
    /// lets a bridge inside WSL reach the Core's loopback TCP listener.
    /// </summary>
    public static string RenderDotWslConfig(int memoryGb, int processors) => string.Create(CultureInfo.InvariantCulture, $"""
        [wsl2]
        memory={memoryGb}GB
        processors={processors}
        networkingMode=mirrored

        [experimental]
        autoMemoryReclaim=gradual
        """);
}

/// <summary>
/// Windows Sandbox (.wsb) for running untrusted Windows-side code the agent produced or downloaded: a
/// disposable desktop whose state is discarded on close. It needs Windows Pro, Enterprise or Education
/// (the same requirement as Hyper-V, whose technology it uses) and is not zero-cost: it is a lightweight
/// VM with its own memory. Networking is off by default, so no agent or login runs inside it.
/// </summary>
public static class WindowsSandboxConfig
{
    public const string SandboxUserDesktop = @"C:\Users\WDAGUtilityAccount\Desktop";

    public sealed record Mapping(string HostFolder, string SandboxFolder, bool ReadOnly);

    public static string Render(IReadOnlyList<Mapping> mappings, string? logonCommand, int memoryMb = 4096, bool networking = false)
    {
        foreach (var m in mappings)
            if (!Directory.Exists(m.HostFolder)) throw new DirectoryNotFoundException($"Sandbox mapping source {m.HostFolder} does not exist.");

        var doc = new XElement("Configuration",
            new XElement("VGpu", "Disable"),
            new XElement("Networking", networking ? "Enable" : "Disable"),
            new XElement("MappedFolders", mappings.Select(m => new XElement("MappedFolder",
                new XElement("HostFolder", Path.GetFullPath(m.HostFolder)),
                new XElement("SandboxFolder", m.SandboxFolder),
                new XElement("ReadOnly", m.ReadOnly ? "true" : "false")))),
            new XElement("ClipboardRedirection", "Disable"),
            new XElement("PrinterRedirection", "Disable"),
            new XElement("AudioInput", "Disable"),
            new XElement("VideoInput", "Disable"),
            new XElement("ProtectedClient", "Enable"),
            new XElement("MemoryInMB", memoryMb.ToString(CultureInfo.InvariantCulture)));
        if (logonCommand is not null)
            doc.Add(new XElement("LogonCommand", new XElement("Command", logonCommand)));

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb, CultureInfo.InvariantCulture)) doc.Save(writer, SaveOptions.None);
        return sb.ToString();
    }

    /// <summary>A typical untrusted run: inputs read-only, one writable output folder.</summary>
    public static string ForUntrustedRun(string inputFolder, string outputFolder, string? logonCommand = null) => Render(
        [
            new Mapping(inputFolder, SandboxUserDesktop + @"\in", ReadOnly: true),
            new Mapping(outputFolder, SandboxUserDesktop + @"\out", ReadOnly: false),
        ], logonCommand);
}
