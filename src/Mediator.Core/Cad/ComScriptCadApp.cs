namespace Mediator.Core.Cad;

/// <summary>
/// A connector whose script runs in Windows PowerShell 5.1 and talks to a running desktop program through COM
/// (GetActiveObject, or a new instance when none runs). Available when the program is installed or its COM class is registered.
/// </summary>
public abstract class ComScriptCadApp(CadProgramSpec spec, CadLocator? locator, string? overrideFolder) : ScriptCadApp(overrideFolder)
{
    protected CadLocator Locator { get; } = locator ?? CadLocator.Default;
    public CadProgramSpec Spec { get; } = spec;
    public override string DisplayName => Spec.Name;

    protected override CadScriptRunner? Runner(out string? why)
    {
        why = null;
        if (!OperatingSystem.IsWindows()) { why = $"{Spec.Name} automation needs Windows."; return null; }
        if (Locator.Find(Spec) is null && !Locator.ComRegistered(Spec))
        {
            why = $"{Spec.Name} is not installed on this PC (or set {Spec.OverrideVariable} to its executable).";
            return null;
        }
        if (Locator.WindowsPowerShell() is not { } ps) { why = "Windows PowerShell 5.1 (powershell.exe) was not found."; return null; }
        return new CadScriptRunner(ps, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"], [], ".ps1", Utf8Bom: true);
    }

    public override CadScriptInfo Script => new("Windows PowerShell (COM)",
        $"The script runs in Windows PowerShell 5.1. In scope: $app (the running {Spec.Name}, {Spec.ProgId} COM object), $doc (the document at path, " +
        "opened if needed, else the active one), $MediatorArgs (the call's arguments), Write-Result @{ ... } (the hashtable you return; otherwise " +
        $"the script's output is returned). Use the {Spec.Name} COM API exactly as from VBA. Throw to report an error.");
}
