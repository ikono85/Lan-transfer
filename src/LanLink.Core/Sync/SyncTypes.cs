namespace LanLink.Core.Sync;

// ---- Messages du protocole (JSON)
public sealed record SyncPairRequest(Guid PairId, string Name);

public sealed record SyncPairReply(bool Accepted, string? Reason);

public sealed record SyncOpenMessage(Guid PairId);

public sealed record SyncOpenReply(bool Ok, string? Reason);

public sealed record SyncListEntry(string Path, long Size, long Ticks, bool Busy);

public sealed record SyncListing(List<SyncListEntry> Entries, bool Last);

public sealed record SyncPathMessage(string Path);

public sealed record SyncFileInfo(bool Ok, string? Error, long Size, long Ticks);

public sealed record SyncPutMessage(string Path, long Size, long Ticks);

public sealed record SyncOpReply(bool Ok, string? Error);

// ---- Modèle

/// <summary>État d'un fichier : taille, date de modification (ticks UTC) et « en cours d'écriture ».</summary>
public sealed record SyncEntry(long Size, long Ticks, bool Busy = false);

/// <summary>Une synchronisation entre un dossier de ce PC et un dossier d'un autre PC.</summary>
public sealed class SyncPair
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string PeerAddress { get; set; } = "";
    public int PeerPort { get; set; } = 45870;

    /// <summary>Empreinte du certificat de l'autre PC : toute connexion avec un autre certificat est refusée.</summary>
    public string PeerFingerprint { get; set; } = "";

    /// <summary>Vrai sur le PC qui a créé la synchronisation : c'est lui qui se connecte et pilote les échanges.</summary>
    public bool IsInitiator { get; set; }

    /// <summary>Mot de passe de l'autre PC (initiateur seulement) ; chiffré au repos par <see cref="SyncPairStore"/>.</summary>
    public string? Password { get; set; }

    public int IntervalSeconds { get; set; } = 60;
    public bool Paused { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public string? LastError { get; set; }
}

public enum SyncActionKind { Push, Pull, DeleteLocal, DeleteRemote }

/// <param name="Conflict">Les deux côtés avaient changé : la version la plus récente gagne, l'autre va à la corbeille.</param>
public sealed record SyncAction(string Path, SyncActionKind Kind, bool Conflict = false);

public sealed record SyncPlan(List<SyncAction> Actions, Dictionary<string, SyncEntry> Baseline);

public sealed record SyncProgress(Guid PairId, string Phase, string? CurrentPath, int Done, int Total);

public sealed record SyncResult(int Pushed, int Pulled, int DeletedLocal, int DeletedRemote, int Conflicts, List<string> Errors)
{
    public int Total => Pushed + Pulled + DeletedLocal + DeletedRemote;
}

public class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
}

/// <summary>La synchronisation a été interrompue par prudence (dossier absent, suppression massive…).</summary>
public sealed class SyncAbortedException : SyncException
{
    public SyncAbortedException(string message) : base(message) { }
}
