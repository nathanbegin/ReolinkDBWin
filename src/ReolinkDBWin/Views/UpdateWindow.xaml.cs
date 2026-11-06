using System.Diagnostics;
using System.IO;
using System.Windows;
using ReolinkDBWin.Core.Updates;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

/// <summary>Nouvelle version disponible : notes de version, téléchargement et installation par-dessus l'actuelle.</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateInfo _info;
    private readonly Action<string> _skip;
    private readonly Func<string, Task> _exitForUpdate;
    private CancellationTokenSource? _cts;

    public UpdateWindow(UpdateInfo info, Action<string> skip, Func<string, Task> exitForUpdate)
    {
        _info = info;
        _skip = skip;
        _exitForUpdate = exitForUpdate;
        InitializeComponent();
        Title = $"Mise à jour — {AppInfo.ProductName}";
        HeaderText.Text = $"{AppInfo.ProductName} {info.Version.ToString(3)} est disponible";
        VersionText.Text = $"Version installée : {AppInfo.Version}" + (info.PublishedAt is { } d ? $" · publiée le {d.LocalDateTime:d MMMM yyyy}" : "");
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes) ? "(pas de notes de version)" : CleanMarkdown(info.Notes);
        if (info.InstallerUrl is null)
        {
            InstallButton.IsEnabled = false;
            StatusText.Text = "Cette version n'a pas d'installateur : téléchargez-la depuis la page.";
        }
        else
        {
            StatusText.Text = "Vos paramètres, caméras et votre licence sont conservés. L'application redémarre après l'installation.";
        }
        Closed += (_, _) => _cts?.Cancel();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var path = Path.Combine(Path.GetTempPath(), _info.InstallerName ?? $"VigieWidget-Setup-{_info.Version.ToString(3)}.exe");
        try
        {
            StatusText.Text = "Téléchargement…";
            using var checker = UpdateService.CreateChecker();
            var progress = new Progress<double>(p =>
            {
                Progress.Value = p;
                StatusText.Text = $"Téléchargement… {p:P0}";
            });
            await checker.DownloadAsync(_info, path, progress, _cts.Token);
            StatusText.Text = "Installation…";
            Log.Info($"Mise à jour : lancement de l'installateur {path}");
            // Installation silencieuse par-dessus l'installation existante, puis redémarrage de l'application.
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
            {
                UseShellExecute = true,
            });
            await _exitForUpdate($"mise à jour vers {_info.Version.ToString(3)}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Mise à jour", ex);
            StatusText.Text = "Échec de la mise à jour : " + ex.Message;
            InstallButton.IsEnabled = true;
            SkipButton.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        _skip(_info.Version.ToString(3));
        Close();
    }

    private void PageButton_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(_info.PageUrl.ToString());
    private void LaterButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Notes de version GitHub (Markdown) en texte lisible.</summary>
    private static string CleanMarkdown(string md) => string.Join(Environment.NewLine, md.Replace("\r", "").Split('\n').Select(l =>
    {
        var t = l.TrimEnd();
        if (t.StartsWith("### ")) return t[4..].ToUpperInvariant();
        if (t.StartsWith("## ")) return t[3..].ToUpperInvariant();
        if (t.StartsWith("# ")) return t[2..].ToUpperInvariant();
        if (t.StartsWith("- ") || t.StartsWith("* ")) return "• " + t[2..];
        return t;
    }).Select(l => l.Replace("**", "").Replace("`", "")));
}
