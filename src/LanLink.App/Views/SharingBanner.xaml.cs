using System.Windows;
using LanLink.Core;
using LanLink.Core.Remote;

namespace LanLink.App.Views;

/// <summary>Barre orange toujours visible pendant que l'écran est partagé, avec arrêt immédiat.</summary>
public partial class SharingBanner : Window
{
    private readonly Action _stop;

    public SharingBanner(RemotePeer peer, RemoteGrant grant, Action stop)
    {
        InitializeComponent();
        _stop = stop;
        MessageText.Text = $"🖥 Votre écran est partagé avec {peer.DeviceName} — {(grant.Control ? "contrôle actif" : "vue seule")}";
        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
            Top = SystemParameters.WorkArea.Top + 8;
        };
    }

    private void OnStop(object sender, RoutedEventArgs e) => _stop();
}
