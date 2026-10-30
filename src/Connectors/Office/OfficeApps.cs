namespace Mediator.Connectors.Office;

/// <summary>
/// V15: where desktop Word, Excel, PowerPoint and Outlook are installed (App Paths registration, then the Click-to-Run
/// and MSI Office16 folders). Only used for the Connectors tab; the Office tools read and write files without Office.
/// </summary>
public static class OfficeApps
{
    public static readonly IReadOnlyList<string> Ids = ["word", "excel", "powerpoint", "outlook"];

    public static string DisplayName(string id) => id switch
    {
        "word" => "Microsoft Word",
        "excel" => "Microsoft Excel",
        "powerpoint" => "Microsoft PowerPoint",
        "outlook" => "Microsoft Outlook",
        _ => id,
    };

    public static string ExecutableName(string id) => id switch
    {
        "word" => "WINWORD.EXE",
        "excel" => "EXCEL.EXE",
        "powerpoint" => "POWERPNT.EXE",
        "outlook" => "OUTLOOK.EXE",
        _ => throw new ArgumentException($"Unknown Office app '{id}'."),
    };

    /// <summary>The app's .exe, or null when it is not installed.</summary>
    public static string? Locate(string id, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        string exe = ExecutableName(id);
        if (OperatingSystem.IsWindows())
        {
            string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe;
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try { if (hive.OpenSubKey(appPaths)?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"'); }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        foreach (var root in new[] { env("ProgramFiles"), env("ProgramFiles(x86)") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var sub in new[] { @"Microsoft Office\root\Office16", @"Microsoft Office\Office16" })
            {
                string p = Path.Combine(root, sub, exe);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>The new Outlook for Windows (olk.exe, a Store app), which has no automation. Null if not installed.</summary>
    public static string? LocateNewOutlook(Func<string, string?>? env = null)
    {
        string? local = (env ?? Environment.GetEnvironmentVariable)("LOCALAPPDATA");
        if (string.IsNullOrEmpty(local)) return null;
        string p = Path.Combine(local, "Microsoft", "WindowsApps", "olk.exe");
        return File.Exists(p) ? p : null;
    }
}
