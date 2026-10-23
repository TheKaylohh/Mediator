using System.Collections.ObjectModel;
using Cockpit.Core.Connectors;

namespace Cockpit.UI;

/// <summary>"Run on Windows startup": the HKCU Run value. The WPF host implements it with the registry; tests use a fake.</summary>
public interface IStartupRegistration
{
    bool IsSupported { get; }
    bool IsEnabled { get; }
    /// <summary>Throws on failure (the card shows the message).</summary>
    void SetEnabled(bool enabled);
}

public sealed class UnsupportedStartupRegistration : IStartupRegistration
{
    public bool IsSupported => false;
    public bool IsEnabled => false;
    public void SetEnabled(bool enabled) => throw new PlatformNotSupportedException("Start-up registration is Windows only.");
}

/// <summary>One line of the prerequisite checklist.</summary>
public sealed class PrerequisiteViewModel(string id, string name, bool required) : ObservableObject
{
    private string _status = "checking…";
    private bool? _isOk;
    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>At least one of the two CLIs is required; Python and Git are recommended.</summary>
    public bool IsRequired { get; } = required;
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    /// <summary>null while checking.</summary>
    public bool? IsOk { get => _isOk; internal set { if (SetProperty(ref _isOk, value)) OnPropertyChanged(nameof(IsMissing)); } }
    public bool IsMissing => IsOk == false;
    public string Hint => ConnectorProbe.InstallHint(Id);
}

/// <summary>A theme button in the first-run card.</summary>
public sealed class ThemeOptionViewModel(CockpitTheme theme) : ObservableObject
{
    private bool _isSelected;
    public CockpitTheme Theme { get; } = theme;
    public string Name { get; } = CockpitOverlayViewModel.ThemeName(theme);
    public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }
}

