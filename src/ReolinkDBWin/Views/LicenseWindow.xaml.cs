using System.Windows;
using ReolinkDBWin.Core.Licensing;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

/// <summary>Activation de la licence, état de l'abonnement et crédits.</summary>
public partial class LicenseWindow : Window
{
    private readonly LicenseManager _manager;

    public LicenseWindow(LicenseManager manager, bool blocking)
    {
        _manager = manager;
        InitializeComponent();
        Title = $"Licence — {AppInfo.ProductName}";
        ProductText.Text = $"{AppInfo.ProductName} {AppInfo.Version}";
        TaglineText.Text = AppInfo.Tagline;
        CreditsRun.Text = $"Développé par {AppInfo.Developer} · {AppInfo.Copyright}";
        GitHubRun.Text = AppInfo.GitHubUrl;
        BuyButton.Visibility = string.IsNullOrWhiteSpace(AppInfo.PurchaseUrl) ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Content = blocking ? "Quitter" : "Fermer";
        Refresh();
    }

    public LicenseStatus Status { get; private set; } = null!;

    private void Refresh()
    {
        Status = _manager.Evaluate();
        StatusText.Text = Status.Message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(Status.AllowsUse
            ? Status.ShouldRemind ? "WarnBrush" : "OkBrush"
            : "ErrorBrush");
        LicenseeText.Text = Status.License is { } l
            ? $"Titulaire : {l.Name}{(string.IsNullOrEmpty(l.Email) ? "" : $" <{l.Email}>")} · licence {l.Id}"
            : "";
        DeactivateButton.Visibility = Status.License is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        var (ok, _, error) = _manager.Activate(KeyBox.Text);
        if (!ok)
        {
            ErrorText.Text = error;
            return;
        }
        ErrorText.Text = "";
        KeyBox.Text = "";
        Refresh();
        DialogResult = true;
    }

    private void DeactivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Retirer la licence de cet ordinateur ? Vous pourrez la réactiver avec la même clé.", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _manager.Deactivate();
        Refresh();
    }

    private void BuyButton_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.PurchaseUrl);
    private void GitHubLink_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.GitHubUrl);
    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
