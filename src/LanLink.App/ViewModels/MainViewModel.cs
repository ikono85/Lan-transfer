using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanLink.App.Services;
using LanLink.Core;
using LanLink.Core.Discovery;
using LanLink.Core.Net;
using LanLink.Core.Remote;
using LanLink.App.Views;
using LanLink.Core.Security;
using LanLink.Core.Sync;
using LanLink.Core.Transfer;

namespace LanLink.App.ViewModels;

public sealed record PeerItem(string Address, int Port, string Name)
{
    public string Display => $"{Name}  —  {Address}";
}

public sealed class HistoryItem
{
    public TransferRecord Record { get; }

    public HistoryItem(TransferRecord record) => Record = record;

    public string Arrow => Record.Direction == TransferDirection.Sent ? "⬆" : "⬇";
    public string Name => Record.Name;
    public string Detail => $"{(Record.Direction == TransferDirection.Sent ? "vers" : "de")} {Record.PeerName} ({Record.PeerAddress}) · {FormatSize(Record.Size)}";
    public string Time => Record.TimeUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
    public string Status => Record.Status switch
    {
        TransferStatus.Completed => "✔ Terminé",
        TransferStatus.Cancelled => "⏹ Annulé",
        TransferStatus.Interrupted => "⚠ Interrompu (reprise possible)",
        TransferStatus.Rejected => "✖ Refusé",
        _ => "✖ Échec" + (string.IsNullOrEmpty(Record.Error) ? "" : " : " + Record.Error),
    };

    public static string FormatSize(long bytes)
    {
        string[] units = { "o", "Ko", "Mo", "Go", "To" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} o" : $"{value:0.#} {units[unit]}";
    }
}

public sealed partial class SyncPairItem : ObservableObject
{
    public SyncPair Pair { get; }

    public SyncPairItem(SyncPair pair)
    {
        Pair = pair;
        Refresh();
    }

    public string Name => Pair.Name;
    public string LocalPath => Pair.LocalPath;

    public string Peer => Pair.IsInitiator
        ? $"↔ {Pair.PeerName} ({Pair.PeerAddress})"
        : $"↔ {Pair.PeerName} — piloté par cet autre PC";

