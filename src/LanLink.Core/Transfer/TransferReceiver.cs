using System.Security.Cryptography;
using System.Text.Json;
using LanLink.Core.Protocol;
using LanLink.Core.Security;

namespace LanLink.Core.Transfer;

/// <summary>
/// Côté destinataire d'un transfert. Les données partielles (« .part ») et l'état sont gardés dans
/// <c>&lt;dossier de réception&gt;/.lanlink</c> : un transfert coupé reprend là où il s'est arrêté.
/// </summary>
public sealed class TransferReceiver
{
    private sealed class State
    {
        public string? RootPath { get; set; }
        public List<int> Completed { get; set; } = new();
    }

    private readonly ILinkHost _host;
    private readonly RemotePeer _peer;

    public TransferReceiver(ILinkHost host, RemotePeer peer)
    {
        _host = host;
        _peer = peer;
    }

    public async Task ReceiveAsync(FrameChannel channel, TransferOffer offer, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var name = "?";
        string? finalPath = null;
        try
        {
            var files = Validate(offer, out name);

            var confirmed = await _host.ConfirmIncomingAsync(_peer, offer, ct).ConfigureAwait(false);
            if (!confirmed)
            {
                await channel.WriteJsonAsync(FrameType.OfferReply, new OfferReply(false, "Refusé"), ct).ConfigureAwait(false);
                Finish(id, name, offer.TotalSize, TransferStatus.Rejected, null, null);
                return;
            }

            var downloadDir = Path.GetFullPath(_host.DownloadDirectory);
            Directory.CreateDirectory(downloadDir);
            var stateDir = Path.Combine(downloadDir, ".lanlink");
            Directory.CreateDirectory(stateDir);

            var key = TransferKey(offer);
            var statePath = Path.Combine(stateDir, key + ".json");
            var state = LoadState(statePath);

            if (!HasEnoughSpace(downloadDir, offer.TotalSize))
            {
                await channel.WriteJsonAsync(FrameType.OfferReply, new OfferReply(false, "Espace disque insuffisant"), ct).ConfigureAwait(false);
                Finish(id, name, offer.TotalSize, TransferStatus.Failed, "Espace disque insuffisant", null);
                return;
            }

            string? root = null;
            if (offer.Kind == "folder")
            {
                root = state.RootPath is { } saved && Directory.Exists(saved)
                    ? saved
                    : SafePath.Unique(Path.Combine(downloadDir, name));
                Directory.CreateDirectory(root);
                state.RootPath = root;
                SaveState(statePath, state);
            }

            await channel.WriteJsonAsync(FrameType.OfferReply, new OfferReply(true, null), ct).ConfigureAwait(false);
            channel.ReadTimeout = TransferConstants.IoTimeout;

            long done = 0;
            void Report() => _host.OnTransferProgress(new TransferProgress(id, name, TransferDirection.Received, done, offer.TotalSize));
            Report();

            for (var i = 0; i < files.Count; i++)
            {
                var start = (await channel.ExpectAsync(FrameType.FileStart, ct).ConfigureAwait(false)).Json<FileStartMessage>();
                if (start.Index != i) throw new LinkProtocolException("Ordre des fichiers inattendu.");
                var entry = offer.Files[i];

                if (state.Completed.Contains(i))
                {
                    await channel.WriteJsonAsync(FrameType.ResumeInfo, new ResumeInfo(true, entry.Size, null), ct).ConfigureAwait(false);
                    done += entry.Size;
                    Report();
                    continue;
                }

                var partPath = Path.Combine(stateDir, $"{key}-{i}.part");
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var have = await PreparePartAsync(partPath, entry.Size, hasher, ct).ConfigureAwait(false);
                await channel.WriteJsonAsync(FrameType.ResumeInfo,
                    new ResumeInfo(false, have, have > 0 ? Convert.ToHexString(hasher.GetCurrentHash()) : null), ct).ConfigureAwait(false);

                var ack = (await channel.ExpectAsync(FrameType.ResumeAck, ct).ConfigureAwait(false)).Json<ResumeAck>();
                if (ack.ResumeAt != have && ack.ResumeAt != 0)
                    throw new LinkProtocolException("Point de reprise incohérent.");
                if (ack.ResumeAt == 0 && have > 0) hasher.GetHashAndReset();

                await using (var part = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                                 bufferSize: 1, FileOptions.Asynchronous))
                {
                    part.SetLength(ack.ResumeAt);
                    part.Position = ack.ResumeAt;
                    var received = ack.ResumeAt;
                    done += received;
                    while (received < entry.Size)
                    {
                        var frame = await channel.ExpectAsync(FrameType.Data, ct).ConfigureAwait(false);
                        if (frame.Payload.Length == 0 || received + frame.Payload.Length > entry.Size)
                            throw new LinkProtocolException("Données reçues incohérentes.");
                        await part.WriteAsync(frame.Payload, ct).ConfigureAwait(false);
                        hasher.AppendData(frame.Payload.Span);
                        received += frame.Payload.Length;
                        done += frame.Payload.Length;
                        Report();
                    }
                    await part.FlushAsync(ct).ConfigureAwait(false);
                }

                var end = (await channel.ExpectAsync(FrameType.FileEnd, ct).ConfigureAwait(false)).Json<FileEndMessage>();
                var actual = Convert.ToHexString(hasher.GetHashAndReset());
                if (!string.Equals(actual, end.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partPath);
                    await channel.WriteJsonAsync(FrameType.FileResult, new FileResult(false, "Somme de contrôle incorrecte"), ct).ConfigureAwait(false);
                    throw new IOException($"Somme de contrôle incorrecte pour {entry.Path}.");
                }

                var destination = offer.Kind == "folder"
                    ? SafePath.Combine(root!, files[i])
                    : (finalPath = SafePath.Unique(SafePath.Combine(downloadDir, files[i])));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(partPath, destination, overwrite: true);
                try { File.SetLastWriteTimeUtc(destination, new DateTime(entry.ModifiedUtcTicks, DateTimeKind.Utc)); }
                catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException) { /* date non critique */ }

                state.Completed.Add(i);
                SaveState(statePath, state);
                await channel.WriteJsonAsync(FrameType.FileResult, new FileResult(true, null), ct).ConfigureAwait(false);
            }

