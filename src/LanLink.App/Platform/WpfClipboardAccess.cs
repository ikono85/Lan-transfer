using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LanLink.Core.Remote;

namespace LanLink.App.Platform;

/// <summary>Surveille et modifie le presse-papiers Windows (texte et images), sur le thread de l'interface.</summary>
public sealed class WpfClipboardAccess : IClipboardAccess
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private const int MaxTextChars = 500_000;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private uint _lastSequence;

    public event Action<ClipboardData>? Changed;

    public WpfClipboardAccess(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _lastSequence = GetClipboardSequenceNumber(); // le contenu déjà présent n'est pas envoyé
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Poll();
        _dispatcher.Invoke(_timer.Start);
    }

    private void Poll()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _lastSequence) return;
        _lastSequence = sequence;
        try
        {
            if (System.Windows.Clipboard.ContainsText())
            {
                var text = System.Windows.Clipboard.GetText();
                if (text.Length is > 0 and <= MaxTextChars) Changed?.Invoke(new ClipboardData("text", text));
            }
            else if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } image)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                if (ms.Length <= ClipboardData.MaxBytes) Changed?.Invoke(new ClipboardData("image", Convert.ToBase64String(ms.ToArray())));
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException)
        {
            // Presse-papiers occupé par une autre application : on réessaiera au prochain changement.
        }
    }

    public void Set(ClipboardData data)
    {
        _dispatcher.Invoke(() =>
        {
            try
            {
                if (data.Kind == "text" && data.Data.Length <= MaxTextChars)
                {
                    System.Windows.Clipboard.SetText(data.Data);
                }
                else if (data.Kind == "image")
                {
                    var bytes = Convert.FromBase64String(data.Data);
                    if (bytes.Length > ClipboardData.MaxBytes) return;
                    var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    System.Windows.Clipboard.SetImage(decoder.Frames[0]);
                }
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or FormatException or NotSupportedException)
            {
                // Contenu invalide ou presse-papiers occupé : ignoré.
            }
            _lastSequence = GetClipboardSequenceNumber(); // ne pas renvoyer ce qu'on vient de recevoir
        });
    }

    public void Dispose() => _dispatcher.BeginInvoke(_timer.Stop);
}

/// <summary>Éléments de plateforme pour partager cet écran.</summary>
public sealed class WindowsRemoteEnvironment : IRemoteHostEnvironment
{
    private readonly Dispatcher _dispatcher;

    public WindowsRemoteEnvironment(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public IScreenCapturer CreateScreenCapturer() => new GdiScreenCapturer();

    public IInputInjector CreateInputInjector() => new SendInputInjector();

    public IAudioCapturer? CreateAudioCapturer()
    {
        try { return new LoopbackAudioCapturer(); }
        catch (Exception ex) when (ex is COMException or NotSupportedException or InvalidOperationException) { return null; }
    }

    public IClipboardAccess? CreateClipboard() => new WpfClipboardAccess(_dispatcher);
}