    [ObservableProperty] private string _icon = "⏳";
    [ObservableProperty] private string _status = "";

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(LocalPath));
        OnPropertyChanged(nameof(Peer));
        if (Status.Length == 0)
        {
            Icon = Pair.Paused ? "⏸" : "⏳";
            Status = Pair.Paused ? "En pause."
                : !Pair.IsInitiator ? "En attente de l'autre PC."
                : Pair.LastError is { } error ? "✖ " + error
                : Pair.LastSyncUtc is { } last ? $"Dernière synchronisation : {last.ToLocalTime():dd/MM/yyyy HH:mm}"
                : "En attente…";
        }
    }

    public void Apply(SyncStatus status)
    {
        Icon = status.State switch
        {
            SyncState.Syncing => "🔄",
            SyncState.Error => "⚠",
            SyncState.Paused => "⏸",
            _ => "✔",
        };
        var when = status.LastSyncUtc is { } last ? $" · {last.ToLocalTime():HH:mm}" : "";
        Status = status.State == SyncState.Syncing ? status.Message : status.Message + when;
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _sendCts;

    public MainViewModel(AppServices services, Dispatcher dispatcher)
    {
        _services = services;
        _dispatcher = dispatcher;

        DeviceName = services.Settings.DeviceName;
        DownloadDirectory = services.Settings.DownloadDirectory;
        Theme = services.Settings.Theme;
        AllowScreenSharing = services.Settings.AllowScreenSharing;
        Endpoint = $"{Environment.MachineName} · port {services.Settings.Port}";
        FingerprintShort = services.Identity.Identity.FingerprintHex[..16];
        HasPassword = services.Identity.Verifier is not null;
        StatusText = services.NetworkError ?? "Prêt.";

        foreach (var record in services.History.Load()) History.Add(new HistoryItem(record));

        services.Discovery.PeersChanged += () => _dispatcher.BeginInvoke(RefreshPeers);
        services.Host.Progress += p => _dispatcher.BeginInvoke(() => OnProgress(p));
        services.Host.Finished += r => _dispatcher.BeginInvoke(() => AddHistory(r));
        services.Host.ChatReceived += (peer, text) => _dispatcher.BeginInvoke(() => ChatLines.Add($"{peer.DeviceName} : {text}"));
        services.Host.Logged += m => _dispatcher.BeginInvoke(() => StatusText = m);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => RefreshPeers();
        timer.Start();
        RefreshPeers();

        services.SyncPairs.Changed += () => _dispatcher.BeginInvoke(RefreshSyncList);
        services.Sync.StatusChanged += st => _dispatcher.BeginInvoke(() => OnSyncStatus(st));
        RefreshSyncList();
    }

    // ---- État général
    public ObservableCollection<PeerItem> Peers { get; } = new();
    public ObservableCollection<string> PendingPaths { get; } = new();
    public ObservableCollection<string> ChatLines { get; } = new();
    public ObservableCollection<HistoryItem> History { get; } = new();

    public string Endpoint { get; }
    public string FingerprintShort { get; }
    public bool ShowPasswordWarning => !HasPassword;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _selectedTab;

    // ---- Destinataire
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SendChatCommand), nameof(RemoteViewCommand), nameof(RemoteControlCommand), nameof(CreateSyncCommand))]
    private string _targetAddress = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SendChatCommand), nameof(RemoteViewCommand), nameof(RemoteControlCommand), nameof(CreateSyncCommand))]
    private string _targetPassword = "";

    [ObservableProperty] private PeerItem? _selectedPeer;

    partial void OnSelectedPeerChanged(PeerItem? value)
    {
        if (value is null) return;
        TargetAddress = value.Port == _services.Settings.Port ? value.Address : $"{value.Address}:{value.Port}";
    }

    private void RefreshPeers()
    {
        var current = _services.Discovery.Snapshot();
        if (current.Select(p => (p.Address, p.DeviceName)).SequenceEqual(Peers.Select(p => (p.Address, p.Name)))) return;
        var selected = SelectedPeer?.Address;
        Peers.Clear();
        foreach (var p in current) Peers.Add(new PeerItem(p.Address, p.Port, p.DeviceName));
        if (selected is not null) SelectedPeer = Peers.FirstOrDefault(p => p.Address == selected);
    }

    private (string Host, int Port) ParseTarget()
    {
        var text = TargetAddress.Trim();
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out var port) && port is > 0 and < 65536)
            return (text[..colon], port);
        return (text, _services.Settings.Port);
    }

    private bool HasTarget => !string.IsNullOrWhiteSpace(TargetAddress) && !string.IsNullOrEmpty(TargetPassword);

    // ---- Envoi de fichiers
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if ((File.Exists(path) || Directory.Exists(path)) && !PendingPaths.Contains(path)) PendingPaths.Add(path);
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void PickFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Fichiers à envoyer" };
        if (dialog.ShowDialog() == true) AddPaths(dialog.FileNames);
    }

    [RelayCommand]
    private void PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier à envoyer" };
        if (dialog.ShowDialog() == true) AddPaths(new[] { dialog.FolderName });
    }

    [RelayCommand]
    private void ClearPending()
    {
        PendingPaths.Clear();
        SendCommand.NotifyCanExecuteChanged();
    }

    private bool CanSend() => !IsBusy && HasTarget && PendingPaths.Count > 0;

    partial void OnIsBusyChanged(bool value)
    {
        SendCommand.NotifyCanExecuteChanged();
        SendChatCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RemoteViewCommand.NotifyCanExecuteChanged();
        RemoteControlCommand.NotifyCanExecuteChanged();
        CreateSyncCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var (host, port) = ParseTarget();
        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;
        IsBusy = true;
        ProgressValue = 0;
        StatusText = $"Connexion à {host}…";
        try
        {
            await using var session = await LinkClient.ConnectAsync(host, port, TargetPassword,
                _services.Identity.Identity, _services.Settings.DeviceName, ct: ct);
            StatusText = $"Connecté à {session.Server.DeviceName}.";

            foreach (var path in PendingPaths.ToList())
            {
                long total = 0;
                var name = Path.GetFileName(path.TrimEnd('\\', '/'));
                var progress = new Progress<TransferProgress>(p =>
                {
                    total = p.TotalBytes;
                    name = p.Name;
                    ProgressValue = p.TotalBytes == 0 ? 100 : 100.0 * p.BytesDone / p.TotalBytes;
                    StatusText = $"Envoi de « {p.Name} » — {HistoryItem.FormatSize(p.BytesDone)} / {HistoryItem.FormatSize(p.TotalBytes)}";
                });

                TransferStatus status;
                string? error = null;
                try
                {
                    await session.SendPathAsync(path, progress, ct);
                    status = TransferStatus.Completed;
                    PendingPaths.Remove(path);
                }
                catch (TransferRejectedException ex)
                {
                    status = TransferStatus.Rejected;
                    error = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    RecordSent(name, total, session.Server, host, TransferStatus.Cancelled, null, path);
                    StatusText = "Envoi annulé (il pourra reprendre).";
                    return;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or EndOfStreamException)
                {
                    RecordSent(name, total, session.Server, host, TransferStatus.Interrupted, ex.Message, path);
                    StatusText = "Transfert interrompu : " + ex.Message;
                    return;
                }

                RecordSent(name, total, session.Server, host, status, error, path);
                if (status == TransferStatus.Rejected) StatusText = error ?? "Refusé.";
            }

            ProgressValue = 0;
            if (PendingPaths.Count == 0) StatusText = "Tous les éléments ont été envoyés.";
        }
        catch (AuthenticationFailedException ex)
        {
            StatusText = "✖ " + ex.Message;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException
                                       or System.Security.Authentication.AuthenticationException)
        {
            StatusText = $"✖ Connexion impossible à {host}:{port} : {ex.Message}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Connexion annulée.";
        }
        finally
        {
            IsBusy = false;
            _sendCts.Dispose();
            _sendCts = null;
        }
    }

    private void RecordSent(string name, long size, RemotePeer peer, string address, TransferStatus status, string? error, string path)
    {
        var record = new TransferRecord(Guid.NewGuid(), DateTime.UtcNow, TransferDirection.Sent, name, size,
            peer.DeviceName, address, status, error, path);
        _services.History.Add(record);
        AddHistory(record);
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _sendCts?.Cancel();

    private void OnProgress(TransferProgress p)
    {
        if (p.Direction != TransferDirection.Received) return;
        ProgressValue = p.TotalBytes == 0 ? 100 : 100.0 * p.BytesDone / p.TotalBytes;
        StatusText = p.BytesDone >= p.TotalBytes
            ? $"Réception de « {p.Name} » terminée."
            : $"Réception de « {p.Name} » — {HistoryItem.FormatSize(p.BytesDone)} / {HistoryItem.FormatSize(p.TotalBytes)}";
    }

    private void AddHistory(TransferRecord record)
    {
        History.Insert(0, new HistoryItem(record));
        if (record.Direction == TransferDirection.Received) ProgressValue = 0;
    }

    // ---- Chat
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SendChatCommand))]
    private string _chatInput = "";

    private bool CanSendChat() => !IsBusy && HasTarget && !string.IsNullOrWhiteSpace(ChatInput);

    [RelayCommand(CanExecute = nameof(CanSendChat))]
    private async Task SendChatAsync()
    {
        var text = ChatInput.Trim();
        var (host, port) = ParseTarget();
        IsBusy = true;
        try
        {
            await using var session = await LinkClient.ConnectAsync(host, port, TargetPassword,
                _services.Identity.Identity, _services.Settings.DeviceName);
            await session.SendChatAsync(text);
            ChatLines.Add($"Moi → {session.Server.DeviceName} : {text}");
            ChatInput = "";
            StatusText = "Message envoyé.";
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or System.Net.Sockets.SocketException
                                       or IOException or TimeoutException or System.Security.Authentication.AuthenticationException)
        {
            StatusText = "✖ " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Écran distant
    [ObservableProperty] private bool _remoteAudio = true;
    [ObservableProperty] private bool _remoteClipboard = true;

    private bool CanRemote() => !IsBusy && HasTarget;

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task RemoteViewAsync() => StartRemoteAsync(control: false);

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private Task RemoteControlAsync() => StartRemoteAsync(control: true);

    private async Task StartRemoteAsync(bool control)
    {
        var (host, port) = ParseTarget();
        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;
        IsBusy = true;
        StatusText = $"Connexion à {host}…";
        LinkSession? link = null;
        RemoteViewerWindow? window = null;
        try
        {
            link = await LinkClient.ConnectAsync(host, port, TargetPassword,
                _services.Identity.Identity, _services.Settings.DeviceName, ct: ct);
            StatusText = $"En attente de l'accord de {link.Server.DeviceName}… (Annuler pour abandonner)";

            window = new RemoteViewerWindow(link.Server.DeviceName,
                reason => StatusText = reason is null ? "Session d'écran distant terminée." : "✖ " + reason);
            var request = new RemoteRequest(control, RemoteAudio, RemoteClipboard, 1600, 15, 60);
            var viewer = await link.StartRemoteAsync(request, window, ct);

            link = null; // la session en est désormais propriétaire
            window.Attach(viewer);
            if (!window.HasEnded) window.Show();
            StatusText = control ? "Contrôle à distance ouvert." : "Vue de l'écran distant ouverte.";
        }
        catch (RemoteRefusedException ex)
        {
            StatusText = "✖ " + ex.Message;
        }
        catch (AuthenticationFailedException ex)
        {
            StatusText = "✖ " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Demande annulée.";
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException
                                       or System.Security.Authentication.AuthenticationException or LanLink.Core.Protocol.LinkProtocolException)
        {
            StatusText = $"✖ Connexion impossible à {host}:{port} : {ex.Message}";
        }
        finally
        {
            if (link is not null) await link.DisposeAsync();
            if (window is { IsVisible: false }) window.Close();
            IsBusy = false;
            _sendCts?.Dispose();
            _sendCts = null;
        }
    }

    // ---- Synchronisation de dossiers
    public ObservableCollection<SyncPairItem> SyncPairs { get; } = new();

    [ObservableProperty] private SyncPairItem? _selectedSync;

    private readonly Dictionary<Guid, SyncStatus> _syncStatuses = new();

    private void RefreshSyncList()
    {
        var selected = SelectedSync?.Pair.Id;
        SyncPairs.Clear();
        foreach (var pair in _services.SyncPairs.All())
        {
            var item = new SyncPairItem(pair);
            if (_syncStatuses.TryGetValue(pair.Id, out var status)) item.Apply(status);
            SyncPairs.Add(item);
        }
        SelectedSync = SyncPairs.FirstOrDefault(i => i.Pair.Id == selected);
    }

    private void OnSyncStatus(SyncStatus status)
    {
        _syncStatuses[status.PairId] = status;
        SyncPairs.FirstOrDefault(i => i.Pair.Id == status.PairId)?.Apply(status);
    }

    [RelayCommand(CanExecute = nameof(CanRemote))]
    private async Task CreateSyncAsync()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier de ce PC à synchroniser" };
        if (picker.ShowDialog() != true) return;
        var folder = picker.FolderName;
        if (string.Equals(Path.GetPathRoot(folder), folder, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "✖ Choisis un dossier, pas la racine d'un disque.";
            return;
        }

        var (host, port) = ParseTarget();
        _sendCts = new CancellationTokenSource();
        var ct = _sendCts.Token;
        IsBusy = true;
        StatusText = $"Connexion à {host}…";
        try
        {
            await using var link = await LinkClient.ConnectAsync(host, port, TargetPassword,
                _services.Identity.Identity, _services.Settings.DeviceName, ct: ct);
            var pair = new SyncPair
            {
                Name = new DirectoryInfo(folder).Name,
                LocalPath = folder,
                PeerName = link.Server.DeviceName,
                PeerAddress = host,
                PeerPort = port,
                PeerFingerprint = link.Server.CertFingerprint,
                IsInitiator = true,
                Password = TargetPassword,
            };

            StatusText = $"En attente de l'accord de {link.Server.DeviceName}… (Annuler pour abandonner)";
            var reply = await link.RequestSyncPairAsync(new SyncPairRequest(pair.Id, pair.Name), ct);
            if (!reply.Accepted)
            {
                StatusText = "✖ " + (reply.Reason ?? "Synchronisation refusée.");
                return;
            }

            _services.SyncPairs.Add(pair);
            _services.Sync.Watch(pair);
            StatusText = $"Synchronisation « {pair.Name} » créée avec {pair.PeerName}.";
        }
        catch (AuthenticationFailedException ex)
        {
            StatusText = "✖ " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Demande annulée.";
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException
                                       or System.Security.Authentication.AuthenticationException or LanLink.Core.Protocol.LinkProtocolException)
        {
            StatusText = $"✖ Connexion impossible à {host}:{port} : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _sendCts?.Dispose();
            _sendCts = null;
        }
    }

    [RelayCommand]
    private void SyncNow()
    {
        if (SelectedSync is not { } item) return;
        if (!item.Pair.IsInitiator) { StatusText = "Cette synchronisation est pilotée par l'autre PC."; return; }
        if (item.Pair.Paused) { StatusText = "Reprends la synchronisation pour la lancer."; return; }
        _services.Sync.SyncNow(item.Pair.Id);
        item.Apply(new SyncStatus(item.Pair.Id, SyncState.Syncing, "Démarrage…", item.Pair.LastSyncUtc));
    }

    [RelayCommand]
    private void TogglePauseSync()
    {
        if (SelectedSync is not { } item) return;
        if (!item.Pair.IsInitiator) { StatusText = "Seul l'autre PC peut mettre cette synchronisation en pause."; return; }
        item.Pair.Paused = !item.Pair.Paused;
        _services.SyncPairs.Update(item.Pair);
        item.Apply(new SyncStatus(item.Pair.Id, item.Pair.Paused ? SyncState.Paused : SyncState.Idle,
            item.Pair.Paused ? "En pause." : "Reprise…", item.Pair.LastSyncUtc));
        if (!item.Pair.Paused) _services.Sync.SyncNow(item.Pair.Id);
    }

    [RelayCommand]
    private void OpenSyncFolder()
    {
        if (SelectedSync is not { } item) return;
        if (Directory.Exists(item.Pair.LocalPath)) Process.Start("explorer.exe", $"\"{item.Pair.LocalPath}\"");
        else StatusText = "Ce dossier n'existe plus.";
    }

    [RelayCommand]
    private void ResetSync()
    {
        if (SelectedSync is not { } item) return;
        if (!item.Pair.IsInitiator) { StatusText = "Réinitialisation possible seulement depuis l'autre PC."; return; }
        var answer = System.Windows.MessageBox.Show(
            "Oublier l'historique de cette synchronisation ?\n\nLes deux dossiers seront comparés comme si c'était la première fois : " +
            "rien n'est supprimé, les fichiers différents gardent la version la plus récente (l'autre va à la corbeille).",
            "Réinitialiser", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        _services.SyncStates.Delete(item.Pair.Id);
        _services.Sync.SyncNow(item.Pair.Id);
    }

    [RelayCommand]
    private async Task RemoveSyncAsync()
    {
        if (SelectedSync is not { } item) return;
        var answer = System.Windows.MessageBox.Show(
            $"Supprimer la synchronisation « {item.Name} » ?\n\nAucun fichier n'est supprimé, des deux côtés.",
            "Supprimer", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        await _services.Sync.UnwatchAsync(item.Pair.Id);
        _services.SyncStates.Delete(item.Pair.Id);
        _syncStatuses.Remove(item.Pair.Id);
        _services.SyncPairs.Remove(item.Pair.Id);
    }

    // ---- Historique
    [ObservableProperty] private HistoryItem? _selectedHistory;

    [RelayCommand]
    private void OpenHistoryLocation()
    {
        var path = SelectedHistory?.Record.Path;
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        else StatusText = "Ce fichier n'existe plus à cet emplacement.";
    }

    [RelayCommand]
    private void ClearHistory()
    {
        _services.History.Clear();
        History.Clear();
    }

    // ---- Réglages
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _downloadDirectory = "";
    [ObservableProperty] private string _theme = "system";
    [ObservableProperty] private bool _allowScreenSharing = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowPasswordWarning), nameof(PasswordStatus))]
    private bool _hasPassword;
    [ObservableProperty] private string _passwordMessage = "";

    public string PasswordStatus => HasPassword
        ? "Un mot de passe est défini : les autres PC doivent le connaître pour se connecter."
        : "Aucun mot de passe : ce PC refuse toutes les connexions entrantes.";

    // Alimentés par le code-behind (PasswordBox n'est pas liable).
    public string NewPassword { get; set; } = "";
    public string ConfirmPassword { get; set; } = "";
    public Action? ClearPasswordFields { get; set; }

    partial void OnDeviceNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _services.Settings.DeviceName = value.Trim();
        _services.SaveSettings();
    }

    partial void OnAllowScreenSharingChanged(bool value)
    {
        _services.Settings.AllowScreenSharing = value;
        _services.SaveSettings();
    }

    partial void OnDownloadDirectoryChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _services.Settings.DownloadDirectory = value;
        _services.SaveSettings();
    }

    [RelayCommand]
    private void PickDownloadDirectory()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier de réception", InitialDirectory = DownloadDirectory };
        if (dialog.ShowDialog() == true) DownloadDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void SetTheme(string mode)
    {
        Theme = mode;
        _services.Settings.Theme = mode;
        _services.SaveSettings();
        ThemeManager.Apply(mode);
    }

    [RelayCommand]
    private async Task SetPasswordAsync()
    {
        if (NewPassword != ConfirmPassword)
        {
            PasswordMessage = "Les deux mots de passe ne correspondent pas.";
            return;
        }
        if (PasswordPolicy.Validate(NewPassword) is { } problem)
        {
            PasswordMessage = problem;
            return;
        }

        PasswordMessage = "Calcul en cours…";
        var password = NewPassword;
        await Task.Run(() => _services.Identity.SetPassword(password));
        _services.Server.UpdateVerifier(_services.Identity.Verifier);
        HasPassword = true;
        PasswordMessage = "✔ Mot de passe enregistré.";
        ClearPasswordFields?.Invoke();
    }

    // ---- Onglets
    public void ShowSettingsIfNoPassword()
    {
        if (!HasPassword) SelectedTab = 5;
    }
}
