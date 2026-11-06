using System.Drawing;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace ReolinkDBWin.Services;

/// <summary>Icône dans la zone de notification avec menu contextuel.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _pauseItem;
    private readonly WinForms.ToolStripMenuItem _liveItem;
    private readonly WinForms.ToolStripMenuItem _historyItem;
    private readonly Icon _normalIcon;
    private readonly Icon _pausedIcon;

    public TrayIcon(Action showLive, Action showHistory, Action showSettings, Action<bool> setPaused, Action testNotification, Action showLicense, Action exit)
    {
        _normalIcon = LoadIcon("app.ico");
        _pausedIcon = LoadIcon("app-paused.ico");

        var menu = new WinForms.ContextMenuStrip();
        _liveItem = new WinForms.ToolStripMenuItem("Voir la sonnette en direct", null, (_, _) => showLive()) { Font = new Font(WinForms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        _historyItem = new WinForms.ToolStripMenuItem("Historique Frigate…", null, (_, _) => showHistory());
        _pauseItem = new WinForms.ToolStripMenuItem("Suspendre les notifications", null, (_, _) =>
        {
            _pauseItem!.Checked = !_pauseItem.Checked;
            _icon!.Icon = _pauseItem.Checked ? _pausedIcon : _normalIcon;
            setPaused(_pauseItem.Checked);
        });
        menu.Items.Add(_liveItem);
        menu.Items.Add(_historyItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WinForms.ToolStripMenuItem("Notification de test", null, (_, _) => testNotification()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Paramètres…", null, (_, _) => showSettings()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Licence et à propos…", null, (_, _) => showLicense()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("Ouvrir le journal de diagnostic", null, (_, _) =>
        {
            Diagnostics.LogHeartbeat();
            AppInfo.OpenUrl(AppPaths.LogFile);
        }));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("Quitter", null, (_, _) => exit()));

        _icon = new WinForms.NotifyIcon
        {
            Icon = _normalIcon,
            Text = AppInfo.ProductName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) showLive();
        };
    }

    public void SetShortcutHints(string live, string history)
    {
        _liveItem.ShortcutKeyDisplayString = live;
        _historyItem.ShortcutKeyDisplayString = history;
    }

    public void SetHistoryEnabled(bool enabled) => _historyItem.Enabled = enabled;

    /// <summary>Infobulle (64 caractères max imposés par Windows).</summary>
    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..60] + "…" : text;

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _normalIcon.Dispose();
        _pausedIcon.Dispose();
    }

    private static Icon LoadIcon(string name)
    {
        var info = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{name}"));
        using var stream = info!.Stream;
        return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }
}
