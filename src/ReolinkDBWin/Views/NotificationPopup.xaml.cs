using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

/// <summary>
/// Fenêtre d'alerte Vigie (coin inférieur droit) : image entière de la caméra concernée,
/// remplacée par la vidéo en direct dès qu'elle est prête, et actions sous l'image.
/// </summary>
public partial class NotificationPopup : Window
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<DetectionEvent> _openLive;
    private readonly Action<string> _openClip;
    private readonly DispatcherTimer _closeTimer;
    private CameraStream? _stream;
    private DetectionEvent? _event;
    private DateTime _closeAt;
    private TimeSpan _duration;

    public NotificationPopup(Func<AppSettings> settings, Action<DetectionEvent> openLive, Action<string> openClip)
    {
        _settings = settings;
        _openLive = openLive;
        _openClip = openClip;
        InitializeComponent();
        LiveTile.ShowStatus = false;
        LiveTile.ShowName = false;
        LiveTile.EnableZoom = false;
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _closeTimer.Tick += (_, _) => OnCountdown();
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) =>
        {
            if (_closeAt - DateTime.Now < TimeSpan.FromSeconds(5)) _closeAt = DateTime.Now.AddSeconds(5);
            _closeTimer.Start();
        };
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Affiche (ou met à jour) l'alerte pour un événement.</summary>
    public void ShowEvent(DetectionEvent e, byte[]? image, CameraSettings? camera)
    {
        _event = e;
        var settings = _settings();
        TitleText.Text = DetectionTypeInfo.NotificationTitle(e.Type);
        var parts = new List<string> { e.Timestamp.LocalDateTime.ToString("HH:mm:ss") };
        var where = camera?.Name ?? e.Camera;
        if (!string.IsNullOrWhiteSpace(where)) parts.Add(where);
        if (e.Type == DetectionType.Other && !string.IsNullOrEmpty(e.RawLabel)) parts.Add(e.RawLabel);
        if (e.Score is { } score) parts.Add($"{score:P0}");
        SubText.Text = string.Join(" · ", parts);
        ClipButton.Visibility = e.FrigateEventId is null ? Visibility.Collapsed : Visibility.Visible;

        var bitmap = ImageHelper.ToBitmap(image, 800);
        if (bitmap is not null || _stream is null) SnapshotImage.Source = bitmap;

        // Vidéo en direct de la caméra concernée (la sonnette par défaut).
        var url = camera is null ? StreamUrlBuilder.Build(settings) : StreamUrlBuilder.Build(settings, camera);
        if (url is not null && _stream?.Url != url)
        {
            DisposeStream();
            _stream = new CameraStream(camera?.Id ?? "popup", camera?.Name ?? "Sonnette", url, settings.Stream, Dispatcher, 960) { Mute = true };
            _stream.StateChanged += UpdateLiveBadge;
            LiveTile.Attach(_stream);
            _stream.Start();
        }
        LiveTile.ShowLed = settings.MultiView.ShowStatusLed;
        UpdateLiveBadge();

        _duration = TimeSpan.FromSeconds(Math.Max(5, e.Type == DetectionType.Doorbell
            ? Math.Max(60, settings.Notifications.PopupSeconds)
            : settings.Notifications.PopupSeconds));
        _closeAt = DateTime.Now + _duration;
        _closeTimer.Start();

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            Reposition();
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        if (settings.Notifications.Sound)
        {
            if (e.Type == DetectionType.Doorbell) SystemSounds.Exclamation.Play();
            else SystemSounds.Asterisk.Play();
        }
    }

    public void Dismiss()
    {
        _closeTimer.Stop();
        DisposeStream();
        SnapshotImage.Source = null;
        Hide();
    }

    private void UpdateLiveBadge() =>
        LiveBadge.Visibility = _stream?.IsReady == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnCountdown()
    {
        var left = _closeAt - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            Dismiss();
            return;
        }
        var width = Math.Max(0, ActualWidth - 44);
        Countdown.Width = width * Math.Clamp(left.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
    }

    private void Reposition()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Right - ActualWidth;
        Top = work.Bottom - ActualHeight;
    }

    private void DisposeStream()
    {
        if (_stream is null) return;
        _stream.StateChanged -= UpdateLiveBadge;
        LiveTile.Attach(null);
        _stream.Dispose();
        _stream = null;
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e) => OpenLive();

    private void MediaArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenLive();

    private void OpenLive()
    {
        var ev = _event;
        Dismiss();
        if (ev is not null) _openLive(ev);
    }

    private void ClipButton_Click(object sender, RoutedEventArgs e)
    {
        var id = _event?.FrigateEventId;
        Dismiss();
        if (id is not null) _openClip(id);
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Dismiss();

    protected override void OnClosed(EventArgs e)
    {
        DisposeStream();
        base.OnClosed(e);
    }
}
