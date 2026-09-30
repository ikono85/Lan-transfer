using System.Diagnostics;
using System.Threading.Channels;
using LanLink.Core.Protocol;

namespace LanLink.Core.Remote;

/// <summary>
/// Côté PC partagé : envoie l'écran (JPEG), applique les événements du visiteur, synchronise le
/// presse-papiers et l'audio. Une session par connexion.
/// </summary>
public sealed class RemoteHostSession
{
    private const int MaxInFlight = 2;
    private const int MaxEventsPerFrame = 256;

    private readonly FrameChannel _channel;
    private readonly IRemoteHostEnvironment _env;
    private readonly RemoteRequest _request;
    private readonly RemoteGrant _grant;

    private readonly Channel<(FrameType Type, byte[] Payload)> _outbox =
        Channel.CreateBounded<(FrameType, byte[])>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    private IScreenCapturer _capturer = null!;
    private IInputInjector? _injector;

    // Réglages modifiables par le visiteur
    private volatile int _monitor;
    private volatile int _maxWidth;
    private volatile int _fps;
    private volatile int _quality;
    private volatile bool _audioEnabled;
    private volatile bool _forceFull = true;

    private long _framesSent;
    private long _framesAcked;

    public RemoteHostSession(FrameChannel channel, IRemoteHostEnvironment env, RemoteRequest request, RemoteGrant grant)
    {
        _channel = channel;
        _env = env;
        _request = request;
        _grant = grant;
        _maxWidth = Math.Clamp(request.MaxWidth, 320, 3840);
        _fps = Math.Clamp(request.Fps, 1, 60);
        _quality = Math.Clamp(request.JpegQuality, 20, 95);
        _audioEnabled = grant.Audio;
    }

