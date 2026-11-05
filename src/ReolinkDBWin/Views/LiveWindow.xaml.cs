using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ReolinkDBWin.Controls;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

/// <summary>
/// Widget vidéo en direct : une caméra, mosaïque, ou défilement automatique.
/// En défilement, la caméra suivante est ouverte en arrière-plan pendant l'affichage de la courante ;
/// la bascule n'a lieu que lorsqu'elle affiche déjà des images, avec la transition choisie.
/// </summary>
public partial class LiveWindow : Window
{
    private readonly Func<AppSettings> _settings;
    private readonly Action _saveSettings;
    private readonly Action _openHistory;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _bannerTimer;
    private readonly Dictionary<string, CameraStream> _streams = new();
    private readonly Dictionary<string, CameraSettings> _cameras = new();
    private readonly List<CameraTile> _gridTiles = [];

    private CarouselScheduler? _scheduler;
    private LiveViewMode _mode;
    private CameraTile _front;
    private CameraTile _back;
    private CameraSwitchAnimation? _animation;
    private string? _gridFocusId;
    private bool _built;
    private bool _allowClose;
    private bool _muted = true;
    private Point? _dragStart;

    public LiveWindow(Func<AppSettings> settings, Action saveSettings, Action openHistory, Action openSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        _openHistory = openHistory;
        _openSettings = openSettings;
        InitializeComponent();
        _front = TileA;
        _back = TileB;
        _muted = settings().Stream.StartMuted;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _tick.Tick += (_, _) => OnTick();
        _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _bannerTimer.Tick += (_, _) =>
        {
            _bannerTimer.Stop();
            EventBanner.Visibility = Visibility.Collapsed;
        };
        PreviewKeyDown += OnPreviewKeyDown;
        UpdateMuteButton();
    }

    public bool IsShown => IsVisible && WindowState != WindowState.Minimized;

    // ------------------------------------------------------------------------------------------
    // Affichage de la fenêtre
    // ------------------------------------------------------------------------------------------

    public void Toggle()
    {
        if (IsShown && (IsActive || Topmost)) HideWidget();
        else ShowWidget(activate: true);
    }

    /// <param name="cameraId">Caméra à afficher en priorité (ex. sonnette lors d'un appui).</param>
    public void ShowWidget(bool activate, string? cameraId = null)
    {
        var s = _settings().LiveWindow;
        if (!IsVisible) RestoreWidgetBounds(s);
        Topmost = s.Topmost;
        UpdatePinButton();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        ShowActivated = activate;
        Show();
        if (activate)
        {
            Activate();
            Focus();
        }
        _clock.Start();
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        if (!_built) BuildView(cameraId);
        else if (cameraId is not null) FocusCamera(cameraId, immediate: true);
        _tick.Start();
    }

    public void HideWidget()
    {
        SaveBounds();
        Hide();
        _clock.Stop();
        if (!_settings().Stream.KeepAliveWhenHidden)
        {
            _tick.Stop();
            TearDown();
        }
    }

    /// <summary>Après modification des paramètres : reconstruit l'affichage.</summary>
    public void OnSettingsChanged()
    {
        TearDown();
        Topmost = _settings().LiveWindow.Topmost;
        UpdatePinButton();
        if (IsShown || _settings().Stream.KeepAliveWhenHidden && IsLoaded) BuildView(null);
    }

    /// <summary>Une détection a eu lieu : bandeau, et bascule vers la caméra concernée si activé.</summary>
    public void OnDetection(DetectionEvent e, bool showBanner)
    {
        if (!_built) return;
        var camera = CameraMatcher.Find(_settings(), e);
        if (showBanner)
        {
            var where = camera is null || _cameras.Count <= 1 ? "" : $" ({camera.Name})";
            ShowBannerText($"{DetectionTypeInfo.NotificationTitle(e.Type)}{where} — {e.Timestamp.LocalDateTime:HH:mm:ss}");
        }
        if (camera is null || !_settings().MultiView.JumpOnDetection) return;
        if (_mode == LiveViewMode.Grid)
        {
            _gridTiles.FirstOrDefault(t => t.Stream?.Id == camera.Id)?.Flash();
            return;
        }
        FocusCamera(camera.Id, immediate: false);
    }