            File.Delete(statePath);
            Finish(id, name, offer.TotalSize, TransferStatus.Completed, null, root ?? finalPath);
        }
        catch (OperationCanceledException)
        {
            Finish(id, name, offer.TotalSize, TransferStatus.Cancelled, null, null);
            throw;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or TimeoutException)
        {
            // Connexion perdue : les données partielles sont conservées pour une reprise.
            Finish(id, name, offer.TotalSize, TransferStatus.Interrupted, ex.Message, null);
            throw;
        }
        catch (Exception ex)
        {
            Finish(id, name, offer.TotalSize, TransferStatus.Failed, ex.Message, null);
            throw;
        }
    }

    private void Finish(Guid id, string name, long size, TransferStatus status, string? error, string? path) =>
        _host.OnTransferFinished(new TransferRecord(id, DateTime.UtcNow, TransferDirection.Received, name, size,
            _peer.DeviceName, _peer.Address, status, error, path));

    /// <summary>Vérifie l'offre et renvoie les chemins relatifs assainis.</summary>
    private static List<string> Validate(TransferOffer offer, out string name)
    {
        if (offer.Kind is not ("file" or "folder")) throw new LinkProtocolException("Type de transfert inconnu.");
        name = SafePath.FileName(offer.Name);
        if (offer.Files is null || offer.Files.Count > TransferConstants.MaxFiles)
            throw new LinkProtocolException("Liste de fichiers invalide.");
        if (offer.Kind == "file" && offer.Files.Count != 1)
            throw new LinkProtocolException("Un fichier unique est attendu.");

        long total = 0;
        var paths = new List<string>(offer.Files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in offer.Files)
        {
            if (f.Size < 0) throw new LinkProtocolException("Taille invalide.");
            total = checked(total + f.Size);
            var rel = offer.Kind == "file" ? SafePath.FileName(f.Path) : SafePath.RelativePath(f.Path);
            if (!seen.Add(rel)) throw new LinkProtocolException($"Chemin en double : {rel}");
            paths.Add(rel);
        }
        if (total != offer.TotalSize) throw new LinkProtocolException("Taille totale incohérente.");
        return paths;
    }

    private string TransferKey(TransferOffer offer)
    {
        using var sha = SHA256.Create();
        var text = string.Join('\n', new[] { _peer.CertFingerprint, offer.Kind, offer.Name }
            .Concat(offer.Files.Select(f => $"{f.Path}|{f.Size}|{f.ModifiedUtcTicks}")));
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>
    /// Tronque le « .part » existant à un multiple de la taille de bloc, le hache, et renvoie sa taille.
    /// </summary>
    private static async Task<long> PreparePartAsync(string partPath, long fileSize, IncrementalHash hasher, CancellationToken ct)
    {
        if (!File.Exists(partPath)) return 0;
        long have;
        await using (var probe = new FileStream(partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            have = Math.Min(probe.Length, fileSize) / TransferConstants.BlockSize * TransferConstants.BlockSize;
            probe.SetLength(have);
        }
        if (have == 0) return 0;

        await using var input = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[TransferConstants.BlockSize];
        long left = have;
        while (left > 0)
        {
            var n = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
            if (n == 0) return 0;
            hasher.AppendData(buffer, 0, n);
            left -= n;
        }
        return have;
    }

    private static bool HasEnoughSpace(string directory, long needed)
    {
        try { return new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace > needed + 64L * 1024 * 1024; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return true; }
    }

    private static State LoadState(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new State() : new State(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new State(); }
    }

    private static void SaveState(string path, State state) => File.WriteAllText(path, JsonSerializer.Serialize(state));
}
