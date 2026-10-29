using Microsoft.Win32;
using Cockpit.UI;

namespace Cockpit.Wpf;

/// <summary>
/// "Run on Windows startup": the per-user value HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AiCockpit.
/// Per-user only (no admin rights, no machine-wide change). The value is the quoted path of this executable.
/// </summary>
public sealed class StartupRegistration : IStartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "AiCockpit";

    public bool IsSupported => Environment.ProcessPath is not null;

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string v && v.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("The Run key could not be opened.");
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"", RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
