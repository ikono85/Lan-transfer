using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LanLink.App.Platform;
using LanLink.App.Services;
using LanLink.Core.Remote;

namespace LanLink.App.Views;

/// <summary>Affiche l'écran d'un autre PC et transmet souris, clavier, presse-papiers et audio.</summary>
public partial class RemoteViewerWindow : Window, IRemoteViewerSink
{
    private static readonly (string Name, int Width, int Fps, int Quality)[] QualityPresets =
    {
        ("Économie", 960, 10, 45),
        ("Équilibré", 1600, 15, 60),
        ("Netteté", 2560, 20, 78),
    };

    private static readonly HashSet<Key> ExtendedKeys = new()
    {
        Key.Insert, Key.Delete, Key.Home, Key.End, Key.Prior, Key.Next, Key.Up, Key.Down, Key.Left, Key.Right,
        Key.NumLock, Key.RightCtrl, Key.RightAlt, Key.LWin, Key.RWin, Key.Divide, Key.Apps,
    };

    private readonly Action<string?> _onEnded;
    private readonly HashSet<Key> _keysDown = new();
    private RemoteViewerSession? _session;
    private IClipboardAccess? _clipboard;
    private IAudioPlayer? _player;
    private int _frameWidth, _frameHeight;
    private int _quality = 1;
    private bool _control, _keyboard = true, _audio;
    private bool _ended;

    /// <param name="onEnded">Appelé quand la session se termine (raison null si arrêt normal).</param>
    public RemoteViewerWindow(string deviceName, Action<string?> onEnded)
    {
        InitializeComponent();
        Title = $"Écran distant — {deviceName}";
        _onEnded = onEnded;
        Deactivated += (_, _) => ReleaseKeys();
        Loaded += (_, _) => Surface.Focus();
    }

    public bool HasEnded => _ended;

    /// <summary>Lie la fenêtre à la session une fois la demande acceptée.</summary>
    public void Attach(RemoteViewerSession session)
    {
        _session = session;
        var info = session.Info;
        _control = info.Control;
        _audio = info.Audio;

        ModeText.Text = info.Control ? "Contrôle actif" : "Vue seule";
        if (info.Monitors.Count > 1)
        {
            foreach (var m in info.Monitors)
            {
                var button = new Button { Content = $"🖥 {m.Index + 1}", Focusable = false, Margin = new Thickness(0, 0, 4, 0), ToolTip = m.Name };
                var index = m.Index;
                button.Click += (_, _) => session.UpdateSettings(new RemoteSettingsMessage(index, null, null, null, null));
                MonitorsPanel.Children.Add(button);
            }
        }

        KeyboardButton.Visibility = info.Control ? Visibility.Visible : Visibility.Collapsed;
        AudioButton.Visibility = info.Audio ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();

        if (info.Clipboard)
        {
            _clipboard = new WpfClipboardAccess(Dispatcher);
            _clipboard.Changed += data => _session?.SendClipboard(data);
        }
    }

