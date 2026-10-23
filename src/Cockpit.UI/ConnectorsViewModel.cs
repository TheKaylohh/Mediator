using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Cockpit.Core.Connectors;
using Cockpit.Core.Jobs;
using Cockpit.Core.Runtime;

namespace Cockpit.UI;

/// <summary>Starts processes for the view (sign-in terminals). Tests record instead of starting.</summary>
public interface IProcessLauncher
{
    void Start(ProcessStartInfo psi);
}

public sealed class DefaultProcessLauncher : IProcessLauncher
{
    public static readonly DefaultProcessLauncher Instance = new();
    public void Start(ProcessStartInfo psi) { using var p = Process.Start(psi); }
}

/// <summary>A CLI row in the Connectors tab: version, sign-in state, usage-limit cooldown, [ Sign In ].</summary>
public sealed class CliConnectorViewModel : ObservableObject
{
    private string _version = "checking…";
    private string _auth = "";
    private string _cooldown = "";
    private bool _isInstalled;
    private bool _isSignedInLikely;
    private bool _needsAttention;

    public CliConnectorViewModel(string id, string name, string subscription, Action<CliConnectorViewModel> signIn)
    {
        Id = id;
        Name = name;
        Subscription = subscription;
        SignInCommand = new RelayCommand(() => signIn(this), () => IsInstalled);
    }

    public string Id { get; }
    public string Name { get; }
    public string Subscription { get; }
    public string VersionText { get => _version; internal set => SetProperty(ref _version, value); }
    /// <summary>"credential file found (not verified)", "not signed in", "sign-in failed on the last run".</summary>
    public string AuthText { get => _auth; internal set => SetProperty(ref _auth, value); }
    public string CooldownText { get => _cooldown; internal set { if (SetProperty(ref _cooldown, value)) OnPropertyChanged(nameof(HasCooldown)); } }
    public bool HasCooldown => CooldownText.Length > 0;
    public bool IsInstalled { get => _isInstalled; internal set { if (SetProperty(ref _isInstalled, value)) SignInCommand.Refresh(); } }
    /// <summary>A credential file exists and no run failed for lack of sign-in. Not proof: only a run proves it.</summary>
    public bool IsSignedInLikely { get => _isSignedInLikely; internal set => SetProperty(ref _isSignedInLikely, value); }
    /// <summary>Missing, not signed in or at its limit: shown amber.</summary>
    public bool NeedsAttention { get => _needsAttention; internal set => SetProperty(ref _needsAttention, value); }
    public RelayCommand SignInCommand { get; }
}

/// <summary>A desktop app row: SolidWorks, DaVinci Resolve, Blender.</summary>
public sealed class AppConnectorViewModel(string id, string name) : ObservableObject
{
    private string _detail = "checking…";
    private bool _isAvailable;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Detail { get => _detail; internal set => SetProperty(ref _detail, value); }
    public bool IsAvailable { get => _isAvailable; internal set => SetProperty(ref _isAvailable, value); }
}

/// <summary>A custom MCP server row.</summary>
public sealed class McpServerViewModel : ObservableObject
{
    private bool _isEnabled;
    private readonly Action<McpServerViewModel, bool> _setEnabled;

    internal McpServerViewModel(CustomMcpServer server, Action<McpServerViewModel, bool> setEnabled, Action<McpServerViewModel> remove, Func<McpServerViewModel, Task> ping)
    {
        Name = server.Name;
        _setEnabled = setEnabled;
        Update(server);
        RemoveCommand = new RelayCommand(() => remove(this));
        PingCommand = new RelayCommand(() => _ = ping(this));
    }

    public string Name { get; }
    public string CommandLine { get; private set; } = "";
    public string HealthText { get; private set; } = "";
    public bool IsHealthy { get; private set; }
    public string ToolsText { get; private set; } = "";

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) _setEnabled(this, value); }
    }

    public RelayCommand RemoveCommand { get; }
    public RelayCommand PingCommand { get; }

    internal void Update(CustomMcpServer server)
    {
        CommandLine = string.Join(' ', new[] { server.Command }.Concat(server.Args));
        IsHealthy = server.LastPing is { Ok: true };
        HealthText = server.LastPing switch
        {
            null => "never checked",
            { Ok: true } p => $"healthy · {p.Tools.Count} tool(s) · {p.ElapsedMs} ms · {p.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}",
            { } p => "failed: " + p.Error,
        };
        ToolsText = server.ToolNames.Count == 0 ? "" : string.Join(", ", server.ToolNames.Select(t => CustomMcpService.ExposedName(server.Name, t)));
        _isEnabled = server.Enabled;
        OnPropertyChanged(nameof(CommandLine));
        OnPropertyChanged(nameof(HealthText));
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(ToolsText));
        OnPropertyChanged(nameof(IsEnabled));
    }
}

