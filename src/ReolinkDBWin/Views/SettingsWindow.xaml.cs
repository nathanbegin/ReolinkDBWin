using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using ReolinkDBWin.Core.HomeAssistant;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Reolink;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<DetectionType, Task> _sendTest;
    private readonly List<EntityRow> _haRows = [];

    /// <param name="settings">Copie de travail : modifiée seulement si l'utilisateur enregistre.</param>
    public SettingsWindow(AppSettings settings, Func<DetectionType, Task> sendTest)
    {
        _settings = settings;
        _sendTest = sendTest;
        InitializeComponent();
        LoadFromSettings();

        foreach (var c in new Control[] { ReolinkHost, ReolinkUser, CustomUrl, FrigateStreamName, FrigateUrl, ReolinkRtspPort, FrigateRtspPort })
            if (c is TextBox tb) tb.TextChanged += (_, _) => UpdateStreamPreview();
        ReolinkPassword.PasswordChanged += (_, _) => UpdateStreamPreview();
        foreach (var rb in new[] { StreamMain, StreamSub, StreamFrigate, StreamCustom })
            rb.Checked += (_, _) => UpdateStreamPreview();
        FrigateCamera.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateStreamPreview()));
        UpdateStreamPreview();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AboutText.Text = $"ReolinkDBWin {version?.ToString(3)} — widget vidéo, notifications et historique Frigate pour sonnette Reolink.\nConfiguration : {AppPaths.SettingsFile}";
    }

    /// <summary>Paramètres modifiés (valides seulement si DialogResult == true).</summary>
    public AppSettings Result => _settings;

    // ------------------------------------------------------------------------------------------
    // Chargement / enregistrement
    // ------------------------------------------------------------------------------------------

    private void LoadFromSettings()
    {
        var r = _settings.Reolink;
        ReolinkHost.Text = r.Host;
        ReolinkUser.Text = r.Username;
        ReolinkPassword.Password = r.Password;
        ReolinkHttps.IsChecked = r.UseHttps;
        ReolinkPort.Text = r.Port > 0 ? r.Port.ToString() : "";
        ReolinkRtspPort.Text = r.RtspPort.ToString();
        ReolinkChannel.Text = r.Channel.ToString();
        ReolinkAcceptCert.IsChecked = r.AcceptInvalidCertificate;
        ReolinkMonitor.IsChecked = r.MonitorEvents;
        ReolinkPoll.Text = r.PollIntervalMs.ToString();

        var s = _settings.Stream;
        StreamMain.IsChecked = s.Source == StreamSource.ReolinkMain;
        StreamSub.IsChecked = s.Source == StreamSource.ReolinkSub;
        StreamFrigate.IsChecked = s.Source == StreamSource.FrigateRestream;
        StreamCustom.IsChecked = s.Source == StreamSource.Custom;
        FrigateStreamName.Text = s.FrigateStreamName;
        FrigateRtspPort.Text = s.FrigateRtspPort.ToString();
        CustomUrl.Text = s.CustomUrl;
        NetworkCaching.Text = s.NetworkCachingMs.ToString();
        UseTcp.IsChecked = s.UseTcp;
        StartMuted.IsChecked = s.StartMuted;
        KeepAlive.IsChecked = s.KeepAliveWhenHidden;
        WidgetTopmost.IsChecked = _settings.LiveWindow.Topmost;

        var n = _settings.Notifications;
        NotifEnabled.IsChecked = n.Enabled;
        NotifReolink.IsChecked = n.FromReolink;
        NotifFrigate.IsChecked = n.FromFrigate;
        NotifHa.IsChecked = n.FromHomeAssistant;
        TypeDoorbell.IsChecked = n.Doorbell;
        TypePerson.IsChecked = n.Person;
        TypeVehicle.IsChecked = n.Vehicle;
        TypeAnimal.IsChecked = n.Animal;
        TypePackage.IsChecked = n.Package;
        TypeFace.IsChecked = n.Face;
        TypeMotion.IsChecked = n.Motion;
        TypeOther.IsChecked = n.Other;
        Cooldown.Text = n.CooldownSeconds.ToString();
        IncludeSnapshot.IsChecked = n.IncludeSnapshot;
        OpenLiveOnDoorbell.IsChecked = n.OpenLiveOnDoorbell;
        NotifSound.IsChecked = n.Sound;

        var f = _settings.Frigate;
        FrigateUrl.Text = f.BaseUrl;
        FrigateUser.Text = f.Username;
        FrigatePassword.Password = f.Password;
        FrigateAcceptCert.IsChecked = f.AcceptInvalidCertificate;
        FrigateCamera.Text = f.Camera;
        FrigatePoll.Text = f.PollIntervalSeconds.ToString();
        FrigateLabels.Text = string.Join(", ", f.NotifyLabels);

        var h = _settings.HomeAssistant;
        HaUrl.Text = h.BaseUrl;
        HaToken.Password = h.AccessToken;
        HaAcceptCert.IsChecked = h.AcceptInvalidCertificate;
        _haRows.Clear();
        foreach (var id in h.EntityIds) _haRows.Add(new EntityRow(id, id) { IsSelected = true });
        RefreshHaList();

        HotkeyLive.Text = Hotkey.Parse(_settings.Hotkeys.ToggleLive).ToString();
        HotkeyHistory.Text = Hotkey.Parse(_settings.Hotkeys.OpenHistory).ToString();
        StartWithWindows.IsChecked = _settings.StartWithWindows;
    }

    private void ApplyToSettings(AppSettings target)
    {
        var r = target.Reolink;
        r.Host = ReolinkHost.Text.Trim();
        r.Username = ReolinkUser.Text.Trim();
        r.Password = ReolinkPassword.Password;
        r.UseHttps = ReolinkHttps.IsChecked == true;
        r.Port = ParseInt(ReolinkPort.Text, 0, 0, 65535);
        r.RtspPort = ParseInt(ReolinkRtspPort.Text, 554, 1, 65535);
        r.Channel = ParseInt(ReolinkChannel.Text, 0, 0, 63);
        r.AcceptInvalidCertificate = ReolinkAcceptCert.IsChecked == true;
        r.MonitorEvents = ReolinkMonitor.IsChecked == true;
        r.PollIntervalMs = ParseInt(ReolinkPoll.Text, 1000, 300, 60000);

        var s = target.Stream;
        s.Source = StreamSub.IsChecked == true ? StreamSource.ReolinkSub
            : StreamFrigate.IsChecked == true ? StreamSource.FrigateRestream
            : StreamCustom.IsChecked == true ? StreamSource.Custom
            : StreamSource.ReolinkMain;
        s.FrigateStreamName = FrigateStreamName.Text.Trim();
        s.FrigateRtspPort = ParseInt(FrigateRtspPort.Text, 8554, 1, 65535);
        s.CustomUrl = CustomUrl.Text.Trim();
        s.NetworkCachingMs = ParseInt(NetworkCaching.Text, 400, 0, 5000);
        s.UseTcp = UseTcp.IsChecked == true;
        s.StartMuted = StartMuted.IsChecked == true;
        s.KeepAliveWhenHidden = KeepAlive.IsChecked == true;
        target.LiveWindow.Topmost = WidgetTopmost.IsChecked == true;

        var n = target.Notifications;
        n.Enabled = NotifEnabled.IsChecked == true;
        n.FromReolink = NotifReolink.IsChecked == true;
        n.FromFrigate = NotifFrigate.IsChecked == true;
        n.FromHomeAssistant = NotifHa.IsChecked == true;
        n.Doorbell = TypeDoorbell.IsChecked == true;
        n.Person = TypePerson.IsChecked == true;
        n.Vehicle = TypeVehicle.IsChecked == true;
        n.Animal = TypeAnimal.IsChecked == true;
        n.Package = TypePackage.IsChecked == true;
        n.Face = TypeFace.IsChecked == true;
        n.Motion = TypeMotion.IsChecked == true;
        n.Other = TypeOther.IsChecked == true;
        n.CooldownSeconds = ParseInt(Cooldown.Text, 30, 0, 3600);
        n.IncludeSnapshot = IncludeSnapshot.IsChecked == true;
        n.OpenLiveOnDoorbell = OpenLiveOnDoorbell.IsChecked == true;
        n.Sound = NotifSound.IsChecked == true;

        var f = target.Frigate;
        f.BaseUrl = FrigateUrl.Text.Trim().TrimEnd('/');
        f.Username = FrigateUser.Text.Trim();
        f.Password = FrigatePassword.Password;
        f.AcceptInvalidCertificate = FrigateAcceptCert.IsChecked == true;
        f.Camera = FrigateCamera.Text.Trim();
        f.PollIntervalSeconds = ParseInt(FrigatePoll.Text, 5, 2, 600);
        f.NotifyLabels = FrigateLabels.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.ToLowerInvariant()).Distinct().ToList();

        var h = target.HomeAssistant;
        h.BaseUrl = HaUrl.Text.Trim().TrimEnd('/');
        h.AccessToken = HaToken.Password.Trim();
        h.AcceptInvalidCertificate = HaAcceptCert.IsChecked == true;
        h.EntityIds = _haRows.Where(r => r.IsSelected).Select(r => r.EntityId).ToList();

        target.Hotkeys.ToggleLive = HotkeyLive.Text;
        target.Hotkeys.OpenHistory = HotkeyHistory.Text;
        target.StartWithWindows = StartWithWindows.IsChecked == true;
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), out var v) ? Math.Clamp(v, min, max) : fallback;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyToSettings(_settings);
        if (_settings.Notifications.FromHomeAssistant && _settings.HomeAssistant.IsConfigured && _settings.HomeAssistant.EntityIds.Count == 0)
        {
            var answer = MessageBox.Show(this, "Les notifications Home Assistant sont activées, mais aucune entité n'est sélectionnée. Enregistrer quand même ?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        _settings.FirstRunCompleted = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDirectory);
            Process.Start(new ProcessStartInfo(AppPaths.DataDirectory) { UseShellExecute = true });
        }
        catch { }
    }

    private void UpdateStreamPreview()
    {
        var tmp = SettingsStore.Clone(_settings);
        ApplyToSettings(tmp);
        var url = StreamUrlBuilder.Build(tmp);
        StreamPreview.Text = url is null ? "URL du flux : (configuration incomplète)" : "URL du flux : " + StreamUrlBuilder.Redact(url);
    }

    // ------------------------------------------------------------------------------------------
    // Sonnette : découverte et test
    // ------------------------------------------------------------------------------------------

    private void DiscoverButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new DiscoveryWindow(ReolinkUser.Text.Trim(), ReolinkPassword.Password) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedDevice is not { } d) return;
        ReolinkHost.Text = d.Host;
        if (d.Port > 0)
        {
            ReolinkHttps.IsChecked = d.UseHttps;
            ReolinkPort.Text = d.Port is 80 or 443 ? "" : d.Port.ToString();
        }
        ReolinkTestResult.Text = $"Appareil sélectionné : {d.Name ?? d.Host} {(d.Model is null ? "" : $"({d.Model})")}";
    }

    private async void TestReolinkButton_Click(object sender, RoutedEventArgs e)
    {
        var tmp = SettingsStore.Clone(_settings);
        ApplyToSettings(tmp);
        if (string.IsNullOrWhiteSpace(tmp.Reolink.Host))
        {
            ShowResult(ReolinkTestResult, false, "Indiquez l'adresse de la sonnette.");
            return;
        }
        ShowResult(ReolinkTestResult, null, "Connexion en cours…");
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try
        {
            using var client = new ReolinkClient(new ReolinkConnectionInfo
            {
                Host = tmp.Reolink.Host, Port = tmp.Reolink.Port, UseHttps = tmp.Reolink.UseHttps,
                Username = tmp.Reolink.Username, Password = tmp.Reolink.Password,
                AcceptInvalidCertificate = tmp.Reolink.AcceptInvalidCertificate,
            });
            var info = await client.GetDeviceInfoAsync();
            string events;
            try
            {
                var states = await client.GetDetectionStatesAsync(tmp.Reolink.Channel, tryGetEvents: true);
                events = "détections disponibles : " + string.Join(", ", states.Keys.Select(k => DetectionTypeInfo.DisplayName(DetectionTypeInfo.FromLabel(k))).Distinct());
            }
            catch (ReolinkApiException)
            {
                try
                {
                    var states = await client.GetDetectionStatesAsync(tmp.Reolink.Channel, tryGetEvents: false);
                    events = "détections disponibles : " + string.Join(", ", states.Keys.Select(k => DetectionTypeInfo.DisplayName(DetectionTypeInfo.FromLabel(k))).Distinct())
                             + " (appui sur le bouton non disponible via l'API sur ce firmware)";
                }
                catch (Exception ex)
                {
                    events = "lecture des détections impossible : " + ex.Message;
                }
            }
            await client.LogoutAsync();
            ShowResult(ReolinkTestResult, true, $"✔ {info}\n{events}");
        }
        catch (Exception ex)
        {
            var hint = ex is System.Net.Http.HttpRequestException or TaskCanceledException
                ? "\nVérifiez l'adresse IP et que HTTP/HTTPS est activé sur la sonnette (application Reolink › Réglages › Réseau › Avancé › Configuration du port). Essayez de cocher ou décocher HTTPS."
                : "";
            ShowResult(ReolinkTestResult, false, "✖ " + ex.Message + hint);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Frigate
    // ------------------------------------------------------------------------------------------

    private async void TestFrigateButton_Click(object sender, RoutedEventArgs e)
    {
        var tmp = SettingsStore.Clone(_settings);
        ApplyToSettings(tmp);
        if (!tmp.Frigate.IsConfigured)
        {
            ShowResult(FrigateTestResult, false, "Indiquez l'adresse du serveur Frigate.");
            return;
        }
        ShowResult(FrigateTestResult, null, "Connexion en cours…");
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try
        {
            using var client = EventHub.CreateFrigateClient(tmp.Frigate);
            var version = await client.GetVersionAsync();
            var cameras = await client.GetCamerasAsync();
            var current = FrigateCamera.Text;
            FrigateCamera.Items.Clear();
            foreach (var c in cameras) FrigateCamera.Items.Add(c);
            if (string.IsNullOrWhiteSpace(current))
            {
                current = cameras.FirstOrDefault(c => c.Contains("door", StringComparison.OrdinalIgnoreCase)
                                                      || c.Contains("sonnet", StringComparison.OrdinalIgnoreCase)
                                                      || c.Contains("bell", StringComparison.OrdinalIgnoreCase)
                                                      || c.Contains("porte", StringComparison.OrdinalIgnoreCase))
                          ?? cameras.FirstOrDefault() ?? "";
            }
            FrigateCamera.Text = current;
            ShowResult(FrigateTestResult, true, $"✔ Frigate {version} — {cameras.Count} caméra(s) : {string.Join(", ", cameras)}");
        }
        catch (Exception ex)
        {
            ShowResult(FrigateTestResult, false, "✖ " + ex.Message);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Home Assistant
    // ------------------------------------------------------------------------------------------

    private async void LoadHaEntitiesButton_Click(object sender, RoutedEventArgs e)
    {
        var url = HaUrl.Text.Trim();
        var token = HaToken.Password.Trim();
        if (url.Length == 0 || token.Length == 0)
        {
            ShowResult(HaTestResult, false, "Indiquez l'adresse et le jeton d'accès.");
            return;
        }
        ShowResult(HaTestResult, null, "Connexion en cours…");
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try
        {
            using var client = new HomeAssistantClient(url, token, HaAcceptCert.IsChecked == true);
            await client.PingAsync();
            var entities = await client.GetDetectionEntitiesAsync();
            var selected = _haRows.Where(r => r.IsSelected).Select(r => r.EntityId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var firstTime = selected.Count == 0;
            _haRows.Clear();
            foreach (var en in entities)
            {
                var type = DetectionTypeInfo.FromEntityId(en.EntityId);
                // Première fois : présélection des détecteurs Reolink pertinents.
                var preselect = firstTime && type is DetectionType.Doorbell or DetectionType.Person or DetectionType.Package
                                && en.EntityId.StartsWith("binary_sensor.");
                _haRows.Add(new EntityRow(en.EntityId, en.FriendlyName) { IsSelected = selected.Contains(en.EntityId) || preselect });
            }
            foreach (var missing in selected.Where(id => entities.All(en => !en.EntityId.Equals(id, StringComparison.OrdinalIgnoreCase))))
                _haRows.Add(new EntityRow(missing, missing + " (introuvable)") { IsSelected = true });
            // Les entités sélectionnées en premier, puis les entités probablement liées à la sonnette.
            _haRows.Sort((a, b) =>
            {
                var c = b.IsSelected.CompareTo(a.IsSelected);
                if (c != 0) return c;
                c = (DetectionTypeInfo.FromEntityId(a.EntityId) == DetectionType.Other).CompareTo(DetectionTypeInfo.FromEntityId(b.EntityId) == DetectionType.Other);
                return c != 0 ? c : string.CompareOrdinal(a.EntityId, b.EntityId);
            });
            RefreshHaList();
            ShowResult(HaTestResult, true, $"✔ Connecté — {entities.Count} entité(s) binary_sensor / event.");
        }
        catch (Exception ex)
        {
            ShowResult(HaTestResult, false, "✖ " + ex.Message);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    private void HaFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshHaList();

    private void RefreshHaList()
    {
        var view = CollectionViewSource.GetDefaultView(_haRows);
        var filter = HaFilter?.Text?.Trim() ?? "";
        view.Filter = filter.Length == 0
            ? null
            : o => o is EntityRow r && (r.EntityId.Contains(filter, StringComparison.OrdinalIgnoreCase) || r.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase));
        HaEntities.ItemsSource = null;
        HaEntities.ItemsSource = view;
    }

    // ------------------------------------------------------------------------------------------
    // Notifications
    // ------------------------------------------------------------------------------------------

    private async void TestNotificationButton_Click(object sender, RoutedEventArgs e) => await _sendTest(DetectionType.Doorbell);

    private void ShowResult(TextBlock target, bool? ok, string message)
    {
        target.Text = message;
        target.Foreground = (System.Windows.Media.Brush)FindResource(ok switch
        {
            true => "OkBrush",
            false => "ErrorBrush",
            _ => "MutedForeground",
        });
    }

    public sealed class EntityRow(string entityId, string friendlyName) : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string EntityId { get; } = entityId;
        public string FriendlyName { get; } = friendlyName;

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
