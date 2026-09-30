using LanLink.Core.Protocol;
using LanLink.Core.Security;

namespace LanLink.Core.Sync;

/// <summary>Côté initiateur : compare les deux dossiers puis applique le plan sur la connexion.</summary>
public static class SyncEngine
{
    /// <summary>Au-delà de ce nombre de suppressions ET de la moitié de la base, on suppose une erreur (dossier vidé ou absent) et on s'arrête.</summary>
    public const int MassDeleteMinimum = 10;

    public static async Task<SyncResult> RunAsync(FrameChannel channel, SyncPair pair, SyncStateStore states,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        if (!Directory.Exists(pair.LocalPath))
            throw new SyncAbortedException($"Le dossier « {pair.LocalPath} » est introuvable.");

        var fs = new SyncFileSystem(pair.LocalPath);
        void Report(string phase, string? path = null, int done = 0, int total = 0) =>
            progress?.Report(new SyncProgress(pair.Id, phase, path, done, total));

        await channel.WriteJsonAsync(FrameType.SyncOpen, new SyncOpenMessage(pair.Id), ct).ConfigureAwait(false);
        channel.ReadTimeout = TimeSpan.FromSeconds(60);
        var open = (await channel.ExpectAsync(FrameType.SyncOpenReply, ct).ConfigureAwait(false)).Json<SyncOpenReply>();
        if (!open.Ok) throw new SyncAbortedException(open.Reason ?? "Synchronisation refusée par l'autre PC.");

        Report("Analyse des dossiers");
        var local = await Task.Run(() => FolderScanner.Scan(pair.LocalPath), ct).ConfigureAwait(false);
        var remote = await ReadRemoteListingAsync(channel, ct).ConfigureAwait(false);
        var baseline = states.Load(pair.Id);

        var plan = SyncPlanner.Plan(local, remote, baseline);
        var deletes = plan.Actions.Count(a => a.Kind is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote);
        if (deletes >= MassDeleteMinimum && deletes > baseline.Count / 2)
        {
            await SafeDoneAsync(channel, ct).ConfigureAwait(false);
            throw new SyncAbortedException(
                $"{deletes} suppressions prévues sur {baseline.Count} fichiers : par prudence, rien n'a été fait. " +
                "Vérifie que le bon dossier est branché, puis réinitialise la synchronisation si c'est voulu.");
        }

        var newBase = plan.Baseline;
        int pushed = 0, pulled = 0, deletedLocal = 0, deletedRemote = 0, conflicts = 0, index = 0;
        var errors = new List<string>();

        try
        {
            foreach (var action in plan.Actions)
            {
                ct.ThrowIfCancellationRequested();
                Report(Describe(action.Kind), action.Path, index++, plan.Actions.Count);
                try
                {
                    switch (action.Kind)
                    {
                        case SyncActionKind.Push:
                            if (await PushAsync(channel, fs, pair.LocalPath, action.Path, local[action.Path], newBase, ct).ConfigureAwait(false))
                            {
                                pushed++;
                                if (action.Conflict) conflicts++;
                            }
                            break;
                        case SyncActionKind.Pull:
                            if (await PullAsync(channel, fs, action.Path, newBase, ct).ConfigureAwait(false))
                            {
                                pulled++;
                                if (action.Conflict) conflicts++;
                            }
                            break;
                        case SyncActionKind.DeleteLocal:
                            try { fs.Delete(action.Path); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                throw new SyncException("suppression impossible (" + ex.Message + ")");
                            }
                            newBase.Remove(action.Path);
                            deletedLocal++;
                            break;
                        case SyncActionKind.DeleteRemote:
                            await channel.WriteJsonAsync(FrameType.SyncDelete, new SyncPathMessage(action.Path), ct).ConfigureAwait(false);
                            var result = (await channel.ExpectAsync(FrameType.FileResult, ct).ConfigureAwait(false)).Json<FileResult>();
                            if (!result.Ok) throw new SyncException(result.Error ?? "suppression refusée");
                            newBase.Remove(action.Path);
                            deletedRemote++;
                            break;
                    }
                }
                catch (SyncException ex)
                {
                    // Échec propre à ce fichier (protocole toujours synchronisé) : on continue avec les autres.
                    errors.Add($"{action.Path} : {ex.Message}");
                }
            }

            await SafeDoneAsync(channel, ct).ConfigureAwait(false);
        }
        finally
        {
            // Même interrompue, on garde ce qui a été fait : la prochaine passe reprend le reste.
            states.Save(pair.Id, newBase);
        }

        Report("Terminé", null, plan.Actions.Count, plan.Actions.Count);
        return new SyncResult(pushed, pulled, deletedLocal, deletedRemote, conflicts, errors);
    }

