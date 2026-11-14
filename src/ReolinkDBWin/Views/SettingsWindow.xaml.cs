using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using ReolinkDBWin.Core.HomeAssistant;
using ReolinkDBWin.Core.Licensing;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Net;
using ReolinkDBWin.Core.Reolink;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<DetectionType, Task> _sendTest;
    private readonly List<EntityRow> _haRows = [];
    private readonly ObservableCollection<CameraEditItem> _cameraRows = [];
    private readonly Action _showLicense;
    private readonly Func<Task> _checkUpdates;

    /// <param name="settings">Copie de travail : modifiée seulement si l'utilisateur enregistre.</param>
    public SettingsWindow(AppSettings settings, Func<DetectionType, Task> sendTest, Action showLicense, LicenseStatus license, Func<Task> checkUpdates)
    {
        _checkUpdates = checkUpdates;
        _settings = settings;
        _sendTest = sendTest;
        _showLicense = showLicense;
        InitializeComponent();
        LoadFromSettings();

        foreach (var c in new Control[] { ReolinkHost, ReolinkUser, CustomUrl, FrigateUrl, ReolinkRtspPort, FrigateRtspPort, FrigateRtspHost, FrigateRtspUser })
            if (c is TextBox tb) tb.TextChanged += (_, _) => UpdateStreamPreview();
        FrigateStreamName.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateStreamPreview()));
        ReolinkPassword.PasswordChanged += (_, _) => UpdateStreamPreview();
        foreach (var rb in new[] { StreamMain, StreamSub, StreamFrigate, StreamCustom })
            rb.Checked += (_, _) => UpdateStreamPreview();
        FrigateCamera.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateStreamPreview()));
        UpdateStreamPreview();

        Title = $"Paramètres — {AppInfo.ProductName}";
        AboutText.Text = $"{AppInfo.ProductName} {AppInfo.Version} — {AppInfo.Tagline}.\nDéveloppé par {AppInfo.Developer} · {AppInfo.Copyright}\n{AppInfo.GitHubUrl}\n\nConfiguration : {AppPaths.SettingsFile}";
        LicenseStatusText.Text = license.Message;
        LicenseStatusText.Foreground = (System.Windows.Media.Brush)FindResource(license.AllowsUse ? (license.ShouldRemind ? "WarnBrush" : "OkBrush") : "ErrorBrush");
        CameraList.ItemsSource = _cameraRows;
        if (_cameraRows.Count > 0) CameraList.SelectedIndex = 0;
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
        FrigateRtspHost.Text = s.FrigateRtspHost;
        FrigateRtspUser.Text = s.FrigateRtspUsername;
        FrigateRtspPassword.Password = s.FrigateRtspPassword;
        FrigateRtspPort.Text = s.FrigateRtspPort.ToString();
        CustomUrl.Text = s.CustomUrl;
        NetworkCaching.Text = s.NetworkCachingMs.ToString();
        UseTcp.IsChecked = s.UseTcp;
        DecoderCombo.SelectedIndex = (int)s.Decoder;
        StartMuted.IsChecked = s.StartMuted;
        KeepAlive.IsChecked = s.KeepAliveWhenHidden;
        WidgetTopmost.IsChecked = _settings.LiveWindow.Topmost;

        var n = _settings.Notifications;
        NotifEnabled.IsChecked = n.Enabled;
        StylePopup.IsChecked = n.Style == NotificationStyle.LivePopup;
        StyleWindows.IsChecked = n.Style == NotificationStyle.Windows;
        PopupSeconds.Text = n.PopupSeconds.ToString();
        OpenLiveOnDetection.IsChecked = n.OpenLiveOnDetection;
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

        var mv = _settings.MultiView;
        ModeSingle.IsChecked = mv.Mode == LiveViewMode.Single;
        ModeGrid.IsChecked = mv.Mode == LiveViewMode.Grid;
        ModeCarousel.IsChecked = mv.Mode == LiveViewMode.Carousel;
        GridColumns.Text = mv.GridColumns.ToString();
        MaxWait.Text = mv.MaxWaitSeconds.ToString();
        JumpOnDetection.IsChecked = mv.JumpOnDetection;
        DetectionHold.Text = mv.DetectionHoldSeconds.ToString();
        DetectionMax.Text = mv.DetectionMaxSeconds.ToString();
        DetectionCooldown.Text = mv.DetectionCooldownSeconds.ToString();
        KeepAllOpen.IsChecked = mv.KeepAllStreamsOpen;
        EnableZoom.IsChecked = mv.EnableMouseZoom;
        MaxZoom.Text = mv.MaxZoom.ToString("0.#");
        ShowLed.IsChecked = mv.ShowStatusLed;
        ShowNames.IsChecked = mv.ShowCameraNames;
        AdaptiveQuality.IsChecked = mv.AdaptiveQuality;
        FrigateLightStreamName.Text = _settings.Stream.FrigateLightStreamName;
        _cameraRows.Clear();
        foreach (var c in mv.Cameras) _cameraRows.Add(CameraEditItem.From(c));

        HotkeyLive.Text = Hotkey.Parse(_settings.Hotkeys.ToggleLive).ToString();
        HotkeyHistory.Text = Hotkey.Parse(_settings.Hotkeys.OpenHistory).ToString();
        StartWithWindows.IsChecked = _settings.StartWithWindows;
        AutoUpdate.IsChecked = _settings.Updates.AutoCheck;
        UpdateStatusText.Text = $"Version installée : {AppInfo.Version}" +
            (_settings.Updates.LastCheck is { } last ? $" · dernière vérification : {last.LocalDateTime:g}" : "");
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
        s.FrigateRtspHost = FrigateRtspHost.Text.Trim();
        s.FrigateRtspUsername = FrigateRtspUser.Text.Trim();
        s.FrigateRtspPassword = FrigateRtspPassword.Password;
        s.FrigateRtspPort = ParseInt(FrigateRtspPort.Text, 8554, 1, 65535);
        s.CustomUrl = CustomUrl.Text.Trim();
        s.NetworkCachingMs = ParseInt(NetworkCaching.Text, 400, 0, 5000);
        s.UseTcp = UseTcp.IsChecked == true;
        s.Decoder = (HardwareDecoder)Math.Clamp(DecoderCombo.SelectedIndex, 0, 2);
        s.HardwareDecoding = false;
        s.StartMuted = StartMuted.IsChecked == true;
        s.KeepAliveWhenHidden = KeepAlive.IsChecked == true;
        target.LiveWindow.Topmost = WidgetTopmost.IsChecked == true;

        var n = target.Notifications;
        n.Enabled = NotifEnabled.IsChecked == true;
        n.Style = StyleWindows.IsChecked == true ? NotificationStyle.Windows : NotificationStyle.LivePopup;
        n.PopupSeconds = ParseInt(PopupSeconds.Text, 20, 5, 600);
        n.OpenLiveOnDetection = OpenLiveOnDetection.IsChecked == true;
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

        var mv = target.MultiView;
        mv.Mode = ModeGrid.IsChecked == true ? LiveViewMode.Grid : ModeCarousel.IsChecked == true ? LiveViewMode.Carousel : LiveViewMode.Single;
        mv.GridColumns = ParseInt(GridColumns.Text, 0, 0, 8);
        mv.MaxWaitSeconds = ParseInt(MaxWait.Text, 10, 3, 120);
        mv.JumpOnDetection = JumpOnDetection.IsChecked == true;
        mv.DetectionHoldSeconds = ParseInt(DetectionHold.Text, 30, 5, 3600);
        mv.DetectionMaxSeconds = Math.Max(mv.DetectionHoldSeconds, ParseInt(DetectionMax.Text, 90, 5, 7200));
        mv.DetectionCooldownSeconds = ParseInt(DetectionCooldown.Text, 120, 0, 7200);
        mv.KeepAllStreamsOpen = KeepAllOpen.IsChecked == true;
        mv.EnableMouseZoom = EnableZoom.IsChecked == true;
        mv.MaxZoom = double.TryParse(MaxZoom.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z)
            ? Math.Clamp(z, 1.5, 10) : 4;
        mv.ShowStatusLed = ShowLed.IsChecked == true;
        mv.ShowCameraNames = ShowNames.IsChecked == true;
        mv.AdaptiveQuality = AdaptiveQuality.IsChecked == true;
        target.Stream.FrigateLightStreamName = FrigateLightStreamName.Text.Trim();
        mv.Cameras = _cameraRows.Select(r => r.ToSettings()).ToList();
        target.Normalize();

        target.Hotkeys.ToggleLive = HotkeyLive.Text;
        target.Hotkeys.OpenHistory = HotkeyHistory.Text;
        target.StartWithWindows = StartWithWindows.IsChecked == true;
        target.Updates.AutoCheck = AutoUpdate.IsChecked == true;
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
    // Caméras
    // ------------------------------------------------------------------------------------------

    private void AddCamera_Click(object sender, RoutedEventArgs e)
    {
        var item = CameraEditItem.From(new CameraSettings { Name = $"Caméra {_cameraRows.Count + 1}" });
        _cameraRows.Add(item);
        CameraList.SelectedItem = item;
    }

    private void AddCameraWizard_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddCameraWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is not { } camera) return;
        var item = CameraEditItem.From(camera);
        _cameraRows.Add(item);
        CameraList.SelectedItem = item;
    }

    private void RemoveCamera_Click(object sender, RoutedEventArgs e)
    {
        if (CameraList.SelectedItem is not CameraEditItem item) return;
        var index = _cameraRows.IndexOf(item);
        _cameraRows.Remove(item);
        if (_cameraRows.Count > 0) CameraList.SelectedIndex = Math.Min(index, _cameraRows.Count - 1);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int direction)
    {
        if (CameraList.SelectedItem is not CameraEditItem item) return;
        var index = _cameraRows.IndexOf(item);
        var target = index + direction;
        if (target < 0 || target >= _cameraRows.Count) return;
        _cameraRows.Move(index, target);
        CameraList.SelectedItem = item;
    }

    private async void ImportFrigate_Click(object sender, RoutedEventArgs e)
    {
        var tmp = SettingsStore.Clone(_settings);
        ApplyToSettings(tmp);
        if (!tmp.Frigate.IsConfigured)
        {
            MessageBox.Show(this, "Configurez d'abord l'adresse de Frigate dans l'onglet Frigate.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try
        {
            using var client = EventHub.CreateFrigateClient(tmp.Frigate);
            var cameras = await client.GetCamerasAsync();
            var streams = await client.GetGo2RtcStreamsAsync();
            var known = _cameraRows.Select(r => r.ToSettings().EffectiveFrigateCamera(tmp)).OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var name in cameras.Where(c => !known.Contains(c)))
            {
                _cameraRows.Add(CameraEditItem.From(new CameraSettings
                {
                    Name = char.ToUpper(name[0]) + name[1..].Replace('_', ' '),
                    Source = CameraSourceKind.FrigateRestream,
                    StreamName = PickStream(name, streams) ?? name,
                    LightAddress = PickLightStream(name, streams) ?? "",
                    FrigateCamera = name,
                }));
                added++;
            }
            var note = streams.Count == 0
                ? "\n\n⚠ Aucun flux go2rtc n'a été trouvé dans Frigate : le restream n'existe que pour les flux déclarés dans la section « go2rtc › streams » de la configuration de Frigate. Sans cela, utilisez une URL RTSP directe pour chaque caméra."
                : $"\n\nFlux go2rtc disponibles : {string.Join(", ", streams)}.";
            MessageBox.Show(this, (added == 0
                    ? "Toutes les caméras de Frigate sont déjà dans la liste."
                    : $"{added} caméra(s) ajoutée(s).") + note,
                Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Impossible de lire les caméras de Frigate : " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    /// <summary>Flux go2rtc correspondant à une caméra : même nom, sinon un flux qui commence par ce nom (ex. porte_main).</summary>
    private static string? PickStream(string camera, IReadOnlyList<string> streams)
    {
        if (streams.Count == 0) return null;
        return streams.FirstOrDefault(s => s.Equals(camera, StringComparison.OrdinalIgnoreCase))
               ?? streams.Where(s => s.StartsWith(camera, StringComparison.OrdinalIgnoreCase))
                   .OrderBy(s => s.Contains("sub", StringComparison.OrdinalIgnoreCase) ? 1 : 0).FirstOrDefault();
    }

    /// <summary>Sous-flux go2rtc d'une caméra : porte_sub, porte_low, porte_lq, porte_sd…</summary>
    private static string? PickLightStream(string camera, IReadOnlyList<string> streams)
    {
        string[] suffixes = ["_sub", "_low", "_lq", "_sd", "_detect", "-sub", "_substream"];
        return suffixes.Select(sfx => streams.FirstOrDefault(s => s.Equals(camera + sfx, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(s => s is not null);
    }

    private async void LoadGo2RtcStreams_Click(object sender, RoutedEventArgs e)
    {
        var tmp = SettingsStore.Clone(_settings);
        ApplyToSettings(tmp);
        if (!tmp.Frigate.IsConfigured)
        {
            ShowResult(StreamTestResult, false, "Configurez d'abord l'adresse de Frigate (onglet Frigate).");
            return;
        }
        try
        {
            using var client = EventHub.CreateFrigateClient(tmp.Frigate);
            var streams = await client.GetGo2RtcStreamsAsync();
            var current = FrigateStreamName.Text;
            var currentLight = FrigateLightStreamName.Text;
            FrigateStreamName.Items.Clear();
            FrigateLightStreamName.Items.Clear();
            foreach (var st in streams)
            {
                FrigateStreamName.Items.Add(st);
                FrigateLightStreamName.Items.Add(st);
            }
            if (string.IsNullOrWhiteSpace(current)) current = PickStream(tmp.Frigate.Camera, streams) ?? streams.FirstOrDefault() ?? "";
            FrigateStreamName.Text = current;
            if (string.IsNullOrWhiteSpace(currentLight)) currentLight = PickLightStream(current, streams) ?? "";
            FrigateLightStreamName.Text = currentLight;
            ShowResult(StreamTestResult, streams.Count > 0, streams.Count > 0
                ? $"{streams.Count} flux go2rtc : {string.Join(", ", streams)}"
                : "Aucun flux go2rtc déclaré dans Frigate (section go2rtc › streams).");
        }
        catch (Exception ex)
        {
            ShowResult(StreamTestResult, false, "✖ " + ex.Message);
        }
    }

    private async void TestStream_Click(object sender, RoutedEventArgs e)
    {
        var tmp = SettingsStore.Clone(_settings);
        ApplyToSettings(tmp);
        await ProbeAsync(StreamUrlBuilder.Build(tmp), StreamTestResult, (Button)sender);
    }

    private async void TestCamera_Click(object sender, RoutedEventArgs e)
    {
        if (CameraList.SelectedItem is not CameraEditItem item) return;
        var tmp = SettingsStore.Clone(_settings);
        ApplyToSettings(tmp);
        await ProbeAsync(StreamUrlBuilder.Build(tmp, item.ToSettings()), CameraTestResult, (Button)sender);
    }

    private async Task ProbeAsync(string? url, TextBlock target, Button button)
    {
        if (url is null)
        {
            ShowResult(target, false, "Configuration incomplète : impossible de construire l'URL du flux.");
            return;
        }
        if (!url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            ShowResult(target, null, "Test disponible seulement pour les URL RTSP.");
            return;
        }
        button.IsEnabled = false;
        ShowResult(target, null, $"Test de {StreamUrlBuilder.Redact(url)}…");
        try
        {
            var report = await RtspProbe.ProbeAsync(url, TimeSpan.FromSeconds(5));
            ShowResult(target, report.Result == RtspProbeResult.Ok, report.Message);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        try { await _checkUpdates(); }
        finally { btn.IsEnabled = true; }
    }

    private void ManageLicense_Click(object sender, RoutedEventArgs e) => _showLicense();
    private void GitHub_Click(object sender, RoutedEventArgs e) => AppInfo.OpenUrl(AppInfo.GitHubUrl);

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

    /// <summary>Caméra en cours d'édition (liaison avec l'éditeur).</summary>
    public sealed class CameraEditItem : INotifyPropertyChanged
    {
        private string _name = "";
        private bool _enabled = true;
        private int _sourceIndex;
        private string _address = "";
        private string _frigateCamera = "";
        private string _lightAddress = "";
        private int _dwellSeconds = 10;
        private int _transitionIndex = 1;
        private int _transitionMs = 600;
        private bool _notify;

        public string Id { get; private init; } = Guid.NewGuid().ToString("N");
        public string Name { get => _name; set => Set(ref _name, value); }
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public int SourceIndex
        {
            get => _sourceIndex;
            set
            {
                Set(ref _sourceIndex, value);
                OnPropertyChanged(nameof(HasAddress));
            }
        }
        public bool HasAddress => SourceIndex != 0;
        public string Address { get => _address; set => Set(ref _address, value); }
        public string FrigateCamera { get => _frigateCamera; set => Set(ref _frigateCamera, value); }
        public string LightAddress { get => _lightAddress; set => Set(ref _lightAddress, value); }
        public int DwellSeconds { get => _dwellSeconds; set => Set(ref _dwellSeconds, Math.Clamp(value, 2, 3600)); }
        public int TransitionIndex { get => _transitionIndex; set => Set(ref _transitionIndex, value); }
        public int TransitionMs { get => _transitionMs; set => Set(ref _transitionMs, Math.Clamp(value, 0, 5000)); }
        public bool Notify { get => _notify; set => Set(ref _notify, value); }

        public static CameraEditItem From(CameraSettings c) => new()
        {
            Id = c.Id,
            _name = c.Name,
            _enabled = c.Enabled,
            _sourceIndex = (int)c.Source,
            _address = c.Source == CameraSourceKind.Url ? c.Url : c.StreamName,
            _frigateCamera = c.FrigateCamera,
            _lightAddress = c.LightAddress,
            _dwellSeconds = c.DwellSeconds,
            _transitionIndex = (int)c.Transition,
            _transitionMs = c.TransitionMs,
            _notify = c.Notify,
        };

        public CameraSettings ToSettings()
        {
            var source = (CameraSourceKind)Math.Clamp(SourceIndex, 0, 2);
            return new CameraSettings
            {
                Id = Id,
                Name = string.IsNullOrWhiteSpace(Name) ? "Caméra" : Name.Trim(),
                Enabled = Enabled,
                Source = source,
                StreamName = source == CameraSourceKind.FrigateRestream ? Address.Trim() : "",
                Url = source == CameraSourceKind.Url ? Address.Trim() : "",
                FrigateCamera = FrigateCamera.Trim(),
                LightAddress = source == CameraSourceKind.Doorbell ? "" : LightAddress.Trim(),
                DwellSeconds = DwellSeconds,
                Transition = (TransitionKind)Math.Clamp(TransitionIndex, 0, 3),
                TransitionMs = TransitionMs,
                Notify = Notify,
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }
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