    /// <summary>Répond à la demande puis diffuse jusqu'à l'arrêt (visiteur, annulation ou déconnexion).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        IAudioCapturer? audio = null;
        IClipboardAccess? clipboard = null;
        try
        {
            _capturer = _env.CreateScreenCapturer();
            var monitors = _capturer.GetMonitors().ToList();
            if (monitors.Count == 0) throw new InvalidOperationException("Aucun écran disponible.");
            _monitor = (monitors.FirstOrDefault(m => m.Primary) ?? monitors[0]).Index;

            _injector = _grant.Control ? _env.CreateInputInjector() : null;
            audio = _grant.Audio ? _env.CreateAudioCapturer() : null;
            clipboard = _grant.Clipboard ? _env.CreateClipboard() : null;
            _clipboardTarget = clipboard;

            await _channel.WriteJsonAsync(FrameType.RemoteReply, new RemoteReply(true, null,
                _injector is not null, audio is not null, clipboard is not null, monitors, _monitor), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
        {
            _capturer?.Dispose();
            await _channel.WriteJsonAsync(FrameType.RemoteReply,
                new RemoteReply(false, ex.Message, false, false, false, new List<MonitorInfo>(), 0), ct).ConfigureAwait(false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var tasks = new List<Task>
        {
            Task.Run(() => ReadLoopAsync(token), token),
            Task.Run(() => VideoLoopAsync(token), token),
            Task.Run(() => OutboxLoopAsync(token), token),
        };

        if (audio is not null)
        {
            await _channel.WriteJsonAsync(FrameType.AudioFormat, audio.Format, token).ConfigureAwait(false);
            audio.DataAvailable += data =>
            {
                if (_audioEnabled) _outbox.Writer.TryWrite((FrameType.Audio, data));
            };
            audio.Start();
        }

        if (clipboard is not null)
        {
            clipboard.Changed += data =>
                _outbox.Writer.TryWrite((FrameType.Clipboard, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data, FrameChannel.JsonOptions)));
        }

        try
        {
            // La session se termine dès qu'une des boucles s'arrête (arrêt demandé, déconnexion, erreur).
            var finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            cts.Cancel();
            try { await finished.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException or IOException or ObjectDisposedException) { }
        }
        finally
        {
            cts.Cancel();
            audio?.Dispose();
            clipboard?.Dispose();
            _injector?.ReleaseAll(); // évite les touches ou boutons restés enfoncés
            _injector?.Dispose();
            _capturer.Dispose();
            try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { /* arrêt : on ignore les erreurs des boucles */ }
        }

        try { await _channel.WriteAsync(FrameType.RemoteEnd, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    // ---- Lecture des messages du visiteur
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await _channel.ReadAsync(ct).ConfigureAwait(false);
            switch (frame.Type)
            {
                case FrameType.Input:
                    ApplyInput(frame.Json<List<InputEvent>>());
                    break;
                case FrameType.VideoAck:
                    Interlocked.Increment(ref _framesAcked);
                    break;
                case FrameType.Clipboard:
                    ApplyClipboard(frame.Json<ClipboardData>());
                    break;
                case FrameType.RemoteSettings:
                    ApplySettings(frame.Json<RemoteSettingsMessage>());
                    break;
                case FrameType.RemoteEnd:
                    return;
                default:
                    throw new LinkProtocolException($"Trame inattendue pendant le partage d'écran : {frame.Type}");
            }
        }
    }

    private void ApplySettings(RemoteSettingsMessage s)
    {
        if (s.Monitor is { } m && _capturer.GetMonitors().Any(x => x.Index == m)) _monitor = m;
        if (s.MaxWidth is { } w) _maxWidth = Math.Clamp(w, 320, 3840);
        if (s.Fps is { } f) _fps = Math.Clamp(f, 1, 60);
        if (s.JpegQuality is { } q) _quality = Math.Clamp(q, 20, 95);
        if (s.Audio is { } a) _audioEnabled = a && _grant.Audio;
        _forceFull = true;
    }

    private void ApplyClipboard(ClipboardData data)
    {
        if (!_grant.Clipboard || !_request.WantClipboard) return;
        if (data.Kind is not ("text" or "image") || data.Data is null || data.Data.Length > ClipboardData.MaxBytes * 4 / 3 + 16) return;
        _clipboardTarget?.Set(data);
    }

    private IClipboardAccess? _clipboardTarget;

    /// <summary>Applique un lot d'événements ; ignoré sans autorisation de contrôle.</summary>
    private void ApplyInput(List<InputEvent> events)
    {
        var injector = _injector;
        if (injector is null || !_grant.Control) return;
        var bounds = _capturer.GetBounds(_monitor);
        foreach (var e in events.Take(MaxEventsPerFrame))
        {
            switch (e.Type)
            {
                case "move":
                    injector.MoveTo(ToPixel(bounds.X, bounds.Width, e.X), ToPixel(bounds.Y, bounds.Height, e.Y));
                    break;
                case "down" or "up":
                    if (e.Button is "left" or "right" or "middle" or "x1" or "x2") injector.Button(e.Button, e.Type == "down");
                    break;
                case "wheel" or "hwheel":
                    injector.Wheel(Math.Clamp(e.Delta, -1200, 1200), e.Type == "hwheel");
                    break;
                case "keydown" or "keyup":
                    if (e.Vk is > 0 and < 255) injector.Key(e.Vk, e.Scan, e.Extended, e.Type == "keydown");
                    break;
            }
        }
    }

    private static int ToPixel(int origin, int size, double normalized) =>
        origin + (int)Math.Round(Math.Clamp(normalized, 0, 1) * Math.Max(size - 1, 0));

    // ---- Écriture : vidéo, puis audio et presse-papiers via la boîte d'envoi
    private async Task VideoLoopAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        uint seq = 0;
        while (!ct.IsCancellationRequested)
        {
            var frameStart = clock.Elapsed;

            // Contrôle de flux : au plus MaxInFlight images non acquittées.
            if (Interlocked.Read(ref _framesSent) - Interlocked.Read(ref _framesAcked) >= MaxInFlight)
            {
                await Task.Delay(4, ct).ConfigureAwait(false);
                continue;
            }

            var force = _forceFull;
            _forceFull = false;
            var monitor = _monitor;
            var captured = await Task.Run(() => _capturer.Capture(monitor, _maxWidth, _quality, force), ct).ConfigureAwait(false);

            if (captured is not null)
            {
                var frame = new VideoFrame(seq++, captured.Width, captured.Height, monitor, captured.CursorX, captured.CursorY,
                    captured.CursorVisible, captured.Jpeg ?? ReadOnlyMemory<byte>.Empty);
                Interlocked.Increment(ref _framesSent);
                await _channel.WriteAsync(FrameType.Video, frame.Encode(), ct).ConfigureAwait(false);
            }

            var wait = TimeSpan.FromSeconds(1.0 / _fps) - (clock.Elapsed - frameStart);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    private async Task OutboxLoopAsync(CancellationToken ct)
    {
        await foreach (var (type, payload) in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            await _channel.WriteAsync(type, payload, ct).ConfigureAwait(false);
    }
}
