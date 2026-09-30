using System.Windows;
using System.Windows.Threading;
using LanLink.App.Services;
using LanLink.App.ViewModels;
using LanLink.Core;
using LanLink.Core.Protocol;

namespace LanLink.App.Views;

public partial class IncomingDialog : Window
{
    public IncomingDialog(RemotePeer peer, TransferOffer offer)
    {
        InitializeComponent();
        TitleText.Text = $"{peer.DeviceName} veut vous envoyer :";
        DetailText.Text = $"Adresse {peer.Address} · authentifié par mot de passe";
        NameText.Text = offer.Kind == "folder" ? $"📁 {offer.Name}" : offer.Name;
        SizeText.Text = offer.Kind == "folder"
            ? $"{offer.Files.Count} fichier(s) · {HistoryItem.FormatSize(offer.TotalSize)}"
            : HistoryItem.FormatSize(offer.TotalSize);

        // L'expéditeur abandonne au bout de 5 minutes : inutile de laisser la fenêtre ouverte plus longtemps.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
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
