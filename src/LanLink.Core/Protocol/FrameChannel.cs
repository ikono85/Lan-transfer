using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;

namespace LanLink.Core.Protocol;

public enum FrameType : byte
{
    Hello = 0x01,
    ClientAuth = 0x02,
    ServerAuth = 0x03,

    Offer = 0x10,
    OfferReply = 0x11,
    FileStart = 0x12,
    ResumeInfo = 0x13,
    ResumeAck = 0x14,
    Data = 0x15,
    FileEnd = 0x16,
    FileResult = 0x17,

    Chat = 0x20,
    Ack = 0x21,

    RemoteRequest = 0x30,
    RemoteReply = 0x31,
    Video = 0x32,
    Input = 0x33,
    Clipboard = 0x34,
    RemoteSettings = 0x35,
    VideoAck = 0x36,
    Audio = 0x37,
    AudioFormat = 0x38,
    RemoteEnd = 0x39,

    SyncPairRequest = 0x40,
    SyncPairReply = 0x41,
    SyncOpen = 0x42,
    SyncOpenReply = 0x43,
    SyncListRequest = 0x44,
    SyncListing = 0x45,
    SyncGet = 0x46,
    SyncFileInfo = 0x47,
    SyncPut = 0x48,
    SyncPutReply = 0x49,
    SyncDelete = 0x4A,
    SyncDone = 0x4B,

    Error = 0x7F,
}

/// <summary>Une trame lue. <see cref="Payload"/> n'est valide que jusqu'à la lecture suivante.</summary>
public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload)
{
    public T Json<T>() => JsonSerializer.Deserialize<T>(Payload.Span, FrameChannel.JsonOptions)
                          ?? throw new LinkProtocolException("Message vide ou invalide.");
}

public class LinkProtocolException : Exception
{
    public LinkProtocolException(string message) : base(message) { }
    public LinkProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Trames [type : 1 octet][longueur : uint32 big-endian][charge utile] au-dessus d'un flux (TLS).
/// Un seul lecteur et un seul écrivain à la fois.
/// </summary>
public sealed class FrameChannel
{
    public const int MaxPayload = 4 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Stream _stream;
    private readonly byte[] _header = new byte[5];
    private byte[] _readBuffer = new byte[64 * 1024];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Délai d'inactivité toléré pour une lecture (null = illimité).</summary>
    public TimeSpan? ReadTimeout { get; set; }

    public FrameChannel(Stream stream) => _stream = stream;

    public async ValueTask WriteAsync(FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxPayload) throw new LinkProtocolException("Trame trop volumineuse.");
        var total = 5 + payload.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        // Plusieurs tâches peuvent écrire (vidéo, audio, presse-papiers) : une trame à la fois.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            buffer[0] = (byte)type;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1, 4), (uint)payload.Length);
            payload.CopyTo(buffer.AsMemory(5));
            await _stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public ValueTask WriteJsonAsync<T>(FrameType type, T value, CancellationToken ct = default) =>
        WriteAsync(type, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), ct);

    public async ValueTask<Frame> ReadAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (ReadTimeout is { } timeout) cts.CancelAfter(timeout);
        try
        {
            await ReadExactAsync(_header, 5, cts.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(1, 4));
            if (length > MaxPayload) throw new LinkProtocolException($"Trame trop volumineuse ({length} octets).");
            if (_readBuffer.Length < length) _readBuffer = new byte[Math.Max(length, _readBuffer.Length * 2)];
            await ReadExactAsync(_readBuffer, (int)length, cts.Token).ConfigureAwait(false);
            return new Frame((FrameType)_header[0], _readBuffer.AsMemory(0, (int)length));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Le correspondant ne répond plus.");
        }
    }

    /// <summary>Lit une trame du type attendu ; lève <see cref="LinkProtocolException"/> sinon (ou si le pair a signalé une erreur).</summary>
    public async ValueTask<Frame> ExpectAsync(FrameType expected, CancellationToken ct = default)
    {
        var frame = await ReadAsync(ct).ConfigureAwait(false);
        if (frame.Type == expected) return frame;
        if (frame.Type == FrameType.Error)
            throw new LinkProtocolException("Erreur du correspondant : " + System.Text.Encoding.UTF8.GetString(frame.Payload.Span));
        throw new LinkProtocolException($"Trame inattendue : {frame.Type} (attendu : {expected}).");
    }

    private async ValueTask ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Connexion fermée par le correspondant.");
            read += n;
        }
    }
}