    public void CloseForShutdown()
    {
        _allowClose = true;
        TearDown();
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

    private void RestoreWidgetBounds(WindowSettings s)
    {
        Width = Math.Max(MinWidth, s.Width);
        Height = Math.Max(MinHeight, s.Height);
        var work = SystemParameters.WorkArea;
        var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (s.Left is { } l && s.Top is { } t && virt.Contains(new Point(l + 40, t + 15)))
        {
            Left = l;
            Top = t;
        }
        else
        {
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
    // Construction de la vue
    // ------------------------------------------------------------------------------------------

    private void BuildView(string? startId)
    {
        TearDown();
        var settings = _settings();
        var mv = settings.MultiView;
        _mode = mv.Mode;
        _cameras.Clear();
        foreach (var c in mv.EnabledCameras) _cameras[c.Id] = c;
        _built = true;

        foreach (var tile in new[] { TileA, TileB }) ConfigureTile(tile, mv);

        if (_cameras.Count == 0)
        {
            ShowOverlay("Aucune caméra activée. Ajoutez-en dans Paramètres › Caméras.");
            UpdateChrome();
            return;
        }
        var missing = _cameras.Values.Where(c => StreamUrlBuilder.Build(settings, c) is null).ToList();
        if (missing.Count == _cameras.Count)
        {
            ShowOverlay(missing.Any(c => c.Source == CameraSourceKind.Doorbell) && !settings.IsReolinkConfigured
                ? "La sonnette n'est pas encore configurée."
                : "Les caméras ne sont pas configurées (adresse manquante).");
            UpdateChrome();
            return;
        }
        OverlayPanel.Visibility = Visibility.Collapsed;

        if (_mode == LiveViewMode.Grid)
        {
            BuildGrid(mv);
        }
        else
        {
            Stage.Visibility = Visibility.Visible;
            GridPanel.Visibility = Visibility.Collapsed;
            _scheduler = new CarouselScheduler(_cameras.Values.Select(c => new CarouselItem(c.Id, TimeSpan.FromSeconds(c.DwellSeconds),
                c.Transition, TimeSpan.FromMilliseconds(c.TransitionMs))))
            {
                AutoAdvance = _mode == LiveViewMode.Carousel,
                PreloadNext = _mode == LiveViewMode.Carousel,
                MaxWait = TimeSpan.FromSeconds(Math.Max(3, mv.MaxWaitSeconds)),
            };
            _scheduler.Start(DateTimeOffset.Now, startId);
            SyncStreams();
            ShowOnFront(_scheduler.Current!.Id);
        }
        UpdateChrome();
    }

    private void ConfigureTile(CameraTile tile, MultiViewSettings mv)
    {
        tile.EnableZoom = mv.EnableMouseZoom;
        tile.MaxZoom = Math.Clamp(mv.MaxZoom, 1, 10);
        tile.ShowLed = mv.ShowStatusLed;
        tile.ShowName = mv.ShowCameraNames && _cameras.Count > 1;
    }

    private void BuildGrid(MultiViewSettings mv)
    {
        Stage.Visibility = Visibility.Collapsed;
        GridPanel.Visibility = Visibility.Visible;
        GridPanel.Children.Clear();
        _gridTiles.Clear();
        var n = _cameras.Count;
        GridPanel.Columns = mv.GridColumns > 0 ? Math.Min(mv.GridColumns, n) : (int)Math.Ceiling(Math.Sqrt(n));
        SyncStreams();
        foreach (var id in _cameras.Keys)
        {
            var tile = new CameraTile { Margin = new Thickness(1) };
            ConfigureTile(tile, mv);
            tile.ShowName = mv.ShowCameraNames;
            tile.Attach(_streams.GetValueOrDefault(id));
            tile.MouseLeftButtonDown += GridTile_MouseLeftButtonDown;
            _gridTiles.Add(tile);
            GridPanel.Children.Add(tile);
        }
        ApplyMute();
    }

    private void TearDown()
    {
        _animation?.Complete();
        _animation = null;
        TileA.Attach(null);
        TileB.Attach(null);
        foreach (var t in _gridTiles) t.Attach(null);
        _gridTiles.Clear();
        GridPanel.Children.Clear();
        foreach (var s in _streams.Values) s.Dispose();
        _streams.Clear();
        _scheduler = null;
        _gridFocusId = null;
        _built = false;
        ProgressLine.Width = 0;
        Dots.Children.Clear();
    }

    /// <summary>Ouvre les flux voulus (courant, suivant préchargé, prioritaire) et ferme les autres.</summary>
    private void SyncStreams()
    {
        var settings = _settings();
        var wanted = new HashSet<string>(_mode == LiveViewMode.Grid ? _cameras.Keys : _scheduler?.WantedIds ?? []);
        foreach (var tile in new[] { TileA, TileB }.Where(t => t.Visibility == Visibility.Visible && t.Stream is not null))
            wanted.Add(tile.Stream!.Id);
        if (_gridFocusId is not null) wanted.Add(_gridFocusId);

        foreach (var id in wanted)
        {
            if (_streams.ContainsKey(id) || !_cameras.TryGetValue(id, out var cam)) continue;
            var url = StreamUrlBuilder.Build(settings, cam);
            if (url is null) continue;
            // Mosaïque : résolution plafonnée pour ménager le processeur.
            var maxWidth = _mode == LiveViewMode.Grid ? (_cameras.Count > 4 ? 960 : 1280) : 1920;
            var stream = new CameraStream(id, cam.Name, url, settings.Stream, Dispatcher, maxWidth) { Mute = true };
            stream.StateChanged += UpdateChrome;
            _streams[id] = stream;
            stream.Start();
        }
        foreach (var id in _streams.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _streams[id].Dispose();
            _streams.Remove(id);
        }
        ApplyMute();
    }

    // ------------------------------------------------------------------------------------------
    // Défilement et transitions
    // ------------------------------------------------------------------------------------------

    private void OnTick()
    {
        if (!_built || _scheduler is null || _mode == LiveViewMode.Grid) return;
        var now = DateTimeOffset.Now;
        var before = string.Join(",", _scheduler.WantedIds);
        var sw = _scheduler.Tick(now, id => _streams.TryGetValue(id, out var s) && s.IsReady);
        if (sw is not null) PerformSwitch(sw);
        if (string.Join(",", _scheduler.WantedIds) != before) SyncStreams();
        UpdateProgress(now);
    }

    private void FocusCamera(string cameraId, bool immediate)
    {
        if (_scheduler is null) return;
        var now = DateTimeOffset.Now;
        if (immediate || _streams.TryGetValue(cameraId, out var s) && s.IsReady)
        {
            var sw = _scheduler.Show(cameraId, now);
            if (sw is not null) PerformSwitch(sw);
            // Maintien comme pour une détection
            _scheduler.Prioritize(cameraId, now, TimeSpan.FromSeconds(Math.Max(5, _settings().MultiView.DetectionHoldSeconds)));
        }
        else
        {
            _scheduler.Prioritize(cameraId, now, TimeSpan.FromSeconds(Math.Max(5, _settings().MultiView.DetectionHoldSeconds)));
        }
        SyncStreams();
        UpdateChrome();
    }

    private void ShowOnFront(string id)
    {
        _front.Attach(_streams.GetValueOrDefault(id));
        _front.Visibility = Visibility.Visible;
        _front.Opacity = 1;
        _front.RenderTransform = Transform.Identity;
        _back.Visibility = Visibility.Collapsed;
        _back.Attach(null);
        ApplyMute();
        UpdateChrome();
    }

    private void PerformSwitch(CarouselSwitch sw)
    {
        _animation?.Complete();
        if (!_streams.ContainsKey(sw.ToId)) SyncStreams();
        var incoming = _back;
        var outgoing = _front;
        incoming.Attach(_streams.GetValueOrDefault(sw.ToId));
        incoming.ResetZoom();
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        incoming.Visibility = Visibility.Visible;
        _front = incoming;
        _back = outgoing;
        ApplyMute();
        UpdateChrome();

        _animation = new CameraSwitchAnimation(incoming, outgoing, sw.Transition, sw.Duration, Stage.ActualWidth, () =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Attach(null);
            _animation = null;
            SyncStreams();
        });
        _animation.Begin();
    }

    private void Step(int direction)
    {
        if (_mode == LiveViewMode.Grid || _scheduler is null) return;
        var sw = _scheduler.Step(direction, DateTimeOffset.Now);
        if (sw is null) return;
        SyncStreams();
        PerformSwitch(sw);
    }

    private void UpdateProgress(DateTimeOffset now)
    {
        if (_scheduler is null || _mode != LiveViewMode.Carousel || _scheduler.Count < 2 || _scheduler.Paused)
        {
            ProgressLine.Width = 0;
            return;
        }
        var total = _scheduler.Current!.Dwell.TotalSeconds;
        var remaining = _scheduler.Remaining(now).TotalSeconds;
        var ratio = total <= 0 ? 0 : Math.Clamp(1 - remaining / total, 0, 1);
        ProgressLine.Width = ViewHost.ActualWidth * ratio;
    }

    /// <summary>Barre de titre, points, boutons selon le mode.</summary>
    private void UpdateChrome()
    {
        var multi = _cameras.Count > 1;
        var stageMode = _mode != LiveViewMode.Grid || _gridFocusId is not null;
        PrevButton.Visibility = NextButton.Visibility = multi && _mode != LiveViewMode.Grid ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = multi && _mode == LiveViewMode.Carousel ? Visibility.Visible : Visibility.Collapsed;
        if (_scheduler is not null)
        {
            PauseButton.Content = _scheduler.Paused ? "" : "";
            PauseButton.ToolTip = _scheduler.Paused ? "Reprendre le défilement (P)" : "Pause du défilement (P)";
        }
        ModeButton.ToolTip = _mode switch
        {
            LiveViewMode.Grid => "Mode : mosaïque — cliquer pour le défilement (V)",
            LiveViewMode.Carousel => "Mode : défilement — cliquer pour une caméra (V)",
            _ => "Mode : une caméra — cliquer pour la mosaïque (V)",
        };
        ModeButton.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;

        var current = _mode == LiveViewMode.Grid
            ? (_gridFocusId is null ? null : _cameras.GetValueOrDefault(_gridFocusId))
            : _scheduler?.Current is { } c ? _cameras.GetValueOrDefault(c.Id) : null;
        TitleText.Text = current?.Name ?? (_mode == LiveViewMode.Grid ? $"Mosaïque ({_cameras.Count})" : "Vidéo en direct");

        Dots.Children.Clear();
        if (multi && stageMode && _mode != LiveViewMode.Grid && _scheduler is not null)
        {
            for (var i = 0; i < _scheduler.Count; i++)
            {
                var id = _scheduler.Items[i].Id;
                var isCurrent = i == _scheduler.CurrentIndex;
                var ready = _streams.TryGetValue(id, out var s) && s.IsReady;
                Dots.Children.Add(new Ellipse
                {
                    Width = isCurrent ? 7 : 5,
                    Height = isCurrent ? 7 : 5,
                    Margin = new Thickness(3, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = new SolidColorBrush(isCurrent ? Colors.White : ready ? Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
                    ToolTip = _cameras.GetValueOrDefault(id)?.Name,
                });
            }
        }
    }

    private void ShowOverlay(string text)
    {
        OverlayText.Text = text;
        OverlayPanel.Visibility = Visibility.Visible;
    }

    private void ApplyMute()
    {
        // Seule la caméra visible au premier plan peut avoir du son.
        var audible = _mode == LiveViewMode.Grid && _gridFocusId is null ? null : _front.Stream;
        foreach (var s in _streams.Values) s.Mute = _muted || !ReferenceEquals(s, audible);
        UpdateMuteButton();
    }

    // ------------------------------------------------------------------------------------------
    // Commandes
    // ------------------------------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.M: ToggleMute(); break;
            case Key.S: TakeSnapshot(); break;
            case Key.R: Reconnect(); break;
            case Key.Left: Step(-1); break;
            case Key.Right: Step(+1); break;
            case Key.P: TogglePause(); break;
            case Key.V: CycleMode(); break;
            case Key.D0 or Key.NumPad0 or Key.Home: CurrentTile()?.ResetZoom(); break;
            default: return;
        }
        e.Handled = true;
    }

    private CameraTile? CurrentTile() =>
        _mode == LiveViewMode.Grid && _gridFocusId is null ? _gridTiles.FirstOrDefault(t => t.IsMouseOver) : _front;

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleLargeSize();
            return;
        }
        BeginDragMove();
    }

