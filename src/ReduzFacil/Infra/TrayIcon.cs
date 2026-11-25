using ReduzFacil.Core.Util;
using Forms = System.Windows.Forms;

namespace ReduzFacil.Infra;

/// <summary>Ícone perto do relógio (bandeja), usado quando a pasta vigiada continua rodando com a janela fechada.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(App app)
    {
        _icon = new Forms.NotifyIcon
        {
            Text = AppInfo.Name,
            Visible = false,
        };
        try
        {
            _icon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(AppInfo.ExePath) ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _icon.Icon = System.Drawing.SystemIcons.Application;
        }

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir o Reduz Fácil", null, (_, _) => app.ShowWindow());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => app.ExitApp());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => app.ShowWindow();
        _icon.BalloonTipClicked += (_, _) => app.ShowWindow();
    }

    public void Update(bool visible)
    {
        try { _icon.Visible = visible; } catch (Exception ex) { Log.Warn("Bandeja: " + ex.Message); }
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            if (!_icon.Visible) return;
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            Log.Warn("Aviso da bandeja: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