/// <summary>
/// The first-run setup card (V7). Shown on the first launch, and on any launch where neither Claude Code nor Gemini
/// CLI is found. Checks claude, gemini, python and git; offers Run on startup, the theme and the hotkey; saves them.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    private readonly IConnectorProbe _probe;
    private readonly IStartupRegistration _startup;
    private readonly CockpitOverlayViewModel _overlay;
    private bool _isOpen;
    private bool _runOnStartup;
    private string _hotkeyText;
    private string _hotkeyError = "";
    private CockpitTheme _theme;
    private bool _isChecking;
    private string _summary = "";

    public OnboardingViewModel(IConnectorProbe probe, IStartupRegistration startup, CockpitOverlayViewModel overlay)
    {
        _probe = probe;
        _startup = startup;
        _overlay = overlay;
        var s = overlay.Settings;
        _hotkeyText = s.Hotkey;
        _theme = s.Theme;
        _runOnStartup = startup.IsSupported && startup.IsEnabled;
        foreach (var id in ConnectorProbe.Prerequisites)
            Prerequisites.Add(new PrerequisiteViewModel(id, ConnectorProbe.DisplayName(id), required: id is "claude" or "gemini"));
        foreach (var t in CockpitOverlayViewModel.Themes) ThemeOptions.Add(new ThemeOptionViewModel(t) { IsSelected = t == _theme });
        SelectThemeCommand = new RelayCommand(p => { if (p is ThemeOptionViewModel o) Theme = o.Theme; });
        UsePresetCommand = new RelayCommand(p => { if (p is string g) HotkeyText = g; });
        FinishCommand = new RelayCommand(() => Finish(), () => HotkeyError.Length == 0);
        RecheckCommand = new RelayCommand(() => _ = CheckAsync(), () => !IsChecking);
        SkipCommand = new RelayCommand(() => IsOpen = false);
    }

    public ObservableCollection<PrerequisiteViewModel> Prerequisites { get; } = new();
    public ObservableCollection<ThemeOptionViewModel> ThemeOptions { get; } = new();
    public RelayCommand SelectThemeCommand { get; }
    public RelayCommand UsePresetCommand { get; }
    public IReadOnlyList<CockpitTheme> Themes => CockpitOverlayViewModel.Themes;
    public IReadOnlyList<string> HotkeyPresets => HotkeyGesture.Presets;
    public bool IsStartupSupported => _startup.IsSupported;

    public bool IsOpen { get => _isOpen; set => SetProperty(ref _isOpen, value); }
    public bool IsChecking { get => _isChecking; private set { if (SetProperty(ref _isChecking, value)) RecheckCommand.Refresh(); } }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    /// <summary>Applied immediately (preview) and saved on Finish.</summary>
    public CockpitTheme Theme
    {
        get => _theme;
        set
        {
            if (!SetProperty(ref _theme, value)) return;
            foreach (var o in ThemeOptions) o.IsSelected = o.Theme == value;
            _overlay.CurrentTheme = value;
        }
    }

    public string HotkeyText
    {
        get => _hotkeyText;
        set
        {
            if (!SetProperty(ref _hotkeyText, value ?? "")) return;
            HotkeyError = HotkeyGesture.TryParse(_hotkeyText, out _, out var error) ? "" : error ?? "invalid";
        }
    }

    public string HotkeyError
    {
        get => _hotkeyError;
        private set { if (SetProperty(ref _hotkeyError, value)) FinishCommand.Refresh(); }
    }

    public bool RunOnStartup { get => _runOnStartup; set => SetProperty(ref _runOnStartup, value); }

    public RelayCommand FinishCommand { get; }
    public RelayCommand RecheckCommand { get; }
    public RelayCommand SkipCommand { get; }

    /// <summary>The view re-registers the global hotkey when it changes.</summary>
    public event Action<HotkeyGesture>? HotkeyChanged;

    public bool AnyCliFound => Prerequisites.Any(p => p.IsRequired && p.IsOk == true);

    /// <summary>Checks the prerequisites, then opens the card if this is the first run or no CLI was found. Returns whether it opened.</summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        await CheckAsync(ct).ConfigureAwait(true);
        IsOpen = !_overlay.Settings.FirstRunCompleted || !AnyCliFound;
        return IsOpen;
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        IsChecking = true;
        try
        {
            foreach (var p in Prerequisites)
            {
                var r = await _probe.ProbeToolAsync(p.Id, ct).ConfigureAwait(true);
                p.IsOk = r.Found;
                p.Status = r.Found ? $"v{r.Version}" : r.Problem ?? "not found";
                if (r.Found && p.Id is "claude" or "gemini")
                    p.Status += _probe.Credentials(p.Id) == CredentialState.FilePresent ? " · signed in" : " · not signed in yet (Connectors tab → Sign In)";
            }
            Summary = AnyCliFound
                ? "Ready. Jobs use your Claude Pro or Google AI Pro subscription through these CLIs."
                : "Install Claude Code or Gemini CLI (or both) to run jobs; the overlay works meanwhile.";
            OnPropertyChanged(nameof(AnyCliFound));
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Saves theme, hotkey and first-run; applies the startup choice. Returns false (card stays open) on a problem.</summary>
    public bool Finish()
    {
        if (!HotkeyGesture.TryParse(HotkeyText, out var gesture, out var error)) { HotkeyError = error ?? "invalid"; return false; }
        if (_startup.IsSupported && RunOnStartup != _startup.IsEnabled)
        {
            try { _startup.SetEnabled(RunOnStartup); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException)
            {
                Summary = "Run on startup could not be changed: " + ex.Message;
                return false;
            }
        }
        bool hotkeyChanged = gesture.ToString() != _overlay.Settings.Hotkey;
        _overlay.UpdateSettings(_overlay.Settings with
        {
            Theme = Theme,
            Hotkey = gesture.ToString(),
            FirstRunCompleted = true,
            RunOnStartup = _startup.IsSupported && RunOnStartup,
        });
        if (hotkeyChanged) HotkeyChanged?.Invoke(gesture);
        IsOpen = false;
        return true;
    }
}
