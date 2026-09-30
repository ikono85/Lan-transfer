using System.Windows;
using System.Windows.Threading;
using LanLink.App.Services;
using LanLink.Core;
using LanLink.Core.Security;
using LanLink.Core.Sync;

namespace LanLink.App.Views;

/// <summary>Un autre PC propose de synchroniser un dossier : l'utilisateur choisit le dossier de ce PC.</summary>
public partial class SyncPairDialog : Window
{
    public string ChosenFolder { get; private set; } = "";

    public SyncPairDialog(RemotePeer peer, SyncPairRequest request)
    {
        InitializeComponent();
        TitleText.Text = $"{peer.DeviceName} propose de synchroniser « {request.Name} »";
        DetailText.Text = $"Adresse {peer.Address} · authentifié par mot de passe";

        string suggestion;
        try { suggestion = SafePath.FileName(request.Name); }
        catch (ArgumentException) { suggestion = "Synchronisation"; }
        FolderBox.Text = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LanLink", suggestion);

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

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier à synchroniser" };
        if (dialog.ShowDialog() == true) FolderBox.Text = dialog.FolderName;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(FolderBox.Text.Trim());
            if (string.Equals(System.IO.Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
            {
                ErrorText.Text = "Choisis un dossier, pas la racine d'un disque.";
                return;
            }
            System.IO.Directory.CreateDirectory(full);
            ChosenFolder = full;
            DialogResult = true;
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ErrorText.Text = "Dossier inutilisable : " + ex.Message;
        }
    }

    private void OnRefuse(object sender, RoutedEventArgs e) => DialogResult = false;
}
