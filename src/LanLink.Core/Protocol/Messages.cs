namespace LanLink.Core.Protocol;

// ---- Handshake (après l'établissement TLS 1.3 mutuel)
public sealed record HelloMessage(int Version, string DeviceName, string Salt, int MemoryKiB, int Iterations, int Parallelism, string Nonce);

public sealed record ClientAuthMessage(string DeviceName, string Nonce, string Proof);

public sealed record ServerAuthMessage(bool Ok, string? Proof, string? Error);

// ---- Transfert de fichiers
public sealed record FileEntry(string Path, long Size, long ModifiedUtcTicks);

/// <param name="Kind">"file" ou "folder".</param>
public sealed record TransferOffer(string Kind, string Name, long TotalSize, List<FileEntry> Files);

public sealed record OfferReply(bool Accepted, string? Reason);

public sealed record FileStartMessage(int Index);

/// <param name="Done">Le fichier est déjà complet côté récepteur.</param>
/// <param name="Have">Octets déjà reçus (multiple de la taille de bloc).</param>
/// <param name="PrefixSha256">SHA-256 (hex) de ces <paramref name="Have"/> premiers octets.</param>
public sealed record ResumeInfo(bool Done, long Have, string? PrefixSha256);

public sealed record ResumeAck(long ResumeAt);

public sealed record FileEndMessage(string Sha256);

public sealed record FileResult(bool Ok, string? Error);

// ---- Messagerie
public sealed record ChatMessage(string Text);
