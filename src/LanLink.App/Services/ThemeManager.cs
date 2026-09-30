using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace LanLink.App.Services;

/// <summary>Applique le thème sombre/clair (ou celui de Windows) et suit ses changements.</summary>
public static class ThemeManager
{
    private const int DwmUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static string _mode = "system";

    public static bool IsDark { get; private set; } = true;

    public static void Initialize(string mode)
    {
        SystemEvents.UserPreferenceChanged += (_, _) =>
        {
            if (_mode == "system") System.Windows.Application.Current.Dispatcher.Invoke(() => Apply(_mode));
        };
        Apply(mode);
    }

    public static void Apply(string mode)
    {
        _mode = mode;
        IsDark = mode switch
        {
            "dark" => true,
            "light" => false,
            _ => !SystemUsesLightTheme(),
        };

        var app = System.Windows.Application.Current;
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/LanLink;component/Themes/{(IsDark ? "Dark" : "Light")}.xaml"),
        };
        app.Resources.MergedDictionaries[0] = dictionary;

        foreach (Window window in app.Windows) ApplyTitleBar(window);
    }

    /// <summary>Barre de titre sombre (Windows 10 2004+ / Windows 11).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
