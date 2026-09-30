using System.Buffers.Binary;

namespace LanLink.Core.Remote;

// ---- Messages du protocole (JSON)

/// <summary>Demande du visiteur.</summary>
public sealed record RemoteRequest(bool WantControl, bool WantAudio, bool WantClipboard, int MaxWidth, int Fps, int JpegQuality)
{
    public static RemoteRequest ViewOnly { get; } = new(false, false, false, 1600, 15, 60);
}

/// <summary>Ce que l'utilisateur du PC partagé a réellement autorisé.</summary>
public sealed record RemoteGrant(bool Control, bool Audio, bool Clipboard);

public sealed record MonitorInfo(int Index, string Name, int Width, int Height, bool Primary);

public sealed record RemoteReply(bool Accepted, string? Reason, bool Control, bool Audio, bool Clipboard,
    List<MonitorInfo> Monitors, int Monitor);

/// <summary>
/// Événement souris/clavier. <c>X</c>/<c>Y</c> sont normalisés (0..1) dans l'écran affiché.
/// <c>Type</c> : move, down, up, wheel, hwheel, keydown, keyup.
/// </summary>
public sealed record InputEvent(string Type, double X = 0, double Y = 0, string? Button = null, int Delta = 0,
    int Vk = 0, int Scan = 0, bool Extended = false);

/// <summary>Changements de réglages demandés par le visiteur (les champs null sont inchangés).</summary>
public sealed record RemoteSettingsMessage(int? Monitor, int? MaxWidth, int? Fps, int? JpegQuality, bool? Audio);

/// <param name="Kind">"text" ou "image" (PNG en base64).</param>
public sealed record ClipboardData(string Kind, string Data)
{
    public const int MaxBytes = 2 * 1024 * 1024;
}

public sealed record AudioFormatInfo(int SampleRate, int Channels, int BitsPerSample);

public sealed record ScreenRect(int X, int Y, int Width, int Height);

public sealed class RemoteRefusedException : Exception
{
    public RemoteRefusedException(string? reason)
        : base(string.IsNullOrEmpty(reason) ? "Demande refusée par l'autre PC." : $"Demande refusée : {reason}") { }
}

// ---- Image d'écran (trame binaire)

/// <summary>
/// Une image d'écran. En-tête binaire de 14 octets (big-endian) suivi du JPEG :
/// séquence u32, largeur u16, hauteur u16, écran u8, curseur X u16, curseur Y u16 (0..65535), drapeaux u8 (bit 0 : curseur visible).
/// Un JPEG vide signifie « image inchangée, seul le curseur a bougé ».
/// </summary>
public readonly record struct VideoFrame(uint Seq, int Width, int Height, int Monitor, double CursorX, double CursorY,
    bool CursorVisible, ReadOnlyMemory<byte> Jpeg)
{
    public const int HeaderSize = 14;

    public byte[] Encode()
    {
        var buffer = new byte[HeaderSize + Jpeg.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), Seq);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(4, 2), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6, 2), (ushort)Height);
        buffer[8] = (byte)Monitor;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(9, 2), (ushort)Math.Round(Math.Clamp(CursorX, 0, 1) * 65535));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(11, 2), (ushort)Math.Round(Math.Clamp(CursorY, 0, 1) * 65535));
        buffer[13] = (byte)(CursorVisible ? 1 : 0);
        Jpeg.CopyTo(buffer.AsMemory(HeaderSize));
        return buffer;
    }

    /// <summary>Décode une trame ; le JPEG est copié (la mémoire source est réutilisée par le canal).</summary>
    public static VideoFrame Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize) throw new Protocol.LinkProtocolException("Image d'écran tronquée.");
        return new VideoFrame(
            BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2)),
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2)),
            payload[8],
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(9, 2)) / 65535.0,
            BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(11, 2)) / 65535.0,
            (payload[13] & 1) != 0,
            payload[HeaderSize..].ToArray());
    }
}

// ---- Abstractions de la plateforme (implémentées par l'application Windows, simulées dans les tests)

/// <param name="Jpeg">Null si l'image n'a pas changé (le curseur peut quand même avoir bougé).</param>
public sealed record CapturedFrame(byte[]? Jpeg, int Width, int Height, double CursorX, double CursorY, bool CursorVisible);

public interface IScreenCapturer : IDisposable
{
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>Rectangle de l'écran en pixels du bureau virtuel.</summary>
    ScreenRect GetBounds(int monitor);

    /// <summary>Capture l'écran ; renvoie null si rien n'a changé (ni image ni curseur) et que <paramref name="forceFull"/> est faux.</summary>
    CapturedFrame? Capture(int monitor, int maxWidth, int jpegQuality, bool forceFull);
}

public interface IInputInjector : IDisposable
{
    void MoveTo(int x, int y);
    void Button(string button, bool down);
    void Wheel(int delta, bool horizontal);
    void Key(int vk, int scan, bool extended, bool down);

    /// <summary>Relâche toutes les touches et boutons encore enfoncés (fin de session, déconnexion).</summary>
    void ReleaseAll();
}

public interface IAudioCapturer : IDisposable
{
    AudioFormatInfo Format { get; }
    event Action<byte[]>? DataAvailable;
    void Start();
}

public interface IAudioPlayer : IDisposable
{
    void Start(AudioFormatInfo format);
    void Play(ReadOnlyMemory<byte> pcm);
}

public interface IClipboardAccess : IDisposable
{
    /// <summary>Levé quand l'utilisateur local copie quelque chose (pas quand <see cref="Set"/> est appelé).</summary>
    event Action<ClipboardData>? Changed;

    void Set(ClipboardData data);
}

/// <summary>Fabrique des éléments de la plateforme pour partager cet écran.</summary>
public interface IRemoteHostEnvironment
{
    IScreenCapturer CreateScreenCapturer();
    IInputInjector CreateInputInjector();
    IAudioCapturer? CreateAudioCapturer();
    IClipboardAccess? CreateClipboard();
}
