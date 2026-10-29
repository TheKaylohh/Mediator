using System.Diagnostics;
using Mediator.Core.Browser;

namespace Mediator.UI;

/// <summary>
/// V13: the Connectors card for Mediator for Chrome. "Set up" does everything Mediator can do for the one Chrome step
/// the user must do themselves (Load unpacked): refreshes the extension folder and pairing, copies the folder path,
/// and opens chrome://extensions. The dot turns green when the extension pairs.
/// </summary>
public sealed class BrowserExtensionViewModel : ObservableObject
{
    private readonly BrowserExtensionHost? _host;
    private readonly IUiDispatcher _ui;
    private readonly IProcessLauncher _launcher;
    private readonly Action<string>? _copyText;
    private string _detail = "";
    private bool _isConnected;
    private bool _showSteps;

    public BrowserExtensionViewModel(BrowserExtensionHost? host, IUiDispatcher ui, IProcessLauncher launcher, Action<string>? copyText)
    {
        _host = host;
        _ui = ui;
        _launcher = launcher;
        _copyText = copyText;
        SetUpCommand = new RelayCommand(SetUp, () => _host is not null);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _host is not null);
        if (_host is not null) _host.Bridge.ConnectionChanged += _ => _ui.Post(Update);
        Update();
    }

    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public bool IsConnected { get => _isConnected; private set { if (SetProperty(ref _isConnected, value)) OnPropertyChanged(nameof(SetUpText)); } }
    public string SetUpText => IsConnected ? "Set up again" : "Set up";
    public RelayCommand SetUpCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>Re-reads the connection state (also called by the tab's Refresh).</summary>
    public void Update()
    {
        if (_host is null)
        {
            IsConnected = false;
            Detail = "Not available: the extension bridge could not start (see the log). Browser jobs use Claude in Chrome instead.";
            return;
        }
        IsConnected = _host.Bridge.IsConnected;
        if (IsConnected) { _showSteps = false; Detail = $"Connected (v{_host.Bridge.ExtensionVersion}). Try: /chrome open news.ycombinator.com and summarize the top 5 posts."; }
        else if (_showSteps) Detail = BrowserExtension.SetupSteps(_host.InstallDirectory);
        else if (_host.EverConnected) Detail = "Installed, not connected. It connects a few seconds after Chrome opens (Mediator opens Chrome for browser jobs).";
        else Detail = "Not set up. Lets Claude or Gemini read and use your Chrome tabs (\"/chrome …\"). Press Set up: one step in Chrome, once.";
    }

    private void SetUp()
    {
        if (_host is null) return;
        if (_host.Prepare() is { } problem) { Detail = "Could not prepare the extension: " + problem; return; }
        _showSteps = true;
        try { _copyText?.Invoke(_host.InstallDirectory); } catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.ExternalException) { }
        if (BrowserExtension.OpenExtensionsPage() is { } psi)
        {
            try { _launcher.Start(psi); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Detail = "Could not open Chrome: " + ex.Message; return; }
        }
        Update();
        if (!IsConnected && BrowserExtension.OpenExtensionsPage() is null) Detail = "Google Chrome was not found. Install it, then press Set up. " + Detail;
    }

    private void OpenFolder()
    {
        if (_host is null) return;
        Directory.CreateDirectory(_host.InstallDirectory);
        try { _launcher.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _host.InstallDirectory }, UseShellExecute = false }); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { Detail = "Could not open the folder: " + ex.Message; }
    }
}
