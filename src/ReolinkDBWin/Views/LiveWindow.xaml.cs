using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

/// <summary>
/// Widget vidéo en direct : fenêtre sans bordure, redimensionnable, toujours au premier plan,
/// affichée/masquée par raccourci clavier global.
/// </summary>
public partial class LiveWindow : Window
{
    private readonly Func<AppSettings> _settings;
    private readonly Action _saveSettings;
    private readonly Action _openHistory;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly DispatcherTimer _bannerTimer;
    private MediaPlayer? _player;
    private string? _currentUrl;
    private bool _allowClose;
    private bool _wantPlaying;
    private int _failures;

    public LiveWindow(Func<AppSettings> settings, Action saveSettings, Action openHistory, Action openSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _openHistory = openHistory;
        _openSettings = openSettings;
        InitializeComponent();

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnectTimer.Tick += (_, _) =>
        {
            _reconnectTimer.Stop();
            if (_wantPlaying) StartPlayback(force: true);
        };
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            EventBanner.Visibility = Visibility.Collapsed;
        };

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => EnsurePlayer();
    }

    public bool IsShown => IsVisible && WindowState != WindowState.Minimized;

    // ------------------------------------------------------------------------------------------
    // Affichage
    // ------------------------------------------------------------------------------------------

    public void Toggle()
    {
        // Visible au premier plan (ou actif) : on masque. Sinon (masqué ou caché derrière) : on affiche.
        if (IsShown && (IsActive || Topmost)) HideWidget();
        else ShowWidget(activate: true);
    }

    public void ShowWidget(bool activate)
    {
        var s = _settings().LiveWindow;
        if (!IsVisible) RestoreWidgetBounds(s);
        Topmost = s.Topmost;
        UpdatePinButton();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowActivated = activate; // ouverture automatique (sonnette) : ne pas voler le focus
        Show();
        if (activate)
        {
            Activate();
            Focus();
        }
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        // Attendre que la surface vidéo (HWND) soit créée, sinon VLC ouvrirait sa propre fenêtre.
        Dispatcher.BeginInvoke(() => StartPlayback(force: false), DispatcherPriority.ApplicationIdle);
    }

    public void HideWidget()
    {
        SaveBounds();
        Hide();
        _clock.Stop();
        if (!_settings().Stream.KeepAliveWhenHidden) StopPlayback();
    }

    /// <summary>Affiche un bandeau temporaire (« Quelqu'un sonne… ») au-dessus de la vidéo.</summary>
    public void ShowEventBanner(DetectionEvent e)
    {
        EventBannerText.Text = $"{DetectionTypeInfo.NotificationTitle(e.Type)} — {e.Timestamp.LocalDateTime:HH:mm:ss}";
        EventBanner.Visibility = Visibility.Visible;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    /// <summary>Appelé après modification des paramètres : relance le flux si l'URL a changé.</summary>
    public void OnSettingsChanged()
    {
        var url = StreamUrlBuilder.Build(_settings());
        if (url != _currentUrl && _wantPlaying)
        {
            StopPlayback();
            if (IsShown || _settings().Stream.KeepAliveWhenHidden) StartPlayback(force: true);
        }
        Topmost = _settings().LiveWindow.Topmost;
        UpdatePinButton();
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        StopPlayback();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideWidget();
            return;
        }
        SaveBounds();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        var player = _player;
        _player = null;
        if (player is not null)
        {
            VideoView.MediaPlayer = null;
            player.Dispose();
        }
        base.OnClosed(e);
    }

    private void RestoreWidgetBounds(WindowSettings s)
    {
        Width = Math.Max(MinWidth, s.Width);
        Height = Math.Max(MinHeight, s.Height);
        var work = SystemParameters.WorkArea;
        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (s.Left is { } l && s.Top is { } t && virt.IntersectsWith(new Rect(l, t, Width, Height)) && virt.Contains(new Point(l + 40, t + 15)))
        {
            Left = l;
            Top = t;
        }
        else
        {
            // Par défaut : coin inférieur droit de l'écran principal.
            Left = work.Right - Width - 16;
            Top = work.Bottom - Height - 16;
        }
    }

    private void SaveBounds()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        var s = _settings().LiveWindow;
        s.Left = Left;
        s.Top = Top;
        s.Width = ActualWidth;
        s.Height = ActualHeight;
        _saveSettings();
    }

    // ------------------------------------------------------------------------------------------
    // Lecture vidéo
    // ------------------------------------------------------------------------------------------

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        _player = new MediaPlayer(VlcService.Instance) { EnableHardwareDecoding = true };
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => OnStreamLost("Erreur de lecture du flux."));
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => OnStreamLost("Le flux s'est interrompu."));
        _player.Buffering += (_, e) =>
        {
            if (e.Cache < 100) Dispatcher.BeginInvoke(() => { if (OverlayPanel.Visibility == Visibility.Visible) OverlayText.Text = $"Mise en mémoire tampon… {e.Cache:0}%"; });
        };
        VideoView.MediaPlayer = _player;
        _player.Mute = _settings().Stream.StartMuted;
        UpdateMuteButton();
    }

    private void StartPlayback(bool force)
    {
        EnsurePlayer();
        var settings = _settings();
        var url = StreamUrlBuilder.Build(settings);
        TitleText.Text = string.IsNullOrWhiteSpace(settings.Frigate.Camera) ? "Sonnette" : settings.Frigate.Camera;

        if (url is null)
        {
            ShowOverlay("La sonnette n'est pas encore configurée.", showConfigure: true);
            SetStatus(StatusKind.Error);
            return;
        }
        _wantPlaying = true;
        if (!force && _player!.IsPlaying && url == _currentUrl) return;

        _currentUrl = url;
        ShowOverlay(_failures > 0 ? "Reconnexion au flux vidéo…" : "Connexion au flux vidéo…");
        SetStatus(StatusKind.Connecting);

        var media = new Media(VlcService.Instance, new Uri(url));
        var s = settings.Stream;
        media.AddOption($":network-caching={Math.Clamp(s.NetworkCachingMs, 0, 5000)}");
        media.AddOption(":clock-jitter=0");
        media.AddOption(":clock-synchro=0");
        if (s.UseTcp && url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)) media.AddOption(":rtsp-tcp");
        var player = _player!;
        // Play/Stop peuvent bloquer : hors du thread de l'interface.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                player.Stop();
                player.Play(media);
            }
            catch (Exception ex)
            {
                Log.Error("Démarrage du flux", ex);
            }
            finally
            {
                media.Dispose();
            }
        });
    }

    private void StopPlayback()
    {
        _wantPlaying = false;
        _reconnectTimer.Stop();
        _failures = 0;
        var player = _player;
        if (player is null) return;
        ThreadPool.QueueUserWorkItem(_ => { try { player.Stop(); } catch { } });
        SetStatus(StatusKind.Idle);
    }

    private void OnPlaying()
    {
        _failures = 0;
        OverlayPanel.Visibility = Visibility.Collapsed;
        SetStatus(StatusKind.Ok);
    }

    private void OnStreamLost(string message)
    {
        if (!_wantPlaying) return;
        _failures++;
        SetStatus(StatusKind.Error);
        var hint = _failures >= 3
            ? "\nVérifiez l'adresse, les identifiants et que RTSP est activé dans l'application Reolink (Réglages › Réseau › Avancé › Configuration du port)."
            : "";
        ShowOverlay($"{message} Nouvelle tentative…{hint}");
        _reconnectTimer.Interval = TimeSpan.FromSeconds(Math.Min(30, 3 * _failures));
        _reconnectTimer.Start();
    }

    private void ShowOverlay(string text, bool showConfigure = false)
    {
        OverlayText.Text = text;
        OverlayButton.Visibility = showConfigure ? Visibility.Visible : Visibility.Collapsed;
        OverlayPanel.Visibility = Visibility.Visible;
    }

    private enum StatusKind { Idle, Connecting, Ok, Error }

    private void SetStatus(StatusKind kind)
    {
        StatusDot.Fill = (Brush)FindResource(kind switch
        {
            StatusKind.Ok => "OkBrush",
            StatusKind.Error => "ErrorBrush",
            StatusKind.Connecting => "WarnBrush",
            _ => "MutedForeground",
        });
    }

    // ------------------------------------------------------------------------------------------
    // Commandes
    // ------------------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.M: ToggleMute(); e.Handled = true; break;
            case Key.S: TakeSnapshot(); e.Handled = true; break;
            case Key.R: _failures = 0; StartPlayback(force: true); e.Handled = true; break;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-clic : bascule entre petite et grande taille.
            var work = SystemParameters.WorkArea;
            if (ActualWidth < work.Width * 0.6)
            {
                Width = work.Width * 0.7;
                Height = Width * 9 / 16 + 30;
                Left = work.Left + (work.Width - Width) / 2;
                Top = work.Top + (work.Height - Height) / 2;
            }
            else
            {
                Width = 480;
                Height = 300;
                Left = work.Right - Width - 16;
                Top = work.Bottom - Height - 16;
            }
            return;
        }
        try { DragMove(); } catch (InvalidOperationException) { }
        SaveBounds();
    }

    private void ToggleMute()
    {
        if (_player is null) return;
        _player.Mute = !_player.Mute;
        UpdateMuteButton();
    }

    private void UpdateMuteButton()
    {
        var muted = _player?.Mute ?? true;
        MuteButton.Content = muted ? "" : "";
        MuteButton.ToolTip = muted ? "Activer le son (M)" : "Couper le son (M)";
    }

    private void UpdatePinButton()
    {
        PinButton.Content = Topmost ? "" : "";
        PinButton.ToolTip = Topmost ? "Ne plus garder au premier plan" : "Garder au premier plan";
    }

    private void TakeSnapshot()
    {
        if (_player is null || !_player.IsPlaying) return;
        try
        {
            Directory.CreateDirectory(AppPaths.SnapshotsDirectory);
            var path = Path.Combine(AppPaths.SnapshotsDirectory, $"sonnette-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            if (_player.TakeSnapshot(0, path, 0, 0))
                ShowEventBannerText($"Capture enregistrée : {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error("Capture", ex);
        }
    }

    private void ShowEventBannerText(string text)
    {
        EventBannerText.Text = text;
        EventBanner.Visibility = Visibility.Visible;
        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();
    private void SnapshotButton_Click(object sender, RoutedEventArgs e) => TakeSnapshot();
    private void HistoryButton_Click(object sender, RoutedEventArgs e) => _openHistory();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _openSettings();
    private void OverlayButton_Click(object sender, RoutedEventArgs e) => _openSettings();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideWidget();
    private void CloseCommand_Executed(object sender, ExecutedRoutedEventArgs e) => HideWidget();

    private void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _failures = 0;
        StartPlayback(force: true);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _settings().LiveWindow.Topmost = Topmost;
        _saveSettings();
        UpdatePinButton();
    }
}
