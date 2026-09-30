namespace LanLink.App.Services;

/// <summary>Icône de la zone de notification : l'application reste active fenêtre fermée.</summary>
public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayService(Action show, Action quit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Ouvrir LanLink", null, (_, _) => show());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => quit());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "LanLink",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) show();
        };
        _icon.BalloonTipClicked += (_, _) => show();
    }

    public void Notify(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text.Length > 200 ? text[..200] + "…" : text;
        _icon.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
