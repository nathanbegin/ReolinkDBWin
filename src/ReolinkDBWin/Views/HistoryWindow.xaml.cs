using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using Microsoft.Win32;
using ReolinkDBWin.Core.Frigate;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Services;

namespace ReolinkDBWin.Views;

/// <summary>Historique des événements Frigate : recherche, vignettes et lecture des clips.</summary>
public partial class HistoryWindow : Window
{
    private const int PageSize = 50;

    private readonly Func<AppSettings> _settings;
    private readonly ObservableCollection<EventItem> _items = [];
    private readonly DispatcherTimer _progressTimer;
    private FrigateClient? _client;
    private MediaPlayer? _player;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _clipCts;
    private FrigateEventQuery? _lastQuery;
    private string? _pendingFocusId;
    private string? _currentClipPath;
    private bool _seeking;
    private bool _initialized;
    private Task _stopTask = Task.CompletedTask;

    public HistoryWindow(Func<AppSettings> settings)
    {
        _settings = settings;
        InitializeComponent();
        EventList.ItemsSource = _items;
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progressTimer.Tick += (_, _) => UpdateProgress();
        FromDate.SelectedDate = DateTime.Today.AddDays(-7);
        ToDate.SelectedDate = DateTime.Today;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += async (_, _) => await InitializeAsync();
    }

