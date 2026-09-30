using System.Collections.Concurrent;
using LanLink.Core.Net;
using LanLink.Core.Remote;
using Xunit;

namespace LanLink.Core.Tests;

internal sealed class FakeCapturer : IScreenCapturer
{
    private readonly object _lock = new();
    private int _counter;

    public IReadOnlyList<MonitorInfo> GetMonitors() => new[]
    {
        new MonitorInfo(0, "Écran 1", 1920, 1080, true),
        new MonitorInfo(1, "Écran 2", 800, 600, false),
    };

    public ScreenRect GetBounds(int monitor) => monitor == 0 ? new(0, 0, 1920, 1080) : new(1920, 0, 800, 600);

    public CapturedFrame? Capture(int monitor, int maxWidth, int jpegQuality, bool forceFull)
    {
        lock (_lock)
        {
            _counter++;
            var bounds = GetBounds(monitor);
            // Une image sur trois est « inchangée » : seul le curseur est envoyé.
            var jpeg = !forceFull && _counter % 3 == 0 ? Array.Empty<byte>() : new byte[] { 0xFF, 0xD8, (byte)_counter };
            return new CapturedFrame(jpeg, bounds.Width / 2, bounds.Height / 2, 0.5, 0.25, true);
        }
    }

    public void Dispose() { }
}

internal sealed class FakeInjector : IInputInjector
{
    public ConcurrentQueue<string> Events { get; } = new();
    public volatile bool Released;
    public volatile bool Disposed;

    public void MoveTo(int x, int y) => Events.Enqueue($"move:{x},{y}");
    public void Button(string button, bool down) => Events.Enqueue($"btn:{button}:{(down ? "down" : "up")}");
    public void Wheel(int delta, bool horizontal) => Events.Enqueue($"wheel:{delta}:{horizontal}");
    public void Key(int vk, int scan, bool extended, bool down) => Events.Enqueue($"key:{vk}:{(down ? "down" : "up")}");
    public void ReleaseAll() => Released = true;
    public void Dispose() => Disposed = true;
}

internal sealed class FakeClipboard : IClipboardAccess
{
    public event Action<ClipboardData>? Changed;
    public ConcurrentQueue<ClipboardData> SetCalls { get; } = new();
    public void Set(ClipboardData data) => SetCalls.Enqueue(data);
    public void RaiseChanged(ClipboardData data) => Changed?.Invoke(data);
    public void Dispose() { }
}

internal sealed class FakeEnvironment : IRemoteHostEnvironment
{
    public FakeCapturer Capturer { get; } = new();
    public FakeInjector? Injector { get; private set; }
    public FakeClipboard? Clipboard { get; private set; }

    public IScreenCapturer CreateScreenCapturer() => Capturer;
    public IInputInjector CreateInputInjector() => Injector = new FakeInjector();
    public IAudioCapturer? CreateAudioCapturer() => null;
    public IClipboardAccess? CreateClipboard() => Clipboard = new FakeClipboard();
}

internal sealed class CollectingSink : IRemoteViewerSink
{
    public ConcurrentQueue<VideoFrame> Frames { get; } = new();
    public ConcurrentQueue<ClipboardData> Clipboards { get; } = new();
    public TaskCompletionSource<string?> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask OnVideoAsync(VideoFrame frame)
    {
        Frames.Enqueue(frame);
        return ValueTask.CompletedTask;
    }

    public void OnAudioFormat(AudioFormatInfo format) { }
    public void OnAudio(ReadOnlyMemory<byte> pcm) { }
    public void OnClipboard(ClipboardData data) => Clipboards.Enqueue(data);
    public void OnEnded(string? reason) => Ended.TrySetResult(reason);
}

