using System.Security.Cryptography;
using LanLink.Core.Protocol;

namespace LanLink.Core.Sync;

/// <summary>Envoi et réception du contenu d'un fichier (trames Data puis FileEnd avec SHA-256), dans les deux sens.</summary>
internal static class SyncIo
{
    private const int BlockSize = 256 * 1024;

    /// <summary>Ouvre un fichier en lecture sans gêner les autres programmes.</summary>
    public static FileStream OpenForRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>Envoie exactement <paramref name="size"/> octets du flux puis leur empreinte.</summary>
    public static async Task SendAsync(FrameChannel channel, FileStream file, long size, Action<long>? onBytes, CancellationToken ct)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BlockSize];
        var remaining = size;
        while (remaining > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) throw new IOException("Le fichier a été tronqué pendant l'envoi.");
            hasher.AppendData(buffer, 0, read);
            await channel.WriteAsync(FrameType.Data, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
            onBytes?.Invoke(read);
        }
        await channel.WriteJsonAsync(FrameType.FileEnd, new FileEndMessage(Convert.ToHexString(hasher.GetHashAndReset())), ct).ConfigureAwait(false);
    }

    /// <summary>Reçoit <paramref name="size"/> octets dans <paramref name="tempPath"/> et vérifie l'empreinte. Supprime le temporaire en cas d'échec.</summary>
    public static async Task ReceiveAsync(FrameChannel channel, string tempPath, long size, Action<long>? onBytes, CancellationToken ct)
    {
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1, FileOptions.Asynchronous))
            {
                long received = 0;
                while (received < size)
                {
                    var frame = await channel.ExpectAsync(FrameType.Data, ct).ConfigureAwait(false);
                    if (frame.Payload.Length == 0 || received + frame.Payload.Length > size)
                        throw new LinkProtocolException("Données reçues incohérentes.");
                    await output.WriteAsync(frame.Payload, ct).ConfigureAwait(false);
                    hasher.AppendData(frame.Payload.Span);
                    received += frame.Payload.Length;
                    onBytes?.Invoke(frame.Payload.Length);
                }
            }

            var end = (await channel.ExpectAsync(FrameType.FileEnd, ct).ConfigureAwait(false)).Json<FileEndMessage>();
            if (!string.Equals(Convert.ToHexString(hasher.GetHashAndReset()), end.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new SyncException("somme de contrôle incorrecte");
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { }
            throw;
        }
    }
}