    private void UpdateButtons()
    {
        KeyboardButton.Content = _keyboard ? "⌨ Clavier : oui" : "⌨ Clavier : non";
        AudioButton.Content = _audio ? "🔊 Son : oui" : "🔇 Son : non";
        QualityButton.Content = $"✨ {QualityPresets[_quality].Name}";
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    // ---- IRemoteViewerSink (appelé depuis le thread réseau)
    public async ValueTask OnVideoAsync(VideoFrame frame)
    {
        BitmapSource? image = null;
        if (!frame.Jpeg.IsEmpty)
        {
            using var stream = new MemoryStream(frame.Jpeg.ToArray());
            image = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            image.Freeze();
        }

        try { await Dispatcher.InvokeAsync(() => Render(frame, image)); }
        catch (TaskCanceledException) { /* fenêtre fermée pendant l'affichage */ }
    }

    private void Render(VideoFrame frame, BitmapSource? image)
    {
        if (image is not null)
        {
            Screen.Source = image;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
            Waiting.Visibility = Visibility.Collapsed;
        }

        var rect = ImageRect();
        if (frame.CursorVisible && !rect.IsEmpty)
        {
            Canvas.SetLeft(CursorShape, rect.X + frame.CursorX * rect.Width);
            Canvas.SetTop(CursorShape, rect.Y + frame.CursorY * rect.Height);
            CursorShape.Visibility = Visibility.Visible;
        }
        else
        {
            CursorShape.Visibility = Visibility.Collapsed;
        }
    }

    public void OnAudioFormat(AudioFormatInfo format)
    {
        _player?.Dispose();
        var player = new WaveOutAudioPlayer();
        player.Start(format);
        _player = player;
    }

    public void OnAudio(ReadOnlyMemory<byte> pcm)
    {
        if (_audio) _player?.Play(pcm);
    }

    public void OnClipboard(ClipboardData data) => _clipboard?.Set(data);

    public void OnEnded(string? reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_ended) return;
            _ended = true;
            _onEnded(reason);
            Close();
        });
    }

    // ---- Coordonnées
    /// <summary>Zone occupée par l'image dans la surface (l'image est centrée, proportions conservées).</summary>
    private Rect ImageRect()
    {
        if (_frameWidth <= 0 || _frameHeight <= 0) return Rect.Empty;
        double sw = Surface.ActualWidth, sh = Surface.ActualHeight;
        var scale = Math.Min(sw / _frameWidth, sh / _frameHeight);
        double w = _frameWidth * scale, h = _frameHeight * scale;
        return new Rect((sw - w) / 2, (sh - h) / 2, w, h);
    }

    private bool TryNormalize(MouseEventArgs e, out double x, out double y)
    {
        x = y = 0;
        var rect = ImageRect();
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return false;
        var p = e.GetPosition(Surface);
        x = (p.X - rect.X) / rect.Width;
        y = (p.Y - rect.Y) / rect.Height;
        // Pendant un glisser (souris capturée), on borne au bord de l'image ; sinon on ignore l'extérieur.
        if (Surface.IsMouseCaptured)
        {
            x = Math.Clamp(x, 0, 1);
            y = Math.Clamp(y, 0, 1);
            return true;
        }
        return x is >= 0 and <= 1 && y is >= 0 and <= 1;
    }

    // ---- Souris
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_control && TryNormalize(e, out var x, out var y)) _session?.SendInput(new InputEvent("move", x, y));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Surface.Focus();
        if (!_control || !TryNormalize(e, out var x, out var y) || ButtonName(e.ChangedButton) is not { } name) return;
        Surface.CaptureMouse();
        _session?.SendInput(new InputEvent("move", x, y));
        _session?.SendInput(new InputEvent("down", x, y, name));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_control || ButtonName(e.ChangedButton) is not { } name) return;
        if (TryNormalize(e, out var x, out var y)) _session?.SendInput(new InputEvent("move", x, y));
        _session?.SendInput(new InputEvent("up", Button: name));
        if (Mouse.LeftButton == MouseButtonState.Released && Mouse.RightButton == MouseButtonState.Released
            && Mouse.MiddleButton == MouseButtonState.Released)
            Surface.ReleaseMouseCapture();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_control) _session?.SendInput(new InputEvent("wheel", Delta: e.Delta));
    }

    private static string? ButtonName(MouseButton button) => button switch
    {
        MouseButton.Left => "left",
        MouseButton.Right => "right",
        MouseButton.Middle => "middle",
        MouseButton.XButton1 => "x1",
        MouseButton.XButton2 => "x2",
        _ => null,
    };

    // ---- Clavier
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (TrySendKey(e, down: true)) e.Handled = true;
        else base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (TrySendKey(e, down: false)) e.Handled = true;
        else base.OnPreviewKeyUp(e);
    }

    private bool TrySendKey(KeyEventArgs e, bool down)
    {
        if (!_control || !_keyboard || _session is null) return false;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0 || vk >= 255) return false;

        if (down) _keysDown.Add(key); else _keysDown.Remove(key);
        _session.SendInput(new InputEvent(down ? "keydown" : "keyup", Vk: vk, Extended: ExtendedKeys.Contains(key)));
        return true;
    }

    /// <summary>Relâche côté distant les touches encore enfoncées (perte de focus, fermeture).</summary>
    private void ReleaseKeys()
    {
        if (_session is null) return;
        foreach (var key in _keysDown.ToList())
            _session.SendInput(new InputEvent("keyup", Vk: KeyInterop.VirtualKeyFromKey(key), Extended: ExtendedKeys.Contains(key)));
        _keysDown.Clear();
    }

    // ---- Barre d'outils
    private void OnKeyboardToggle(object sender, RoutedEventArgs e)
    {
        if (_keyboard) ReleaseKeys();
        _keyboard = !_keyboard;
        UpdateButtons();
    }

    private void OnAudioToggle(object sender, RoutedEventArgs e)
    {
        _audio = !_audio;
        _session?.UpdateSettings(new RemoteSettingsMessage(null, null, null, null, _audio));
        UpdateButtons();
    }

    private void OnQualityCycle(object sender, RoutedEventArgs e)
    {
        _quality = (_quality + 1) % QualityPresets.Length;
        var (_, width, fps, quality) = QualityPresets[_quality];
        _session?.UpdateSettings(new RemoteSettingsMessage(null, width, fps, quality, null));
        UpdateButtons();
    }

    private void OnFullscreenToggle(object sender, RoutedEventArgs e)
    {
        var full = WindowStyle != WindowStyle.None;
        WindowStyle = full ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = full ? WindowState.Maximized : WindowState.Normal;
        FullscreenButton.Content = full ? "⛶ Quitter le plein écran" : "⛶ Plein écran";
    }

    private void OnStop(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ReleaseKeys();
        _clipboard?.Dispose();
        _player?.Dispose();
        var session = _session;
        _session = null;
        if (session is not null) _ = session.DisposeAsync().AsTask();
        if (!_ended)
        {
            _ended = true;
            _onEnded(null);
        }
    }
}
