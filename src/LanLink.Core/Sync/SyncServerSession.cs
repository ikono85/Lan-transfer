using LanLink.Core.Protocol;
using LanLink.Core.Security;

namespace LanLink.Core.Sync;

/// <summary>Côté PC passif : répond aux demandes de l'initiateur (liste, lecture, écriture, suppression) dans le dossier de la paire.</summary>
public sealed class SyncServerSession
{
    private const int ListingChunk = 2000;
    private const long MinimumFreeSpace = 64L * 1024 * 1024;

    private readonly FrameChannel _channel;
    private readonly SyncPair _pair;
    private readonly Action<string> _log;
    private readonly SyncFileSystem _fs;

    public SyncServerSession(FrameChannel channel, SyncPair pair, Action<string> log)
    {
        _channel = channel;
        _pair = pair;
        _log = log;
        _fs = new SyncFileSystem(pair.LocalPath);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_pair.LocalPath))
        {
            await _channel.WriteJsonAsync(FrameType.SyncOpenReply,
                new SyncOpenReply(false, $"Le dossier « {_pair.LocalPath} » est introuvable sur l'autre PC."), ct).ConfigureAwait(false);
            return;
        }
        await _channel.WriteJsonAsync(FrameType.SyncOpenReply, new SyncOpenReply(true, null), ct).ConfigureAwait(false);
        _fs.Cleanup(TimeSpan.FromDays(30));

        while (!ct.IsCancellationRequested)
        {
            var frame = await _channel.ReadAsync(ct).ConfigureAwait(false);
            switch (frame.Type)
            {
                case FrameType.SyncListRequest:
                    await SendListingAsync(ct).ConfigureAwait(false);
                    break;
                case FrameType.SyncGet:
                    await ServeGetAsync(frame.Json<SyncPathMessage>().Path, ct).ConfigureAwait(false);
                    break;
                case FrameType.SyncPut:
                    await ServePutAsync(frame.Json<SyncPutMessage>(), ct).ConfigureAwait(false);
                    break;
                case FrameType.SyncDelete:
                    await ServeDeleteAsync(frame.Json<SyncPathMessage>().Path, ct).ConfigureAwait(false);
                    break;
                case FrameType.SyncDone:
                    await _channel.WriteAsync(FrameType.Ack, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                    return;
                default:
                    throw new LinkProtocolException($"Requête de synchronisation inattendue : {frame.Type}");
            }
        }
    }

    private async Task SendListingAsync(CancellationToken ct)
    {
        var entries = await Task.Run(() => FolderScanner.Scan(_pair.LocalPath), ct).ConfigureAwait(false);
        var all = entries.Select(e => new SyncListEntry(e.Key, e.Value.Size, e.Value.Ticks, e.Value.Busy)).ToList();
        for (var i = 0; ; i += ListingChunk)
        {
            var chunk = all.Skip(i).Take(ListingChunk).ToList();
            var last = i + ListingChunk >= all.Count;
            await _channel.WriteJsonAsync(FrameType.SyncListing, new SyncListing(chunk, last), ct).ConfigureAwait(false);
            if (last) break;
        }
    }

    private async Task ServeGetAsync(string path, CancellationToken ct)
    {
        FileStream file;
        long size, ticks;
        try
        {
            var full = _fs.Resolve(path);
            file = SyncIo.OpenForRead(full);
            size = file.Length;
            ticks = File.GetLastWriteTimeUtc(full).Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await _channel.WriteJsonAsync(FrameType.SyncFileInfo, new SyncFileInfo(false, ex.Message, 0, 0), ct).ConfigureAwait(false);
            return;
        }

        await using (file.ConfigureAwait(false))
        {
            await _channel.WriteJsonAsync(FrameType.SyncFileInfo, new SyncFileInfo(true, null, size, ticks), ct).ConfigureAwait(false);
            await SyncIo.SendAsync(_channel, file, size, null, ct).ConfigureAwait(false);
        }
    }

    private async Task ServePutAsync(SyncPutMessage put, CancellationToken ct)
    {
        try
        {
            SafePath.RelativePath(put.Path);
            if (put.Size < 0) throw new ArgumentException("taille invalide");
            if (!HasSpace(put.Size)) throw new IOException("espace disque insuffisant");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            await _channel.WriteJsonAsync(FrameType.SyncPutReply, new SyncOpReply(false, ex.Message), ct).ConfigureAwait(false);
            return;
        }

        await _channel.WriteJsonAsync(FrameType.SyncPutReply, new SyncOpReply(true, null), ct).ConfigureAwait(false);

        var temp = _fs.NewTempPath();
        try
        {
            await SyncIo.ReceiveAsync(_channel, temp, put.Size, null, ct).ConfigureAwait(false);
            try { _fs.Replace(put.Path, temp, put.Ticks); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new SyncException("écriture impossible (" + ex.Message + ")");
            }
            await _channel.WriteJsonAsync(FrameType.FileResult, new FileResult(true, null), ct).ConfigureAwait(false);
        }
        catch (SyncException ex)
        {
            // Tout le contenu a été lu : le protocole reste synchronisé, on signale seulement l'échec de ce fichier.
            try { File.Delete(temp); } catch (IOException) { }
            _log($"✖ Synchronisation « {_pair.Name} » : {put.Path} : {ex.Message}");
            await _channel.WriteJsonAsync(FrameType.FileResult, new FileResult(false, ex.Message), ct).ConfigureAwait(false);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    private async Task ServeDeleteAsync(string path, CancellationToken ct)
    {
        try
        {
            _fs.Delete(path);
            await _channel.WriteJsonAsync(FrameType.FileResult, new FileResult(true, null), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await _channel.WriteJsonAsync(FrameType.FileResult, new FileResult(false, ex.Message), ct).ConfigureAwait(false);
        }
    }

    private bool HasSpace(long needed)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_pair.LocalPath))!).AvailableFreeSpace > needed + MinimumFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return true; }
    }
}
