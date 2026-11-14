using System.Windows;
using System.Windows.Controls;
using ReolinkDBWin.Core.Cameras;
using ReolinkDBWin.Core.Discovery;
using ReolinkDBWin.Core.Net;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

/// <summary>
/// Assistant d'ajout de caméra : adresse RTSP construite selon la marque, ou demandée à la caméra par ONVIF
/// (profil le plus grand = flux principal, le plus petit = flux léger).
/// </summary>
public partial class AddCameraWindow : Window
{
    private readonly Dictionary<string, DiscoveredDevice> _found = new();
    private bool _loading = true;

    public AddCameraWindow()
    {
        InitializeComponent();
        BrandCombo.ItemsSource = CameraBrands.All;
        BrandCombo.SelectedIndex = 0;
        _loading = false;
        ApplyBrand();
    }

    public CameraSettings? Result { get; private set; }

    private CameraBrand Brand => (CameraBrand)BrandCombo.SelectedItem;
    private bool IsOnvif => Brand.Id == CameraBrands.OnvifId;

    private void BrandCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) ApplyBrand();
    }

    private void ApplyBrand()
    {
        PortBox.Text = Brand.DefaultPort.ToString();
        NotesText.Text = Brand.Notes;
        NotesText.Visibility = Brand.Notes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActionButton.Content = IsOnvif ? "Interroger la caméra (ONVIF)" : "Tester le flux";
        ChannelBox.IsEnabled = !IsOnvif;
        UpdateUrls();
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => UpdateUrls();
    private void Password_Changed(object sender, RoutedEventArgs e) => UpdateUrls();

    private void HostBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Appareil trouvé par ONVIF : on reprend son port de service et son nom.
        if (HostBox.SelectedItem is not string host || !_found.TryGetValue(host, out var d)) return;
        if (IsOnvif && Uri.TryCreate(d.OnvifServiceUrl, UriKind.Absolute, out var service)) PortBox.Text = service.Port.ToString();
        if (NameBox.Text is "Caméra" or "" && (d.Name ?? d.Model) is { } name) NameBox.Text = name;
    }

    private void UpdateUrls()
    {
        if (_loading || BrandCombo.SelectedItem is null || IsOnvif) return; // ONVIF : rempli par la caméra
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            MainUrlBox.Text = SubUrlBox.Text = "";
            return;
        }
        int.TryParse(PortBox.Text, out var port);
        int.TryParse(ChannelBox.Text, out var channel);
        MainUrlBox.Text = Brand.BuildUrl(false, host, UserBox.Text, PasswordBox.Password, channel, port);
        SubUrlBox.Text = Brand.Sub is null ? "" : Brand.BuildUrl(true, host, UserBox.Text, PasswordBox.Password, channel, port);
    }

    private async void FindButton_Click(object sender, RoutedEventArgs e)
    {
        FindButton.IsEnabled = false;
        Show(null, "Recherche des caméras ONVIF sur le réseau…");
        try
        {
            var devices = await Task.Run(() => new ReolinkDiscovery().DiscoverOnvifAsync(TimeSpan.FromSeconds(3)));
            _found.Clear();
            HostBox.Items.Clear();
            foreach (var d in devices)
            {
                _found[d.Host] = d;
                HostBox.Items.Add(d.Host);
            }
            Show(devices.Count > 0, devices.Count == 0
                ? "Aucune caméra ONVIF trouvée. ONVIF est peut-être désactivé sur la caméra : saisissez l'adresse IP à la main."
                : $"{devices.Count} appareil(s) : " + string.Join(", ", devices.Select(d => $"{d.Host} ({d.Name ?? d.Model ?? "?"})")));
            if (devices.Count > 0) HostBox.IsDropDownOpen = true;
        }
        catch (Exception ex)
        {
            Show(false, "Recherche impossible : " + ex.Message);
        }
        finally
        {
            FindButton.IsEnabled = true;
        }
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        ActionButton.IsEnabled = false;
        try
        {
            if (IsOnvif) await QueryOnvifAsync();
            else await TestAsync();
        }
        finally
        {
            ActionButton.IsEnabled = true;
        }
    }

    private async Task QueryOnvifAsync()
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            Show(false, "Indiquez l'adresse IP de la caméra (ou utilisez « Rechercher »).");
            return;
        }
        int.TryParse(PortBox.Text, out var port);
        Show(null, "Interrogation de la caméra…");
        try
        {
            using var client = new OnvifClient(host, port, UserBox.Text, PasswordBox.Password);
            var profiles = await client.GetProfilesAsync();
            if (profiles.Count == 0)
            {
                Show(false, "La caméra n'a renvoyé aucun profil vidéo.");
                return;
            }
            var main = profiles[0];
            var sub = profiles.Count > 1 ? profiles[^1] : null;
            MainUrlBox.Text = OnvifClient.WithCredentials(main.StreamUri, UserBox.Text, PasswordBox.Password);
            SubUrlBox.Text = sub is null ? "" : OnvifClient.WithCredentials(sub.StreamUri, UserBox.Text, PasswordBox.Password);
            Show(true, $"✔ {profiles.Count} profil(s) — principal : {main}" + (sub is null ? "" : $" ; léger : {sub}"));
        }
        catch (Exception ex)
        {
            Show(false, "✖ " + ex.Message + (ex is System.Net.Http.HttpRequestException or TaskCanceledException
                ? " Vérifiez l'adresse et le port ONVIF (80, 8000 pour Reolink, 8080, 2020 pour Tapo) et qu'ONVIF est activé." : ""));
        }
    }

    private async Task TestAsync()
    {
        var url = MainUrlBox.Text.Trim();
        if (!url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            Show(false, "Indiquez l'adresse IP de la caméra.");
            return;
        }
        Show(null, $"Test de {StreamUrlBuilder.Redact(url)}…");
        var report = await RtspProbe.ProbeAsync(url, TimeSpan.FromSeconds(5));
        // Certaines caméras n'acceptent que l'authentification « digest » : le test répond alors « identifiants
        // requis » alors que la lecture fonctionnera.
        Show(report.Result == RtspProbeResult.Ok ? true : report.Result == RtspProbeResult.Unauthorized ? null : false,
            report.Result == RtspProbeResult.Unauthorized
                ? "La caméra répond et demande une authentification (normal pour beaucoup de marques) : l'adresse et le port sont bons. Si l'image ne s'affiche pas, vérifiez l'utilisateur et le mot de passe."
                : report.Message);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var url = MainUrlBox.Text.Trim();
        if (url.Length == 0)
        {
            Show(false, IsOnvif ? "Cliquez d'abord sur « Interroger la caméra (ONVIF) »." : "Indiquez l'adresse IP de la caméra.");
            return;
        }
        Result = new CameraSettings
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Caméra" : NameBox.Text.Trim(),
            Source = CameraSourceKind.Url,
            Url = url,
            LightAddress = SubUrlBox.Text.Trim(),
        };
        DialogResult = true;
    }

    private void Show(bool? ok, string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(ok switch
        {
            true => "OkBrush",
            false => "ErrorBrush",
            _ => "MutedForeground",
        });
    }
}
