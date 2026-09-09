using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SshDockerBackup.App.Services;
using SshDockerBackup.Core.Docker;
using SshDockerBackup.Core.Ssh;

namespace SshDockerBackup.App.ViewModels;

public sealed partial class ConnectionViewModel : ObservableObject
{
    private readonly ISshSession _ssh;
    private readonly IDockerService _docker;
    private readonly ISettingsStore _settingsStore;
    private readonly IDialogService _dialogs;
    private readonly AppState _state;
    private readonly ILogger<ConnectionViewModel> _log;
    private readonly AppSettings _settings;

    public ConnectionViewModel(
        ISshSession ssh,
        IDockerService docker,
        ISettingsStore settingsStore,
        IDialogService dialogs,
        AppState state,
        ILogger<ConnectionViewModel> log)
    {
        _ssh = ssh;
        _docker = docker;
        _settingsStore = settingsStore;
        _dialogs = dialogs;
        _state = state;
        _log = log;

        _settings = settingsStore.Load();

        Host = _settings.Connection.Host;
        Port = _settings.Connection.Port == 0 ? 22 : _settings.Connection.Port;
        Username = _settings.Connection.Username;
        UseSudo = _settings.Connection.UseSudo;
        PrivateKeyPath = _settings.Connection.PrivateKeyPath ?? "";
        KnownHostFingerprint = _settings.Connection.KnownHostFingerprint ?? "";
    }

    [ObservableProperty] public partial string Host { get; set; } = "";
    [ObservableProperty] public partial int Port { get; set; } = 22;
    [ObservableProperty] public partial string Username { get; set; } = "";
    [ObservableProperty] public partial string PrivateKeyPath { get; set; } = "";
    [ObservableProperty] public partial bool UseSudo { get; set; } = true;
    [ObservableProperty] public partial string KnownHostFingerprint { get; set; } = "";

    /// <summary>Set from the PasswordBox in code-behind; WPF cannot bind PasswordBox.Password.</summary>
    public string Password { get; set; } = "";

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusMessage { get; set; } = "Not connected.";
    [ObservableProperty] public partial bool HasError { get; set; }
    [ObservableProperty] public partial string HostSummary { get; set; } = "";

    public bool IsConnected => _state.IsConnected;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Username))
        {
            SetError("Host and username are required.");
            return;
        }

        if (string.IsNullOrEmpty(Password) && string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            SetError("Enter a password, or point at a private key file.");
            return;
        }

        IsBusy = true;
        HasError = false;
        StatusMessage = "Connecting...";

        try
        {
            var settings = _settings.Connection;
            settings.Host = Host.Trim();
            settings.Port = Port;
            settings.Username = Username.Trim();
            settings.UseSudo = UseSudo;
            settings.PrivateKeyPath = string.IsNullOrWhiteSpace(PrivateKeyPath) ? null : PrivateKeyPath.Trim();
            settings.KnownHostFingerprint = string.IsNullOrWhiteSpace(KnownHostFingerprint)
                ? null
                : KnownHostFingerprint.Trim();
            settings.Password = Password;

            await _ssh.ConnectAsync(settings, ApproveHostKeyAsync).ConfigureAwait(true);

            StatusMessage = "Authenticated. Checking docker...";
            var info = await _docker.ProbeAsync().ConfigureAwait(true);

            _state.HostInfo = info;
            _state.IsConnected = true;
            _state.ConnectionSummary = $"{settings.Username}@{settings.Host}:{settings.Port}";

            KnownHostFingerprint = settings.KnownHostFingerprint ?? "";
            HostSummary = $"{info.HostName}  ·  Docker {info.Version}  ·  root dir {info.RootDir}";
            StatusMessage = "Connected.";

            // Persist everything except the password, which stays in memory only.
            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Connection to {Host} failed", Host);
            _state.IsConnected = false;
            SetError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsConnected));
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        _ssh.Disconnect();
        _state.IsConnected = false;
        _state.HostInfo = null;
        _state.ConnectionSummary = "Not connected";
        _state.Containers.Clear();

        HostSummary = "";
        StatusMessage = "Disconnected.";
        HasError = false;
        OnPropertyChanged(nameof(IsConnected));
    }

    [RelayCommand]
    private void BrowseForKey()
    {
        var path = _dialogs.PickFile("Select a private key", "All files (*.*)|*.*");
        if (path is not null) PrivateKeyPath = path;
    }

    [RelayCommand]
    private void ClearFingerprint()
    {
        KnownHostFingerprint = "";
        _settings.Connection.KnownHostFingerprint = null;
        _settingsStore.Save(_settings);
        StatusMessage = "Saved host fingerprint cleared. The next connection will ask again.";
    }

    /// <summary>
    /// Trust on first use. Shown on the UI thread because SSH.NET raises this from its own thread
    /// while the connect call is still in flight.
    /// </summary>
    private Task<bool> ApproveHostKeyAsync(string fingerprint, string algorithm)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return Task.FromResult(true);

        var accepted = dispatcher.Invoke(() => _dialogs.Confirm(
            "Unknown host key",
            $"""
             {Host} presented a host key this app has not seen before. This is normal the first time you connect to a machine.

             Algorithm:   {algorithm}
             SHA256:      {fingerprint}

             If you can, compare that fingerprint with the one shown on the server itself. Once accepted it is remembered, and a later change will be refused rather than trusted silently.

             Accept and remember this key?
             """));

        _log.LogInformation("Host key {Fingerprint} ({Algorithm}) was {Decision}",
            fingerprint, algorithm, accepted ? "accepted" : "rejected");

        return Task.FromResult(accepted);
    }

    private void SetError(string message)
    {
        HasError = true;
        StatusMessage = message;
    }
}
