using System.Windows;
using System.Windows.Threading;
using LanLink.App.Services;
using LanLink.Core;
using LanLink.Core.Remote;

namespace LanLink.App.Views;

public partial class RemoteRequestDialog : Window
{
    public RemoteGrant Grant => new(ControlBox.IsChecked == true, AudioBox.IsChecked == true, ClipboardBox.IsChecked == true);

    public RemoteRequestDialog(RemotePeer peer, RemoteRequest request)
    {
        InitializeComponent();
        TitleText.Text = request.WantControl
            ? $"{peer.DeviceName} veut voir et contrôler votre écran"
            : $"{peer.DeviceName} veut voir votre écran";
        DetailText.Text = $"Adresse {peer.Address} · authentifié par mot de passe";

        ControlBox.IsEnabled = request.WantControl;
        ControlBox.IsChecked = request.WantControl;
        ClipboardBox.IsEnabled = request.WantClipboard;
        ClipboardBox.IsChecked = request.WantClipboard;
        AudioBox.IsEnabled = request.WantAudio;
        AudioBox.IsChecked = request.WantAudio;
        WarningText.Text = request.WantControl
            ? "Avec le contrôle, cette personne peut ouvrir et modifier tout ce à quoi vous avez accès. N'acceptez que si vous la connaissez."
            : "Cette personne verra tout ce qui s'affiche sur vos écrans.";

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DialogResult = false;
        };
        timer.Start();
        Closed += (_, _) => timer.Stop();
        Loaded += (_, _) => Activate();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnRefuse(object sender, RoutedEventArgs e) => DialogResult = false;
}