    /// <summary>Ouvre directement un événement (depuis une notification).</summary>
    public async void FocusEvent(string eventId)
    {
        var existing = _items.FirstOrDefault(i => i.Event.Id == eventId);
        if (existing is not null)
        {
            EventList.SelectedItem = existing;
            EventList.ScrollIntoView(existing);
            return;
        }
        if (!_initialized)
        {
            _pendingFocusId = eventId;
            return;
        }
        try
        {
            var e = _client is null ? null : await _client.GetEventAsync(eventId);
            if (e is null) return;
            var item = new EventItem(e);
            _items.Insert(0, item);
            EventList.SelectedItem = item;
            EventList.ScrollIntoView(item);
            _ = LoadThumbnailAsync(item, CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetStatus("Impossible d'ouvrir l'événement : " + ex.Message);
        }
    }

    /// <summary>Recrée le client après modification des paramètres.</summary>
    public async void ReloadSettings()
    {
        _initialized = false;
        await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        var s = _settings();
        _client?.Dispose();
        _client = null;
        if (!s.Frigate.IsConfigured)
        {
            SetStatus("Frigate n'est pas configuré. Ouvrez les paramètres › Frigate.");
            ShowOverlay("Configurez l'adresse de votre serveur Frigate dans les paramètres pour consulter l'historique.");
            return;
        }
        _client = EventHub.CreateFrigateClient(s.Frigate);
        SetStatus("Connexion à Frigate…");
        try
        {
            var cameras = await _client.GetCamerasAsync();
            CameraCombo.Items.Clear();
            CameraCombo.Items.Add("Toutes les caméras");
            foreach (var c in cameras) CameraCombo.Items.Add(c);
            CameraCombo.SelectedItem = cameras.Contains(s.Frigate.Camera) ? s.Frigate.Camera : CameraCombo.Items[0];

            IReadOnlyList<string> labels;
            try { labels = await _client.GetLabelsAsync(); }
            catch { labels = LabelNames.Defaults; }
            LabelCombo.Items.Clear();
            LabelCombo.Items.Add(new LabelChoice(null, "Tous les objets"));
            foreach (var l in labels.Union(LabelNames.Defaults).Distinct().OrderBy(LabelNames.Get))
                LabelCombo.Items.Add(new LabelChoice(l, LabelNames.Get(l)));
            LabelCombo.SelectedIndex = 0;

            _initialized = true;
            await SearchAsync(append: false);
            if (_pendingFocusId is { } id)
            {
                _pendingFocusId = null;
                FocusEvent(id);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Erreur Frigate : " + ex.Message);
            ShowOverlay("Impossible de joindre Frigate.\n" + ex.Message);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Recherche
    // ------------------------------------------------------------------------------------------

    private FrigateEventQuery BuildQuery()
    {
        var camera = CameraCombo.SelectedIndex > 0 ? CameraCombo.SelectedItem as string : null;
        var label = (LabelCombo.SelectedItem as LabelChoice)?.Label;
        var now = DateTimeOffset.Now;
        DateTimeOffset? after = null, before = null;
        switch ((RangeCombo.SelectedItem as ComboBoxItem)?.Tag as string)
        {
            case "1h": after = now.AddHours(-1); break;
            case "today": after = new DateTimeOffset(DateTime.Today); break;
            case "24h": after = now.AddHours(-24); break;
            case "7d": after = now.AddDays(-7); break;
            case "30d": after = now.AddDays(-30); break;
            case "custom":
                if (FromDate.SelectedDate is { } f) after = new DateTimeOffset(f.Date);
                if (ToDate.SelectedDate is { } t) before = new DateTimeOffset(t.Date.AddDays(1));
                break;
        }
        return new FrigateEventQuery
        {
            Cameras = camera is null ? [] : [camera],
            Labels = label is null ? [] : [label],
            After = after,
            Before = before,
            Limit = PageSize,
            HasClip = ClipOnlyCheck.IsChecked == true ? true : null,
        };
    }

    private async Task SearchAsync(bool append)
    {
        if (_client is null) return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        FrigateEventQuery query;
        if (append && _lastQuery is not null && _items.Count > 0)
            query = _lastQuery with { Before = _items[^1].Event.StartTime.AddMilliseconds(-1) };
        else
            query = BuildQuery();
        if (!append) _lastQuery = query;

        SearchButton.IsEnabled = false;
        MoreButton.IsEnabled = false;
        SetStatus("Recherche…");
        try
        {
            var events = await _client.GetEventsAsync(query, ct);
            if (!append) _items.Clear();
            var newItems = events.Where(e => _items.All(i => i.Event.Id != e.Id)).Select(e => new EventItem(e)).ToList();
            foreach (var i in newItems) _items.Add(i);
            MoreButton.Visibility = events.Count >= PageSize ? Visibility.Visible : Visibility.Collapsed;
            SetStatus(_items.Count == 0 ? "Aucun événement pour ces critères." : $"{_items.Count} événement(s).");
            if (!append && _items.Count > 0 && EventList.SelectedItem is null) EventList.SelectedIndex = 0;
            _ = LoadThumbnailsAsync(newItems, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus("Erreur : " + ex.Message);
        }
        finally
        {
            SearchButton.IsEnabled = true;
            MoreButton.IsEnabled = true;
        }
    }

    private async Task LoadThumbnailsAsync(IReadOnlyList<EventItem> items, CancellationToken ct)
    {
        using var throttle = new SemaphoreSlim(4);
        await Task.WhenAll(items.Select(async item =>
        {
            await throttle.WaitAsync(ct);
            try { await LoadThumbnailAsync(item, ct); }
            finally { throttle.Release(); }
        }));
    }

    private async Task LoadThumbnailAsync(EventItem item, CancellationToken ct)
    {
        if (_client is null) return;
        try
        {
            var bytes = await _client.GetThumbnailAsync(item.Event.Id, ct);
            item.Thumbnail = ImageHelper.ToBitmap(bytes, 240);
        }
        catch
        {
            // Vignette absente : sans gravité.
        }
    }

    // ------------------------------------------------------------------------------------------
    // Lecture
    // ------------------------------------------------------------------------------------------

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        _player = new MediaPlayer(VlcService.Instance);
        _player.Playing += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            PlayerOverlay.Visibility = Visibility.Collapsed;
            SnapshotImage.Visibility = Visibility.Collapsed;
            PlayButton.Content = "";
            ApplySpeed();
        });
        _player.Paused += (_, _) => Dispatcher.BeginInvoke(() => PlayButton.Content = "");
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(() => PlayButton.Content = "");
        _player.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => ShowOverlay("Impossible de lire ce clip."));
        Player.MediaPlayer = _player;
    }

    private async void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is not EventItem item || _client is null) return;
        _clipCts?.Cancel();
        _clipCts = new CancellationTokenSource();
        var ct = _clipCts.Token;
        EnsurePlayer();
        var player = _player!;
        var previousStop = _stopTask;
        _stopTask = Task.Run(async () =>
        {
            await previousStop;
            try { player.Stop(); } catch (Exception ex) { Log.Error("Arrêt du clip", ex); }
        });
        await _stopTask;
        _currentClipPath = null;
        SaveButton.IsEnabled = false;
        PlayButton.IsEnabled = false;
        SeekSlider.IsEnabled = false;
        SeekSlider.Value = 0;
        TimeText.Text = "00:00 / 00:00";