/// <summary>
/// The Connectors tab (V7): the two subscription CLIs, the desktop apps the Cockpit drives, and custom MCP servers.
/// Sign-in state is reported honestly: the Cockpit checks that the CLI's credential file exists (it never opens it)
/// and whether a run in this session failed for lack of sign-in; only a real run proves a sign-in works.
/// </summary>
public sealed class ConnectorsViewModel : ObservableObject
{
    private readonly IConnectorProbe _probe;
    private readonly ICockpitRuntime _runtime;
    private readonly CustomMcpService? _mcp;
    private readonly IUiDispatcher _ui;
    private readonly IProcessLauncher _launcher;
    private string _newName = "";
    private string _newCommand = "";
    private string _newArgs = "";
    private string _addStatus = "";
    private bool _isBusy;
    private bool _isAddOpen;
    private string _status = "";

    public ConnectorsViewModel(IConnectorProbe probe, ICockpitRuntime runtime, CustomMcpService? mcp, IUiDispatcher ui, IProcessLauncher? launcher = null)
    {
        _probe = probe;
        _runtime = runtime;
        _mcp = mcp;
        _ui = ui;
        _launcher = launcher ?? DefaultProcessLauncher.Instance;
        Clis.Add(new CliConnectorViewModel("claude", "Claude Code CLI", "Claude Pro", SignIn));
        Clis.Add(new CliConnectorViewModel("gemini", "Gemini CLI", "Google AI Pro", SignIn));
        Apps.Add(new AppConnectorViewModel("solidworks", "SolidWorks"));
        Apps.Add(new AppConnectorViewModel("resolve", "DaVinci Resolve"));
        Apps.Add(new AppConnectorViewModel("blender", "Blender"));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsBusy);
        ToggleAddCommand = new RelayCommand(() => IsAddOpen = !IsAddOpen, () => _mcp is not null);
        AddServerCommand = new RelayCommand(() => _ = AddServerAsync(), () => !IsBusy && NewName.Trim().Length > 0 && NewCommand.Trim().Length > 0);
        if (_mcp is not null)
        {
            _mcp.Changed += () => _ui.Post(SyncServers);
            SyncServers();
            if (_mcp.LoadError is { } err) Status = $"{_mcp.FilePath} could not be read ({err}); it was left as it is.";
        }
    }

    public ObservableCollection<CliConnectorViewModel> Clis { get; } = new();
    public ObservableCollection<AppConnectorViewModel> Apps { get; } = new();
    public ObservableCollection<McpServerViewModel> McpServers { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ToggleAddCommand { get; }
    public RelayCommand AddServerCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) { RefreshCommand.Refresh(); AddServerCommand.Refresh(); } }
    }
    public bool IsAddOpen { get => _isAddOpen; set => SetProperty(ref _isAddOpen, value); }
    public string NewName { get => _newName; set { if (SetProperty(ref _newName, value ?? "")) AddServerCommand.Refresh(); } }
    public string NewCommand { get => _newCommand; set { if (SetProperty(ref _newCommand, value ?? "")) AddServerCommand.Refresh(); } }
    /// <summary>Arguments separated by spaces; quote an argument that contains spaces.</summary>
    public string NewArgs { get => _newArgs; set => SetProperty(ref _newArgs, value ?? ""); }
    public string AddStatus { get => _addStatus; private set => SetProperty(ref _addStatus, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool HasMcpServers => McpServers.Count > 0;

    /// <summary>Re-probes everything. Called when the tab opens and by its refresh button.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            foreach (var cli in Clis)
            {
                var tool = await _probe.ProbeToolAsync(cli.Id, ct).ConfigureAwait(true);
                var cred = _probe.Credentials(cli.Id);
                string provider = cli.Id == "claude" ? CockpitOverlayViewModel.ClaudeProvider : CockpitOverlayViewModel.GeminiProvider;
                var lastAuthFailure = _runtime.Jobs.Where(j => j.Provider is { } p && ProviderRouter.Id(p) == provider && j.Outcome is not null)
                    .OrderByDescending(j => j.EndedAt ?? j.StartedAt).FirstOrDefault()?.Outcome == RunOutcome.AuthFailed;
                if (lastAuthFailure) cred = CredentialState.LastRunFailed;
                DateTimeOffset? until = await _runtime.GetCooldownAsync(provider).ConfigureAwait(true);
                _ui.Post(() =>
                {
                    cli.IsInstalled = tool.Found;
                    cli.VersionText = tool.Found ? $"v{tool.Version}" : tool.Problem ?? "not found";
                    cli.AuthText = !tool.Found ? ConnectorProbe.InstallHint(cli.Id) : cred switch
                    {
                        CredentialState.FilePresent => "signed in (credential file found; confirmed by the next run)",
                        CredentialState.LastRunFailed => "not signed in: the last run was refused. Click Sign In.",
                        _ => "not signed in. Click Sign In.",
                    };
                    cli.IsSignedInLikely = tool.Found && cred == CredentialState.FilePresent;
                    cli.CooldownText = until is { } u ? "usage limit until " + u.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture) : "";
                    cli.NeedsAttention = !cli.IsSignedInLikely || until is not null;
                });
            }
            foreach (var (app, status) in Apps.Zip(new[] { _probe.SolidWorks(), _probe.Resolve(), _probe.Blender() }))
                _ui.Post(() => { app.Detail = status.Detail; app.IsAvailable = status.Available; });
            _ui.Post(() => Status = "Checked " + DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    private void SignIn(CliConnectorViewModel cli)
    {
        var (psi, problem) = _probe.SignIn(cli.Id);
        if (psi is null) { Status = problem ?? "Sign-in is not available here."; return; }
        try
        {
            _launcher.Start(psi);
            Status = cli.Id == "claude"
                ? "A terminal opened with Claude Code: type /login if it does not ask, sign in with your Claude account, then close it and press Refresh."
                : "A terminal opened with Gemini CLI: choose Login with Google, finish in the browser, then close it and press Refresh.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Status = "Could not open a terminal: " + ex.Message;
        }
    }

    /// <summary>Splits the arguments box: spaces separate, double quotes group. Pure.</summary>
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) { result.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) result.Add(current.ToString());
        return result;
    }

    /// <summary>The [ + Add Custom MCP ] form: validates, health-pings (15 s), registers, saves cockpit.mcp.json.</summary>
    public async Task<bool> AddServerAsync()
    {
        if (_mcp is null) return false;
        IsBusy = true;
        AddStatus = "Starting the server and asking for its tools…";
        try
        {
            var server = await _mcp.AddAsync(NewName, NewCommand, SplitArguments(NewArgs)).ConfigureAwait(true);
            _ui.Post(() =>
            {
                AddStatus = server.LastPing is { Ok: true } p
                    ? $"Added '{server.Name}': {p.Tools.Count} tool(s). Every call will ask for your approval."
                    : $"Saved '{server.Name}' but disabled: {server.LastPing?.Error}";
                if (server.LastPing is { Ok: true }) { NewName = NewCommand = NewArgs = ""; IsAddOpen = false; }
            });
            return server.LastPing is { Ok: true };
        }
        catch (CustomMcpException ex)
        {
            _ui.Post(() => AddStatus = ex.Message);
            return false;
        }
        finally
        {
            _ui.Post(() => IsBusy = false);
        }
    }

    private void SyncServers()
    {
        if (_mcp is null) return;
        var servers = _mcp.Servers;
        foreach (var gone in McpServers.Where(v => servers.All(s => s.Name != v.Name)).ToList()) McpServers.Remove(gone);
        foreach (var s in servers)
        {
            if (McpServers.FirstOrDefault(v => v.Name == s.Name) is { } row) row.Update(s);
            else McpServers.Add(new McpServerViewModel(s, SetEnabled, Remove, PingAsync));
        }
        OnPropertyChanged(nameof(HasMcpServers));
    }

    private void SetEnabled(McpServerViewModel row, bool enabled)
    {
        try { _mcp?.SetEnabled(row.Name, enabled); }
        catch (CustomMcpException ex) { Status = ex.Message; }
        if (_mcp?.Servers.FirstOrDefault(s => s.Name == row.Name) is { } s) row.Update(s);
        if (enabled && !row.IsHealthy) Status = $"'{row.Name}' stays off until a health check passes: press Check.";
    }

    private void Remove(McpServerViewModel row)
    {
        try { _mcp?.Remove(row.Name); Status = $"Removed '{row.Name}'. Running jobs can no longer call its tools."; }
        catch (CustomMcpException ex) { Status = ex.Message; }
    }

    private async Task PingAsync(McpServerViewModel row)
    {
        if (_mcp is null) return;
        Status = $"Checking '{row.Name}'…";
        try
        {
            var s = await _mcp.RefreshAsync(row.Name).ConfigureAwait(true);
            _ui.Post(() => { row.Update(s); Status = $"'{row.Name}': {row.HealthText}"; });
        }
        catch (CustomMcpException ex) { _ui.Post(() => Status = ex.Message); }
    }
}