    private void ViewHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Les tuiles zoomées gèrent elles-mêmes le glisser (déplacement dans l'image).
        if (e.Handled) return;
        if (e.ClickCount == 2)
        {
            _dragStart = null;
            if (_mode == LiveViewMode.Grid && _gridFocusId is not null)
            {
                ExitGridFocus();
                return;
            }
            if (_mode != LiveViewMode.Grid) ToggleLargeSize();
            return;
        }
        // Le déplacement de la fenêtre ne commence qu'au mouvement, pour laisser passer les doubles-clics.
        _dragStart = e.GetPosition(this);
    }

    private void ViewHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed || Mouse.Captured is CameraTile)
        {
            _dragStart = null;
            return;
        }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - start.X) < 4 && Math.Abs(p.Y - start.Y) < 4) return;
        _dragStart = null;
        BeginDragMove();
    }

    private void GridTile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not CameraTile tile || tile.IsZoomed || tile.Stream is null) return;
        // Double-clic dans la mosaïque : afficher cette caméra en grand (flux déjà ouvert).
        _gridFocusId = tile.Stream.Id;
        GridPanel.Visibility = Visibility.Collapsed;
        Stage.Visibility = Visibility.Visible;
        ConfigureTile(_front, _settings().MultiView);
        _front.ShowName = false;
        ShowOnFront(_gridFocusId);
        e.Handled = true;
    }

    private void ExitGridFocus()
    {
        _gridFocusId = null;
        _front.Attach(null);
        Stage.Visibility = Visibility.Collapsed;
        GridPanel.Visibility = Visibility.Visible;
        ApplyMute();
        UpdateChrome();
    }

    private void BeginDragMove()
    {
        try { DragMove(); } catch (InvalidOperationException) { }
        SaveBounds();
    }

    private void ToggleLargeSize()
    {
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
        SaveBounds();
    }

    private void ToggleMute()
    {
        _muted = !_muted;
        ApplyMute();
    }

    private void UpdateMuteButton()
    {
        MuteButton.Content = _muted ? "" : "";
        MuteButton.ToolTip = _muted ? "Activer le son (M)" : "Couper le son (M)";
    }

    private void UpdatePinButton()
    {
        PinButton.Content = Topmost ? "" : "";
        PinButton.ToolTip = Topmost ? "Ne plus garder au premier plan" : "Garder au premier plan";
    }

    private void TogglePause()
    {
        if (_scheduler is null || _mode != LiveViewMode.Carousel) return;
        _scheduler.Paused = !_scheduler.Paused;
        if (!_scheduler.Paused)
        {
            // Reprise : la caméra courante repart pour sa durée complète.
            var id = _scheduler.Current!.Id;
            _scheduler.Start(DateTimeOffset.Now, id);
            SyncStreams();
        }
        ShowBannerText(_scheduler.Paused ? "⏸ Défilement en pause" : "▶ Défilement repris");
        UpdateChrome();
    }

    private void CycleMode()
    {
        if (_cameras.Count < 2) return;
        var mv = _settings().MultiView;
        mv.Mode = mv.Mode switch
        {
            LiveViewMode.Single => LiveViewMode.Grid,
            LiveViewMode.Grid => LiveViewMode.Carousel,
            _ => LiveViewMode.Single,
        };
        _saveSettings();
        var current = _scheduler?.Current?.Id;
        BuildView(current);
        ShowBannerText(mv.Mode switch
        {
            LiveViewMode.Grid => "Mode mosaïque",
            LiveViewMode.Carousel => "Mode défilement",
            _ => "Mode une caméra",
        });
    }

    private void Reconnect()
    {
        if (_mode == LiveViewMode.Grid && _gridFocusId is null)
            foreach (var s in _streams.Values) s.Restart();
        else
            _front.Stream?.Restart();
    }

    private void TakeSnapshot()
    {
        var stream = CurrentTile()?.Stream ?? _front.Stream;
        if (stream is null) return;
        try
        {
            var name = string.Concat(stream.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = System.IO.Path.Combine(AppPaths.SnapshotsDirectory, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            if (stream.SaveSnapshot(path)) ShowBannerText($"Capture enregistrée : {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Log.Error("Capture", ex);
        }
    }

    private void ShowBannerText(string text)
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
    private void PrevButton_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void NextButton_Click(object sender, RoutedEventArgs e) => Step(+1);
    private void PauseButton_Click(object sender, RoutedEventArgs e) => TogglePause();
    private void ModeButton_Click(object sender, RoutedEventArgs e) => CycleMode();

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        _settings().LiveWindow.Topmost = Topmost;
        _saveSettings();
        UpdatePinButton();
    }
}

/// <summary>Animation de transition entre deux calques vidéo (coupure, fondu, glissement, zoom).</summary>
internal sealed class CameraSwitchAnimation
{
    private readonly FrameworkElement _incoming;
    private readonly FrameworkElement _outgoing;
    private readonly TransitionKind _kind;
    private readonly TimeSpan _duration;
    private readonly double _width;
    private readonly Action _completed;
    private bool _done;

    public CameraSwitchAnimation(FrameworkElement incoming, FrameworkElement outgoing, TransitionKind kind, TimeSpan duration, double width, Action completed)
    {
        _incoming = incoming;
        _outgoing = outgoing;
        _kind = duration <= TimeSpan.Zero ? TransitionKind.Cut : kind;
        _duration = duration;
        _width = Math.Max(1, width);
        _completed = completed;
    }

    public void Begin()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        DoubleAnimation Anim(double from, double to) => new(from, to, _duration) { EasingFunction = ease };
        switch (_kind)
        {
            case TransitionKind.Fade:
            {
                _incoming.Opacity = 0;
                var a = Anim(0, 1);
                a.Completed += (_, _) => Complete();
                _incoming.BeginAnimation(UIElement.OpacityProperty, a);
                break;
            }
            case TransitionKind.Slide:
            {
                _incoming.Opacity = 1;
                var tin = new TranslateTransform(_width, 0);
                var tout = new TranslateTransform(0, 0);
                _incoming.RenderTransform = tin;
                _outgoing.RenderTransform = tout;
                var a = Anim(_width, 0);
                a.Completed += (_, _) => Complete();
                tin.BeginAnimation(TranslateTransform.XProperty, a);
                tout.BeginAnimation(TranslateTransform.XProperty, Anim(0, -_width));
                break;
            }
            case TransitionKind.Zoom:
            {
                _incoming.Opacity = 0;
                _incoming.RenderTransformOrigin = new Point(0.5, 0.5);
                var scale = new ScaleTransform(1.12, 1.12);
                _incoming.RenderTransform = scale;
                var a = Anim(0, 1);
                a.Completed += (_, _) => Complete();
                _incoming.BeginAnimation(UIElement.OpacityProperty, a);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(1.12, 1));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(1.12, 1));
                break;
            }
            default:
                _incoming.Opacity = 1;
                Complete();
                break;
        }
    }

    /// <summary>Termine immédiatement (aussi utilisé si une autre transition démarre).</summary>
    public void Complete()
    {
        if (_done) return;
        _done = true;
        _incoming.BeginAnimation(UIElement.OpacityProperty, null);
        _incoming.Opacity = 1;
        _incoming.RenderTransform = Transform.Identity;
        _outgoing.RenderTransform = Transform.Identity;
        _completed();
    }
}
