using System.Security.Cryptography;
using LanLink.Core.Protocol;

namespace LanLink.Core.Transfer;

public sealed class TransferRejectedException : Exception
{
    public TransferRejectedException(string? reason)
        : base(string.IsNullOrEmpty(reason) ? "Transfert refusé par le destinataire." : $"Transfert refusé : {reason}") { }
}

internal static class TransferConstants
{
    public const int BlockSize = 256 * 1024;
    public const int MaxFiles = 200_000;
    public static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AcceptTimeout = TimeSpan.FromMinutes(5);
}

/// <summary>Côté expéditeur d'un transfert (fichier ou dossier), avec reprise après coupure.</summary>
public static class TransferSender
{
    public static async Task SendAsync(FrameChannel channel, string path,
        IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var (offer, absolutePaths) = BuildOffer(path);
        var id = Guid.NewGuid();

        channel.ReadTimeout = TransferConstants.AcceptTimeout;
        await channel.WriteJsonAsync(FrameType.Offer, offer, ct).ConfigureAwait(false);
        var reply = (await channel.ExpectAsync(FrameType.OfferReply, ct).ConfigureAwait(false)).Json<OfferReply>();
        if (!reply.Accepted) throw new TransferRejectedException(reply.Reason);
        channel.ReadTimeout = TransferConstants.IoTimeout;

        long done = 0;
        void Report() => progress?.Report(new TransferProgress(id, offer.Name, TransferDirection.Sent, done, offer.TotalSize));
        Report();

        var buffer = new byte[TransferConstants.BlockSize];
        for (var i = 0; i < offer.Files.Count; i++)
        {
            var entry = offer.Files[i];
            await channel.WriteJsonAsync(FrameType.FileStart, new FileStartMessage(i), ct).ConfigureAwait(false);
            var info = (await channel.ExpectAsync(FrameType.ResumeInfo, ct).ConfigureAwait(false)).Json<ResumeInfo>();
            if (info.Done)
            {
                done += entry.Size;
                Report();
                continue;
            }

            await using var file = new FileStream(absolutePaths[i], FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != entry.Size)
                throw new IOException($"Le fichier a changé pendant l'envoi : {entry.Path}");

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var resumeAt = 0L;
            if (info.Have > 0 && info.Have <= entry.Size && info.PrefixSha256 is not null)
            {
                await HashPrefixAsync(file, hasher, info.Have, buffer, ct).ConfigureAwait(false);
                if (string.Equals(Convert.ToHexString(hasher.GetCurrentHash()), info.PrefixSha256, StringComparison.OrdinalIgnoreCase))
                {
                    resumeAt = info.Have;
                }
                else
                {
                    hasher.GetHashAndReset();
                    file.Position = 0;
                }
            }

            await channel.WriteJsonAsync(FrameType.ResumeAck, new ResumeAck(resumeAt), ct).ConfigureAwait(false);
            done += resumeAt;
            Report();

            var remaining = entry.Size - resumeAt;
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
                if (read == 0) throw new IOException($"Fin de fichier inattendue : {entry.Path}");
                hasher.AppendData(buffer, 0, read);
                await channel.WriteAsync(FrameType.Data, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
                done += read;
                Report();
            }

            await channel.WriteJsonAsync(FrameType.FileEnd,
                new FileEndMessage(Convert.ToHexString(hasher.GetHashAndReset())), ct).ConfigureAwait(false);
            var result = (await channel.ExpectAsync(FrameType.FileResult, ct).ConfigureAwait(false)).Json<FileResult>();
            if (!result.Ok) throw new IOException($"Le destinataire a rejeté « {entry.Path} » : {result.Error}");
        }
    }

    private static async Task HashPrefixAsync(FileStream file, IncrementalHash hasher, long count, byte[] buffer, CancellationToken ct)
    {
        while (count > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("Fin de fichier inattendue.");
            hasher.AppendData(buffer, 0, read);
            count -= read;
        }
    }

    internal static (TransferOffer Offer, List<string> Paths) BuildOffer(string path)
    {
        path = Path.GetFullPath(path);
        var files = new List<FileEntry>();
        var absolute = new List<string>();
        string kind, name;

        if (Directory.Exists(path))
        {
            kind = "folder";
            name = new DirectoryInfo(path).Name;
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options).OrderBy(f => f.FullName, StringComparer.Ordinal))
            {
                files.Add(new FileEntry(Path.GetRelativePath(path, file.FullName).Replace('\\', '/'), file.Length, file.LastWriteTimeUtc.Ticks));
                absolute.Add(file.FullName);
            }
            if (files.Count > TransferConstants.MaxFiles) throw new IOException("Trop de fichiers dans ce dossier.");
        }
        else if (File.Exists(path))
        {
            kind = "file";
            var info = new FileInfo(path);
            name = info.Name;
            files.Add(new FileEntry(info.Name, info.Length, info.LastWriteTimeUtc.Ticks));
            absolute.Add(path);
        }
        else
        {
            throw new FileNotFoundException("Fichier ou dossier introuvable.", path);
        }

        return (new TransferOffer(kind, name, files.Sum(f => f.Size), files), absolute);
    }
}
