using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LanLink.App.Services;
using LanLink.App.ViewModels;

namespace LanLink.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _allowClose;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        vm.ClearPasswordFields = () =>
        {
            NewPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
        };
        (vm.Theme switch { "dark" => ThemeDark, "light" => ThemeLight, _ => ThemeSystem }).IsChecked = true;
        Loaded += (_, _) => vm.ShowSettingsIfNoPassword();
    }

    /// <summary>Fermer la fenêtre la masque dans la zone de notification ; seul « Quitter » ferme vraiment.</summary>
    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ThemeManager.ApplyTitleBar(this);

    private void OnTargetPasswordChanged(object sender, RoutedEventArgs e) => _vm.TargetPassword = TargetPasswordBox.Password;
    private void OnNewPasswordChanged(object sender, RoutedEventArgs e) => _vm.NewPassword = NewPasswordBox.Password;
    private void OnConfirmPasswordChanged(object sender, RoutedEventArgs e) => _vm.ConfirmPassword = ConfirmPasswordBox.Password;

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string mode }) _vm.SetThemeCommand.Execute(mode);
    }

    private void OnChatKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SendChatCommand.CanExecute(null)) _vm.SendChatCommand.Execute(null);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) _vm.AddPaths(paths);
    }
}
