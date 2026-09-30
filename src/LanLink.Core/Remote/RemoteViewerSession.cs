using System.Threading.Channels;
using LanLink.Core.Net;
using LanLink.Core.Protocol;

namespace LanLink.Core.Remote;

/// <summary>Reçoit ce que diffuse le PC distant (appelé depuis le thread de lecture de la session).</summary>
public interface IRemoteViewerSink
{
    /// <summary>Affiche une image. L'acquittement (qui autorise l'image suivante) part quand cette tâche se termine.</summary>
    ValueTask OnVideoAsync(VideoFrame frame);

    void OnAudioFormat(AudioFormatInfo format);

    /// <summary>Données audio PCM 16 bits ; à copier si elles doivent survivre à l'appel.</summary>
    void OnAudio(ReadOnlyMemory<byte> pcm);

    void OnClipboard(ClipboardData data);

    /// <summary>La session est terminée (arrêt normal : <paramref name="reason"/> est null).</summary>
    void OnEnded(string? reason);
}

/// <summary>Côté visiteur : reçoit l'écran distant et envoie souris, clavier et presse-papiers.</summary>
public sealed class RemoteViewerSession : IAsyncDisposable
{
    private readonly LinkSession _link;
    private readonly FrameChannel _channel;
    private readonly IRemoteViewerSink _sink;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<InputEvent> _input = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleReader = true });
    private Task _readTask = Task.CompletedTask;
    private Task _inputTask = Task.CompletedTask;
    private int _ended;

    public RemoteReply Info { get; }

    /// <summary>Se termine quand la session est finie (dans tous les cas).</summary>
    public Task Completed => _readTask;

    internal RemoteViewerSession(LinkSession link, FrameChannel channel, RemoteReply info, IRemoteViewerSink sink)
    {
        _link = link;
        _channel = channel;
        Info = info;
        _sink = sink;
    }

    internal void Start()
    {
        _readTask = Task.Run(ReadLoopAsync);
        _inputTask = Task.Run(InputLoopAsync);
    }

    /// <summary>Met un événement en file (non bloquant). Les déplacements successifs sont fusionnés.</summary>
    public void SendInput(InputEvent e)
    {
        if (Info.Control) _input.Writer.TryWrite(e);
    }

    public void SendClipboard(ClipboardData data)
    {
        if (!Info.Clipboard) return;
        _ = SafeWriteAsync(FrameType.Clipboard, data);
    }

    public void UpdateSettings(RemoteSettingsMessage settings) => _ = SafeWriteAsync(FrameType.RemoteSettings, settings);

    private async Task SafeWriteAsync<T>(FrameType type, T value)
    {
        try { await _channel.WriteJsonAsync(type, value, _cts.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task ReadLoopAsync()
    {
        string? reason = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await _channel.ReadAsync(_cts.Token).ConfigureAwait(false);
                switch (frame.Type)
                {
                    case FrameType.Video:
                        await _sink.OnVideoAsync(VideoFrame.Decode(frame.Payload.Span)).ConfigureAwait(false);
                        await _channel.WriteAsync(FrameType.VideoAck, ReadOnlyMemory<byte>.Empty, _cts.Token).ConfigureAwait(false);
                        break;
                    case FrameType.Audio:
                        _sink.OnAudio(frame.Payload);
                        break;
                    case FrameType.AudioFormat:
                        _sink.OnAudioFormat(frame.Json<AudioFormatInfo>());
                        break;
                    case FrameType.Clipboard:
                        _sink.OnClipboard(frame.Json<ClipboardData>());
                        break;
                    case FrameType.RemoteEnd:
                        return;
                    default:
                        throw new LinkProtocolException($"Trame inattendue : {frame.Type}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or LinkProtocolException or TimeoutException or ObjectDisposedException)
        {
            reason = _cts.IsCancellationRequested ? null : "Connexion perdue : " + ex.Message;
        }
        finally
        {
            _cts.Cancel();
            _input.Writer.TryComplete();
            if (Interlocked.Exchange(ref _ended, 1) == 0) _sink.OnEnded(reason);
        }
    }

    private async Task InputLoopAsync()
    {
        var reader = _input.Reader;
        var batch = new List<InputEvent>();
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 200 && reader.TryRead(out var e))
                {
                    if (e.Type == "move" && batch.Count > 0 && batch[^1].Type == "move") batch[^1] = e;
                    else batch.Add(e);
                }
                await _channel.WriteJsonAsync(FrameType.Input, batch, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    /// <summary>Demande l'arrêt du partage puis ferme la connexion.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            try { await _channel.WriteAsync(FrameType.RemoteEnd, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        // Laisse un court instant à l'hôte pour répondre (RemoteEnd) avant de couper.
        try { await _readTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        _cts.Cancel();
        await _link.DisposeAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_readTask, _inputTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception) { }
        _cts.Dispose();
    }
}
