using ReolinkDBWin.Core.Events;
using ReolinkDBWin.Core.Frigate;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Reolink;
using ReolinkDBWin.Core.Settings;

namespace ReolinkDBWin.Services;

/// <summary>
/// Orchestration : démarre les surveillances selon la configuration (Reolink, Frigate, Home Assistant),
/// filtre les événements, ajoute une image et affiche la notification.
/// </summary>
public sealed class EventHub : IAsyncDisposable
{
    private readonly NotificationService _notifications;
    private readonly NotificationFilter _filter = new();
    private readonly List<EventMonitor> _monitors = [];
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<EventSource, MonitorStatus> _statuses = new();
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private readonly object _recentGate = new();
    private readonly LinkedList<DetectionEvent> _recent = new();
    private AppSettings _settings = new();
    private ReolinkClient? _reolink;
    private FrigateClient? _frigate;

    public EventHub(NotificationService notifications)
    {
        _notifications = notifications;
    }

    /// <summary>Quelqu'un a sonné (et l'ouverture automatique du widget est activée).</summary>
    public event Action? DoorbellPressed;
    public event Action<DetectionEvent>? EventReceived;
    public event Action? StatusChanged;

    public bool Paused { get; set; }

    public IReadOnlyDictionary<EventSource, MonitorStatus> Statuses
    {
        get { lock (_statuses) return new Dictionary<EventSource, MonitorStatus>(_statuses); }
    }

    public IReadOnlyList<DetectionEvent> RecentEvents
    {
        get { lock (_recentGate) return _recent.ToList(); }
    }

    /// <summary>Client Reolink partagé (instantanés), ou null si non configuré.</summary>
    public ReolinkClient? Reolink => _reolink;
    public FrigateClient? Frigate => _frigate;

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        await _restartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAllAsync().ConfigureAwait(false);
            _settings = settings;
            _filter.Reset();

            if (settings.IsReolinkConfigured)
            {
                _reolink = new ReolinkClient(new ReolinkConnectionInfo
                {
                    Host = settings.Reolink.Host.Trim(),
                    Port = settings.Reolink.Port,
                    UseHttps = settings.Reolink.UseHttps,
                    Username = settings.Reolink.Username,
                    Password = settings.Reolink.Password,
                    AcceptInvalidCertificate = settings.Reolink.AcceptInvalidCertificate,
                });
                if (settings.Reolink.MonitorEvents)
                    AddMonitor(new ReolinkEventMonitor(_reolink, settings.Reolink.Channel, TimeSpan.FromMilliseconds(settings.Reolink.PollIntervalMs)));
            }

            if (settings.Frigate.IsConfigured)
            {
                _frigate = CreateFrigateClient(settings.Frigate);
                // Frigate est aussi surveillé (sans notifier) pour basculer le widget vers la caméra d'une détection.
                var jumpCameras = settings.MultiView.JumpOnDetection
                    && settings.MultiView.EnabledCameras.Any(c => c.Source != CameraSourceKind.Doorbell && c.EffectiveFrigateCamera(settings) is not null);
                if (settings.Notifications.FromFrigate || jumpCameras)
                {
                    var frigateMonitorClient = CreateFrigateClient(settings.Frigate);
                    _owned.Add(frigateMonitorClient);
                    AddMonitor(new FrigateEventMonitor(frigateMonitorClient, CameraMatcher.FrigateCamerasToWatch(settings), settings.Frigate.NotifyLabels,
                        TimeSpan.FromSeconds(settings.Frigate.PollIntervalSeconds)));
                }
            }

            if (settings.HomeAssistant.IsConfigured && settings.Notifications.FromHomeAssistant)
            {
                AddMonitor(new HomeAssistantEventMonitor(settings.HomeAssistant.BaseUrl, settings.HomeAssistant.AccessToken,
                    settings.HomeAssistant.EntityIds, settings.HomeAssistant.AcceptInvalidCertificate));
            }

            foreach (var m in _monitors) m.Start();
            StatusChanged?.Invoke();
        }
        finally
        {
            _restartLock.Release();
        }
    }

    public static FrigateClient CreateFrigateClient(FrigateSettings s) => new(new FrigateConnectionInfo
    {
        BaseUrl = s.BaseUrl,
        Username = s.Username,
        Password = s.Password,
        AcceptInvalidCertificate = s.AcceptInvalidCertificate,
    });

    private void AddMonitor(EventMonitor m)
    {
        m.Detected += (_, e) => _ = HandleAsync(e);
        m.StatusChanged += (_, s) =>
        {
            lock (_statuses) _statuses[s.Source] = s;
            Log.Info($"{s.Source} : {s.State} — {s.Message}");
            StatusChanged?.Invoke();
        };
        _monitors.Add(m);
    }

    /// <summary>Affiche une notification de test (vérifie que Windows les autorise).</summary>
    public Task SendTestAsync(DetectionType type = DetectionType.Doorbell) =>
        HandleAsync(new DetectionEvent { Source = EventSource.Test, Type = type, RawLabel = "test", Camera = "Test" });

    private async Task HandleAsync(DetectionEvent e)
    {
        try
        {
            Log.Info($"Événement {e.Source}/{e.Type} ({e.RawLabel})");
            lock (_recentGate)
            {
                _recent.AddFirst(e with { Image = null });
                while (_recent.Count > 100) _recent.RemoveLast();
            }
            EventReceived?.Invoke(e);

            if (Paused && e.Source != EventSource.Test) return;
            var settings = _settings;
            if (e.Source == EventSource.Frigate && !IsNotifiedFrigateCamera(settings, e.Camera)) return;
            if (!_filter.ShouldNotify(e, settings.Notifications, DateTimeOffset.Now)) return;

            if (e.Type == DetectionType.Doorbell && settings.Notifications.OpenLiveOnDoorbell)
                DoorbellPressed?.Invoke();

            string? imagePath = null;
            if (settings.Notifications.IncludeSnapshot)
            {
                var image = e.Image ?? await TryGetSnapshotAsync().ConfigureAwait(false);
                imagePath = ImageHelper.SaveToCache(image, $"notif-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
            }
            _notifications.Show(e, imagePath, settings.Notifications.Sound, _frigate is not null);
        }
        catch (Exception ex)
        {
            Log.Error("Traitement d'un événement", ex);
        }
    }

    private static bool IsNotifiedFrigateCamera(AppSettings settings, string? camera)
    {
        var notify = CameraMatcher.FrigateCamerasToNotify(settings);
        return notify.Count == 0 || notify.Contains(camera ?? "", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Instantané : Reolink en priorité, sinon dernière image de la caméra dans Frigate.</summary>
    public async Task<byte[]?> TryGetSnapshotAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        if (_reolink is { } r)
        {
            try { return await r.GetSnapshotAsync(_settings.Reolink.Channel, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Log.Error("Instantané Reolink", ex); }
        }
        if (_frigate is { } f && !string.IsNullOrWhiteSpace(_settings.Frigate.Camera))
        {
            try { return await f.GetLatestCameraImageAsync(_settings.Frigate.Camera, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Log.Error("Instantané Frigate", ex); }
        }
        return null;
    }

    private async Task StopAllAsync()
    {
        foreach (var m in _monitors)
        {
            try { await m.StopAsync().ConfigureAwait(false); } catch { }
        }
        _monitors.Clear();
        foreach (var d in _owned) d.Dispose();
        _owned.Clear();
        lock (_statuses) _statuses.Clear();
        _reolink?.Dispose();
        _reolink = null;
        _frigate?.Dispose();
        _frigate = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        _restartLock.Dispose();
    }
}