        if (!item.Event.HasClip)
        {
            await ShowSnapshotAsync(item, "Pas de vidéo pour cet événement — instantané affiché.", ct);
            return;
        }
        if (item.Event.InProgress)
            SetStatus("Événement en cours : le clip peut être incomplet.");

        try
        {
            var path = Path.Combine(AppPaths.CacheDirectory, $"clip-{Sanitize(item.Event.Id)}.mp4");
            if (!File.Exists(path) || item.Event.InProgress)
            {
                Directory.CreateDirectory(AppPaths.CacheDirectory);
                var tmp = path + ".part";
                ShowOverlay("Téléchargement du clip…");
                var progress = new Progress<double>(p => PlayerOverlayText.Text = $"Téléchargement du clip… {p:P0}");
                await using (var fs = File.Create(tmp))
                    await _client.DownloadClipAsync(item.Event.Id, fs, progress, ct);
                File.Move(tmp, path, overwrite: true);
            }
            ct.ThrowIfCancellationRequested();
            _currentClipPath = path;
            using var media = new Media(VlcService.Instance, new Uri(path));
            player.Play(media);
            _progressTimer.Start();
            SaveButton.IsEnabled = true;
            PlayButton.IsEnabled = true;
            SeekSlider.IsEnabled = true;
            SetStatus($"{item.Title} — {item.When}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Lecture du clip", ex);
            await ShowSnapshotAsync(item, "Impossible de charger le clip : " + ex.Message, ct);
        }
    }

    private async Task ShowSnapshotAsync(EventItem item, string message, CancellationToken ct)
    {
        if (_client is null) return;
        try
        {
            var bytes = await _client.GetSnapshotAsync(item.Event.Id, ct);
            SnapshotImage.Source = ImageHelper.ToBitmap(bytes);
            SnapshotImage.Visibility = Visibility.Visible;
            PlayerOverlay.Visibility = Visibility.Collapsed;
            SetStatus(message);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            ShowOverlay(message);
        }
    }

    private void UpdateProgress()
    {
        if (_player is null || _seeking) return;
        var length = _player.Length;
        var time = _player.Time;
        if (length > 0) SeekSlider.Value = Math.Clamp((double)time / length, 0, 1);
        TimeText.Text = $"{Format(time)} / {Format(length)}";
    }

    private static string Format(long ms) => ms <= 0 ? "00:00" : TimeSpan.FromMilliseconds(ms).ToString(ms >= 3_600_000 ? @"h\:mm\:ss" : @"mm\:ss");

    private void SeekSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void SeekSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        if (_player is null) return;
        if (!_player.IsPlaying && _player.State == VLCState.Ended && _currentClipPath is not null)
        {
            using var media = new Media(VlcService.Instance, new Uri(_currentClipPath));
            _player.Play(media);
        }
        _player.Position = (float)SeekSlider.Value;
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_player is null || _currentClipPath is null) return;
        if (_player.State == VLCState.Ended || _player.State == VLCState.Stopped)
        {
            using var media = new Media(VlcService.Instance, new Uri(_currentClipPath));
            _player.Play(media);
        }
        else
        {
            _player.Pause();
        }
    }

    private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplySpeed();

    private void ApplySpeed()
    {
        if (_player is null || SpeedCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        if (float.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
            _player.SetRate(rate);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox or ComboBox or DatePicker) return;
        if (e.Key == Key.Space)
        {
            TogglePlay();
            e.Handled = true;
        }
        else if (_player is not null && e.Key is Key.Left or Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _player.Time = Math.Max(0, _player.Time + (e.Key == Key.Right ? 5000 : -5000));
            e.Handled = true;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not EventItem item || _currentClipPath is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Enregistrer le clip",
            Filter = "Vidéo MP4 (*.mp4)|*.mp4",
            FileName = $"{item.Event.Camera}-{item.Event.Label}-{item.Event.StartTime.LocalDateTime:yyyyMMdd-HHmmss}.mp4",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            await Task.Run(() => File.Copy(_currentClipPath, dlg.FileName, overwrite: true));
            SetStatus("Clip enregistré : " + dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("Erreur d'enregistrement : " + ex.Message);
        }
    }

    private async void SnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        if (EventList.SelectedItem is not EventItem item) return;
        if (_player is not null && _player.IsPlaying) _player.Pause();
        await ShowSnapshotAsync(item, "Instantané de l'événement.", CancellationToken.None);
    }

    private void OpenInFrigate_Click(object sender, RoutedEventArgs e)
    {
        var s = _settings().Frigate;
        if (!s.IsConfigured) return;
        try
        {
            var baseUri = new Uri(s.BaseUrl.Contains("://") ? s.BaseUrl : "http://" + s.BaseUrl);
            var target = new Uri(baseUri, "/review");
            Process.Start(new ProcessStartInfo(target.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Impossible d'ouvrir le navigateur : " + ex.Message);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Divers
    // ------------------------------------------------------------------------------------------

    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await SearchAsync(append: false);
    private async void MoreButton_Click(object sender, RoutedEventArgs e) => await SearchAsync(append: true);

    private void RangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomDates is null) return;
        CustomDates.Visibility = (RangeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowOverlay(string text)
    {
        PlayerOverlayText.Text = text;
        PlayerOverlay.Visibility = Visibility.Visible;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private static string Sanitize(string id) => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    protected override void OnClosed(EventArgs e)
    {
        _progressTimer.Stop();
        _searchCts?.Cancel();
        _clipCts?.Cancel();
        var player = _player;
        _player = null;
        Player.MediaPlayer = null;
        if (player is not null)
        {
            var pendingStop = _stopTask;
            Task.Run(async () =>
            {
                // Jamais de Dispose pendant un Stop encore en cours (crash natif).
                await pendingStop;
                try { player.Stop(); } catch { }
                player.Dispose();
            });
        }
        _client?.Dispose();
        AppPaths.CleanCache(TimeSpan.FromDays(2));
        base.OnClosed(e);
    }

    private sealed record LabelChoice(string? Label, string Display);

    /// <summary>Élément de la liste d'événements.</summary>
    public sealed class EventItem(FrigateEvent e) : INotifyPropertyChanged
    {
        private BitmapImage? _thumbnail;

        public FrigateEvent Event { get; } = e;

        public string Title => LabelNames.Get(Event.Label) + (Event.SubLabel is null ? "" : $" — {Event.SubLabel}");

        public string When
        {
            get
            {
                var t = Event.StartTime.LocalDateTime;
                var day = t.Date == DateTime.Today ? "Aujourd'hui" : t.Date == DateTime.Today.AddDays(-1) ? "Hier" : t.ToString("ddd d MMM");
                return $"{day} à {t:HH:mm:ss}";
            }
        }

        public string Details
        {
            get
            {
                var parts = new List<string> { Event.Camera };
                if (Event.Score is { } s) parts.Add($"{s:P0}");
                if (Event.InProgress) parts.Add("en cours");
                else if (Event.Duration is { } d) parts.Add(d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds} s" : $"{d.Seconds} s");
                if (Event.Zones.Count > 0) parts.Add(string.Join(", ", Event.Zones));
                if (!Event.HasClip) parts.Add("sans vidéo");
                return string.Join(" · ", parts);
            }
        }

        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set { _thumbnail = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
