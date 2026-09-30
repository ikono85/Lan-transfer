namespace LanLink.Core;

public enum TransferDirection { Sent, Received }

public enum TransferStatus { Completed, Failed, Cancelled, Interrupted, Rejected }

/// <summary>Un correspondant authentifié.</summary>
public sealed record RemotePeer(string Address, string DeviceName, string CertFingerprint);

/// <summary>Progression d'un transfert (octets d'origine, reprise comprise).</summary>
public sealed record TransferProgress(Guid Id, string Name, TransferDirection Direction, long BytesDone, long TotalBytes);

/// <summary>Entrée de l'historique.</summary>
public sealed record TransferRecord(
    Guid Id,
    DateTime TimeUtc,
    TransferDirection Direction,
    string Name,
    long Size,
    string PeerName,
    string PeerAddress,
    TransferStatus Status,
    string? Error,
    string? Path);

/// <summary>Callbacks du serveur vers l'application (interface graphique, tests…).</summary>
public interface ILinkHost
{
    string DownloadDirectory { get; }

    /// <summary>Demande à l'utilisateur d'accepter un transfert entrant.</summary>
    Task<bool> ConfirmIncomingAsync(RemotePeer peer, Protocol.TransferOffer offer, CancellationToken ct);

    void OnTransferProgress(TransferProgress progress);

    void OnTransferFinished(TransferRecord record);

    void OnChatMessage(RemotePeer peer, string text);

    void Log(string message);

    // ---- Partage d'écran (facultatif : sans environnement, toute demande est refusée)

    /// <summary>Fabrique des éléments de capture/injection ; null si ce PC ne peut pas partager son écran.</summary>
    Remote.IRemoteHostEnvironment? RemoteEnvironment => null;

    /// <summary>Demande l'accord de l'utilisateur ; renvoie null pour refuser, sinon ce qui est autorisé.</summary>
    Task<Remote.RemoteGrant?> ConfirmRemoteAsync(RemotePeer peer, Remote.RemoteRequest request, CancellationToken ct) =>
        Task.FromResult<Remote.RemoteGrant?>(null);

    /// <summary>Le partage démarre ; <paramref name="stop"/> l'interrompt immédiatement.</summary>
    void OnRemoteSessionStarted(RemotePeer peer, Remote.RemoteGrant grant, Action stop) { }

    void OnRemoteSessionEnded(RemotePeer peer) { }

    // ---- Synchronisation de dossiers

    /// <summary>Un PC propose de synchroniser un dossier : renvoie la paire créée (dossier choisi) ou null pour refuser.</summary>
    Task<Sync.SyncPair?> ConfirmSyncPairAsync(RemotePeer peer, Sync.SyncPairRequest request, CancellationToken ct) =>
        Task.FromResult<Sync.SyncPair?>(null);

    /// <summary>Retrouve une paire acceptée précédemment (null si inconnue).</summary>
    Sync.SyncPair? FindSyncPair(Guid id) => null;
}