public class VideoFrameTests
{
    [Fact]
    public void RoundTrip()
    {
        var frame = new VideoFrame(7, 960, 540, 1, 0.25, 0.75, true, new byte[] { 1, 2, 3 });
        var decoded = VideoFrame.Decode(frame.Encode());
        Assert.Equal(7u, decoded.Seq);
        Assert.Equal(960, decoded.Width);
        Assert.Equal(540, decoded.Height);
        Assert.Equal(1, decoded.Monitor);
        Assert.Equal(0.25, decoded.CursorX, 3);
        Assert.Equal(0.75, decoded.CursorY, 3);
        Assert.True(decoded.CursorVisible);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Jpeg.ToArray());
    }

    [Fact]
    public void Truncated_IsRejected() =>
        Assert.ThrowsAny<Exception>(() => VideoFrame.Decode(new byte[5]));
}

public class RemoteTests : IClassFixture<EndToEndFixture>
{
    private readonly EndToEndFixture _f;

    public RemoteTests(EndToEndFixture fixture) => _f = fixture;

    private static readonly RemoteRequest Full = new(true, false, true, 1280, 60, 60);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "Délai dépassé : " + what);
    }

    private FakeEnvironment Setup(RemoteGrant? grant)
    {
        var env = new FakeEnvironment();
        _f.Host.Env = env;
        _f.Host.Grant = grant;
        return env;
    }

    [Fact]
    public async Task ViewOnly_StreamsFramesWithFlowControl()
    {
        var env = Setup(new RemoteGrant(false, false, false));
        var sink = new CollectingSink();
        await using var link = await _f.ConnectAsync();
        await using var viewer = await link.StartRemoteAsync(RemoteRequest.ViewOnly with { Fps = 60 }, sink);

        Assert.Equal(2, viewer.Info.Monitors.Count);
        Assert.False(viewer.Info.Control);
        await WaitUntil(() => sink.Frames.Count >= 6, "6 images");

        var seqs = sink.Frames.Select(f => f.Seq).ToArray();
        Assert.Equal(seqs.Order().ToArray(), seqs);
        Assert.Contains(sink.Frames, f => f.Jpeg.Length == 0); // image inchangée : curseur seul
        Assert.Contains(sink.Frames, f => f.Jpeg.Length > 0);
        Assert.Null(env.Injector); // aucun contrôle accordé : pas d'injecteur créé
    }

    [Fact]
    public async Task Control_AppliesInputToSelectedMonitor()
    {
        var env = Setup(new RemoteGrant(true, false, false));
        var sink = new CollectingSink();
        await using var link = await _f.ConnectAsync();
        await using var viewer = await link.StartRemoteAsync(Full, sink);
        Assert.True(viewer.Info.Control);

        viewer.SendInput(new InputEvent("move", 0, 1));
        viewer.SendInput(new InputEvent("down", Button: "left"));
        viewer.SendInput(new InputEvent("up", Button: "left"));
        viewer.SendInput(new InputEvent("keydown", Vk: 65));
        viewer.SendInput(new InputEvent("keyup", Vk: 65));
        await WaitUntil(() => env.Injector is { } i && i.Events.Count >= 5, "événements d'entrée");
        Assert.Equal(new[] { "move:0,1079", "btn:left:down", "btn:left:up", "key:65:down", "key:65:up" }, env.Injector!.Events.ToArray());

        viewer.UpdateSettings(new RemoteSettingsMessage(1, null, null, null, null));
        await WaitUntil(() => sink.Frames.Any(f => f.Monitor == 1), "image du 2e écran");
        viewer.SendInput(new InputEvent("move", 0, 0));
        await WaitUntil(() => env.Injector.Events.Contains("move:1920,0"), "déplacement sur le 2e écran");
    }

    [Fact]
    public async Task InvalidInput_IsIgnored()
    {
        var env = Setup(new RemoteGrant(true, false, false));
        await using var link = await _f.ConnectAsync();
        await using var viewer = await link.StartRemoteAsync(Full, new CollectingSink());

        viewer.SendInput(new InputEvent("down", Button: "rm -rf"));
        viewer.SendInput(new InputEvent("keydown", Vk: 999));
        viewer.SendInput(new InputEvent("wheel", Delta: 999999));
        viewer.SendInput(new InputEvent("nonsense"));
        await WaitUntil(() => env.Injector!.Events.Count >= 1, "molette bornée");
        await Task.Delay(150);
        Assert.Equal(new[] { "wheel:1200:False" }, env.Injector!.Events.ToArray());
    }

    [Fact]
    public async Task HostCanDowngradeToViewOnly()
    {
        var env = Setup(new RemoteGrant(false, false, false)); // le visiteur demandait le contrôle
        await using var link = await _f.ConnectAsync();
        await using var viewer = await link.StartRemoteAsync(Full, new CollectingSink());
        Assert.False(viewer.Info.Control);
        viewer.SendInput(new InputEvent("down", Button: "left")); // sans effet
        await Task.Delay(150);
        Assert.Null(env.Injector);
    }

    [Fact]
    public async Task EndingSession_ReleasesPressedKeys()
    {
        var env = Setup(new RemoteGrant(true, false, false));
        var link = await _f.ConnectAsync();
        var viewer = await link.StartRemoteAsync(Full, new CollectingSink());
        viewer.SendInput(new InputEvent("keydown", Vk: 16)); // Maj enfoncée, jamais relâchée
        await WaitUntil(() => env.Injector!.Events.Count >= 1, "touche enfoncée");

        await viewer.DisposeAsync();
        await WaitUntil(() => env.Injector!.Released && env.Injector.Disposed, "relâchement des touches");
    }

    [Fact]
    public async Task ConnectionLoss_ReleasesPressedKeys()
    {
        var env = Setup(new RemoteGrant(true, false, false));
        var link = await _f.ConnectAsync();
        var viewer = await link.StartRemoteAsync(Full, new CollectingSink());
        viewer.SendInput(new InputEvent("keydown", Vk: 17));
        await WaitUntil(() => env.Injector!.Events.Count >= 1, "touche enfoncée");

        await link.DisposeAsync(); // coupure brutale, sans RemoteEnd
        await WaitUntil(() => env.Injector!.Released, "relâchement après coupure");
    }

    [Fact]
    public async Task Clipboard_SyncsBothWays()
    {
        var env = Setup(new RemoteGrant(false, false, true));
        var sink = new CollectingSink();
        await using var link = await _f.ConnectAsync();
        await using var viewer = await link.StartRemoteAsync(Full, sink);
        Assert.True(viewer.Info.Clipboard);

        viewer.SendClipboard(new ClipboardData("text", "du visiteur"));
        await WaitUntil(() => env.Clipboard!.SetCalls.Count == 1, "presse-papiers hôte");
        Assert.Equal("du visiteur", env.Clipboard!.SetCalls.Single().Data);

        env.Clipboard.RaiseChanged(new ClipboardData("text", "de l'hôte"));
        await WaitUntil(() => sink.Clipboards.Count == 1, "presse-papiers visiteur");
        Assert.Equal("de l'hôte", sink.Clipboards.Single().Data);
    }

    [Fact]
    public async Task Refused_ThrowsRefused()
    {
        Setup(null);
        await using var link = await _f.ConnectAsync();
        await Assert.ThrowsAsync<RemoteRefusedException>(() => link.StartRemoteAsync(Full, new CollectingSink()));
    }

    [Fact]
    public async Task SecondSession_IsRefusedWhileFirstIsActive()
    {
        Setup(new RemoteGrant(false, false, false));
        await using var first = await _f.ConnectAsync();
        await using var viewer = await first.StartRemoteAsync(RemoteRequest.ViewOnly, new CollectingSink());

        await using var second = await _f.ConnectAsync();
        var ex = await Assert.ThrowsAsync<RemoteRefusedException>(() => second.StartRemoteAsync(RemoteRequest.ViewOnly, new CollectingSink()));
        Assert.Contains("déjà en cours", ex.Message);
    }
}
