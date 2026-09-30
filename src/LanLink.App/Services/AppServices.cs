using System.Net.Sockets;
using System.Windows;
using LanLink.Core;
using LanLink.Core.Discovery;
using LanLink.Core.Net;
using LanLink.Core.Protocol;
using LanLink.Core.Remote;
using LanLink.Core.Storage;
using LanLink.Core.Sync;

namespace LanLink.App.Services;

/// <summary>Reçoit les événements du serveur (threads réseau) et les relaie à l'interface.</summary>
public sealed class WpfLinkHost : ILinkHost
{
    private readonly AppServices _services;
    private readonly SemaphoreSlim _dialogLock = new(1, 1);

    private readonly Platform.WindowsRemoteEnvironment _remoteEnvironment;
    private Views.SharingBanner? _banner;

    public WpfLinkHost(AppServices services)
    {
        _services = services;
        _remoteEnvironment = new Platform.WindowsRemoteEnvironment(System.Windows.Application.Current.Dispatcher);
    }

    public IRemoteHostEnvironment? RemoteEnvironment => _services.Settings.AllowScreenSharing ? _remoteEnvironment : null;

    public async Task<RemoteGrant?> ConfirmRemoteAsync(RemotePeer peer, RemoteRequest request, CancellationToken ct)
    {
        await _dialogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _services.Tray?.Notify("Partage d'écran demandé",
                $"{peer.DeviceName} veut {(request.WantControl ? "voir et contrôler" : "voir")} votre écran");
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Views.RemoteRequestDialog(peer, request) { Owner = System.Windows.Application.Current.MainWindow };
                using var _ = ct.Register(() => dialog.Dispatcher.BeginInvoke(() => dialog.Close()));
                return dialog.ShowDialog() == true ? dialog.Grant : (RemoteGrant?)null;
            });
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    public void OnRemoteSessionStarted(RemotePeer peer, RemoteGrant grant, Action stop)
    {
        Log($"🖥 Écran partagé avec {peer.DeviceName} ({(grant.Control ? "contrôle" : "vue seule")})");
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _banner?.Close();
            _banner = new Views.SharingBanner(peer, grant, stop);
            _banner.Show();
        });
    }

    public void OnRemoteSessionEnded(RemotePeer peer)
    {
        Log($"⏹ Partage d'écran terminé ({peer.DeviceName}).");
        _services.Tray?.Notify("Partage d'écran terminé", peer.DeviceName);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _banner?.Close();
            _banner = null;
        });
    }

    public async Task<SyncPair?> ConfirmSyncPairAsync(RemotePeer peer, SyncPairRequest request, CancellationToken ct)
    {
        await _dialogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _services.Tray?.Notify("Synchronisation proposée", $"{peer.DeviceName} propose de synchroniser « {request.Name} »");
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Views.SyncPairDialog(peer, request) { Owner = System.Windows.Application.Current.MainWindow };
                using var _ = ct.Register(() => dialog.Dispatcher.BeginInvoke(() => dialog.Close()));
                if (dialog.ShowDialog() != true) return null;

                var pair = new SyncPair
                {
                    Id = request.PairId,
                    Name = request.Name.Length > 64 ? request.Name[..64] : request.Name,
                    LocalPath = dialog.ChosenFolder,
                    PeerName = peer.DeviceName,
                    PeerAddress = peer.Address,
                    PeerFingerprint = peer.CertFingerprint,
                    IsInitiator = false,
                };
                _services.SyncPairs.Add(pair);
                return pair;
            });
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    public SyncPair? FindSyncPair(Guid id) => _services.SyncPairs.Get(id);

    public string DownloadDirectory => _services.Settings.DownloadDirectory;

    public event Action<TransferProgress>? Progress;
    public event Action<TransferRecord>? Finished;
    public event Action<RemotePeer, string>? ChatReceived;
    public event Action<string>? Logged;

    public async Task<bool> ConfirmIncomingAsync(RemotePeer peer, TransferOffer offer, CancellationToken ct)
    {
        // Une seule boîte de dialogue à la fois : les demandes suivantes attendent leur tour.
        await _dialogLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _services.Tray?.Notify("Fichier entrant", $"{peer.DeviceName} veut vous envoyer « {offer.Name} »");
            return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var dialog = new Views.IncomingDialog(peer, offer) { Owner = System.Windows.Application.Current.MainWindow };
                using var _ = ct.Register(() => dialog.Dispatcher.BeginInvoke(() => dialog.Close()));
                return dialog.ShowDialog() == true;
            });
        }
        finally
        {
            _dialogLock.Release();
        }
    }

    public void OnTransferProgress(TransferProgress progress) => Progress?.Invoke(progress);

    public void OnTransferFinished(TransferRecord record)
    {
        _services.History.Add(record);
        if (record.Status == TransferStatus.Completed)
            _services.Tray?.Notify("Transfert terminé", $"« {record.Name} » reçu de {record.PeerName}");
        Finished?.Invoke(record);
    }

    public void OnChatMessage(RemotePeer peer, string text)
    {
        _services.Tray?.Notify(peer.DeviceName, text);
        ChatReceived?.Invoke(peer, text);
    }

    public void Log(string message) => Logged?.Invoke(message);
}

/// <summary>Regroupe l'identité, les réglages, l'historique, le serveur et la découverte.</summary>
public sealed class AppServices : IAsyncDisposable
{
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public IdentityStore Identity { get; }
    public TransferHistory History { get; }
    public SyncPairStore SyncPairs { get; }
    public SyncStateStore SyncStates { get; }
    public SyncCoordinator Sync { get; }
    public WpfLinkHost Host { get; }
    public LinkServer Server { get; }
    public PeerDiscovery Discovery { get; }
    public TrayService? Tray { get; set; }

    /// <summary>Message d'erreur si le port est déjà pris ou le pare-feu bloque.</summary>
    public string? NetworkError { get; private set; }

    public AppServices()
    {
        var root = AppPaths.DefaultRoot;
        SettingsStore = new SettingsStore(root);
        Settings = SettingsStore.Load();
        Identity = new IdentityStore(root);
        History = new TransferHistory(root);
        SyncPairs = new SyncPairStore(root);
        SyncStates = new SyncStateStore(System.IO.Path.Combine(root, "sync"));
        Host = new WpfLinkHost(this);
        Server = new LinkServer(Identity.Identity, Settings.DeviceName, Identity.Verifier, Host);
        Discovery = new PeerDiscovery(() => Settings.DeviceName, Settings.Port);
        Sync = new SyncCoordinator(SyncPairs, SyncStates, Identity.Identity, () => Settings.DeviceName,
            resolveAddress: pair => Discovery.Snapshot().FirstOrDefault(d => d.DeviceName == pair.PeerName)?.Address);
    }

    public void StartNetwork()
    {
        try
        {
            Server.Start(Settings.Port);
            Discovery.Start();
            Sync.Start();
        }
        catch (SocketException ex)
        {
            NetworkError = $"Impossible d'écouter sur le port {Settings.Port} : {ex.Message}";
        }
    }

    public void SaveSettings() => SettingsStore.Save(Settings);

    public async ValueTask DisposeAsync()
    {
        await Sync.DisposeAsync();
        await Server.DisposeAsync();
        await Discovery.DisposeAsync();
        Identity.Dispose();
    }
}