    private static string Describe(SyncActionKind kind) => kind switch
    {
        SyncActionKind.Push => "Envoi",
        SyncActionKind.Pull => "Réception",
        SyncActionKind.DeleteLocal => "Suppression locale",
        _ => "Suppression distante",
    };

    private static async Task<Dictionary<string, SyncEntry>> ReadRemoteListingAsync(FrameChannel channel, CancellationToken ct)
    {
        await channel.WriteAsync(FrameType.SyncListRequest, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
        var remote = new Dictionary<string, SyncEntry>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var chunk = (await channel.ExpectAsync(FrameType.SyncListing, ct).ConfigureAwait(false)).Json<SyncListing>();
            foreach (var e in chunk.Entries)
            {
                if (SafePath.RelativePath(e.Path) != e.Path || e.Size < 0) throw new LinkProtocolException("Liste de fichiers invalide.");
                remote[e.Path] = new SyncEntry(e.Size, e.Ticks, e.Busy);
            }
            if (remote.Count > 2_000_000) throw new LinkProtocolException("Trop de fichiers.");
            if (chunk.Last) return remote;
        }
    }

    private static async Task SafeDoneAsync(FrameChannel channel, CancellationToken ct)
    {
        try
        {
            await channel.WriteAsync(FrameType.SyncDone, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            await channel.ExpectAsync(FrameType.Ack, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or LinkProtocolException or TimeoutException) { }
    }

    /// <returns>Faux si le fichier a été ignoré (modifié depuis l'analyse).</returns>
    private static async Task<bool> PushAsync(FrameChannel channel, SyncFileSystem fs, string root, string path,
        SyncEntry entry, Dictionary<string, SyncEntry> baseline, CancellationToken ct)
    {
        FileStream file;
        try { file = SyncIo.OpenForRead(fs.Resolve(path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SyncException("illisible (" + ex.Message + ")");
        }

        await using (file.ConfigureAwait(false))
        {
            // Modifié depuis l'analyse : on le reprendra à la prochaine passe. Rien n'a encore été envoyé.
            var modified = File.GetLastWriteTimeUtc(fs.Resolve(path)).Ticks;
            if (file.Length != entry.Size || Math.Abs(modified - entry.Ticks) > SyncPlanner.TickTolerance)
                throw new SyncException("modifié pendant la synchronisation, repris au prochain passage");

            await channel.WriteJsonAsync(FrameType.SyncPut, new SyncPutMessage(path, entry.Size, entry.Ticks), ct).ConfigureAwait(false);
            var reply = (await channel.ExpectAsync(FrameType.SyncPutReply, ct).ConfigureAwait(false)).Json<SyncOpReply>();
            if (!reply.Ok) throw new SyncException(reply.Error ?? "refusé");

            await SyncIo.SendAsync(channel, file, entry.Size, null, ct).ConfigureAwait(false);
            var result = (await channel.ExpectAsync(FrameType.FileResult, ct).ConfigureAwait(false)).Json<FileResult>();
            if (!result.Ok) throw new SyncException(result.Error ?? "rejeté");
        }

        baseline[path] = new SyncEntry(entry.Size, entry.Ticks);
        return true;
    }

    private static async Task<bool> PullAsync(FrameChannel channel, SyncFileSystem fs, string path,
        Dictionary<string, SyncEntry> baseline, CancellationToken ct)
    {
        await channel.WriteJsonAsync(FrameType.SyncGet, new SyncPathMessage(path), ct).ConfigureAwait(false);
        var info = (await channel.ExpectAsync(FrameType.SyncFileInfo, ct).ConfigureAwait(false)).Json<SyncFileInfo>();
        if (!info.Ok) throw new SyncException(info.Error ?? "introuvable");

        var temp = fs.NewTempPath();
        await SyncIo.ReceiveAsync(channel, temp, info.Size, null, ct).ConfigureAwait(false);
        try { fs.Replace(path, temp, info.Ticks); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw new SyncException("écriture impossible (" + ex.Message + ")");
        }

        var written = new FileInfo(fs.Resolve(path));
        baseline[path] = new SyncEntry(written.Length, written.LastWriteTimeUtc.Ticks);
        return true;
    }
}
