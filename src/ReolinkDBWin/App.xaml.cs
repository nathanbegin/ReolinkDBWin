using System.Windows;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using ReolinkDBWin.Core.Licensing;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Services;
using ReolinkDBWin.Views;

namespace ReolinkDBWin;

public partial class App : Application
{
    private const string MutexName = @"Local\ReolinkDBWin.SingleInstance";
    private const string ShowEventName = @"Local\ReolinkDBWin.ShowLive";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private SettingsStore _store = null!;
    private AppSettings _settings = new();
    private NotificationService _notifications = null!;
    private EventHub _hub = null!;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private LiveWindow? _live;
    private HistoryWindow? _history;
    private SettingsWindow? _settingsWindow;
    private LicenseManager _license = null!;
    private LicenseWindow? _licenseWindow;
    private DispatcherTimer? _licenseTimer;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Exception non gérée", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Tâche non observée", args.Exception);
            args.SetObserved();
        };

        var toastActivated = ToastNotificationManagerCompat.WasCurrentProcessToastActivated();
        if (!AcquireSingleInstance())
        {
            // Une instance tourne déjà : on lui demande d'afficher le widget et on quitte.
            if (!toastActivated) SignalExistingInstance();
            Shutdown();
            return;
        }

        Log.Info("Démarrage");
        try
        {
            VlcService.Initialize();
        }
        catch (Exception ex)
        {
            Log.Error("Initialisation de LibVLC", ex);
            MessageBox.Show("Impossible d'initialiser le lecteur vidéo (LibVLC).\n\n" + ex.Message, AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        _store = new SettingsStore(AppPaths.SettingsFile, new DpapiSecretProtector());
        _settings = _store.Load();

        // Licence : essai gratuit, puis abonnement annuel.
        _license = new LicenseManager(LicensePublicKey.Value, new RegistryLicenseStore());
        var licenseStatus = _license.Evaluate();
        Log.Info("Licence : " + licenseStatus.Message);
        if (!licenseStatus.AllowsUse && !ShowLicenseDialog(blocking: true))
        {
            await ExitAsync();
            return;
        }

        _tray = new TrayIcon(
            showLive: () => ShowLive(activate: true),
            showHistory: () => ShowHistory(),
            showSettings: ShowSettings,
            setPaused: paused =>
            {
                _hub.Paused = paused;
                UpdateTooltip();
            },
            testNotification: () => _ = _hub.SendTestAsync(),
            showLicense: () => ShowLicenseDialog(blocking: false),
            exit: () => _ = ExitAsync());

        _notifications = new NotificationService((title, body) => Dispatcher.BeginInvoke(() => _tray?.ShowBalloon(title, body)));
        _notifications.Activated += args => Dispatcher.BeginInvoke(() => HandleToastActivation(args));
        _notifications.Initialize();

        _hub = new EventHub(_notifications);
        _hub.DoorbellPressed += () => Dispatcher.BeginInvoke(() => ShowLive(activate: false, CameraSettings.DoorbellId));
        _hub.EventReceived += ev => Dispatcher.BeginInvoke(() =>
        {
            if (_live?.IsShown != true || ev.Source == EventSource.Test) return;
            _live.OnDetection(ev, showBanner: _settings.Notifications.IsTypeEnabled(ev.Type));
        });
        _hub.StatusChanged += () => Dispatcher.BeginInvoke(UpdateTooltip);

        _live = new LiveWindow(() => _settings, SaveSettings, () => ShowHistory(), ShowSettings);
        _hotkeys = new HotkeyManager();
        RegisterHotkeys(showErrors: true);
        StartListeningForSecondInstance();

        await _hub.ApplySettingsAsync(_settings);
        StartupRegistration.Apply(_settings.StartWithWindows);
        _tray.SetHistoryEnabled(_settings.Frigate.IsConfigured);
        AppPaths.CleanCache(TimeSpan.FromDays(2));
        StartLicenseTimer();

        var minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (!_settings.IsReolinkConfigured && !_settings.FirstRunCompleted)
        {
            ShowSettings();
        }
        else if (_license.Evaluate() is { ShouldRemind: true } reminder)
        {
            _tray.ShowBalloon(AppInfo.ProductName, reminder.Message);
        }
        else if (!minimized && !toastActivated)
        {
            _tray.ShowBalloon($"{AppInfo.ProductName} est actif",
                $"{_settings.Hotkeys.ToggleLive} : vidéo en direct · {_settings.Hotkeys.OpenHistory} : historique. Clic droit sur l'icône pour les options.");
        }
    }

    // ------------------------------------------------------------------------------------------
    // Fenêtres
    // ------------------------------------------------------------------------------------------

    private void ShowLive(bool activate, string? cameraId = null)
    {
        if (_exiting || _live is null || !EnsureLicensed()) return;
        _live.ShowWidget(activate, cameraId);
    }

    private void ToggleLive()
    {
        if (_exiting || _live is null || !EnsureLicensed()) return;
        _live.Toggle();
    }

    private void ShowHistory(string? eventId = null)
    {
        if (_exiting || !EnsureLicensed()) return;
        if (_history is null)
        {
            _history = new HistoryWindow(() => _settings);
            _history.Closed += (_, _) => _history = null;
            _history.Show();
        }
        else
        {
            if (_history.WindowState == WindowState.Minimized) _history.WindowState = WindowState.Normal;
            _history.Activate();
        }
        if (eventId is not null) _history.FocusEvent(eventId);
    }

    private async void ShowSettings()
    {
        if (_exiting) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        // Les raccourcis globaux sont suspendus pendant la saisie d'un nouveau raccourci.
        _hotkeys?.UnregisterAll();
        var working = SettingsStore.Clone(_settings);
        _settingsWindow = new SettingsWindow(working, type => _hub.SendTestAsync(type), () => ShowLicenseDialog(blocking: false), _license.Evaluate());
        bool? result;
        try
        {
            result = _settingsWindow.ShowDialog();
        }
        finally
        {
            _settingsWindow = null;
        }

        if (result == true)
        {
            // On conserve la position du widget, qui a pu bouger pendant l'édition.
            working.LiveWindow.Left = _settings.LiveWindow.Left;
            working.LiveWindow.Top = _settings.LiveWindow.Top;
            working.LiveWindow.Width = _settings.LiveWindow.Width;
            working.LiveWindow.Height = _settings.LiveWindow.Height;
            _settings = working;
            SaveSettings();
            StartupRegistration.Apply(_settings.StartWithWindows);
            RegisterHotkeys(showErrors: true);
            _live?.OnSettingsChanged();
            _history?.ReloadSettings();
            _tray?.SetHistoryEnabled(_settings.Frigate.IsConfigured);
            await _hub.ApplySettingsAsync(_license.Evaluate().AllowsUse ? _settings : DisabledSettings());
        }
        else
        {
            if (!_settings.FirstRunCompleted)
            {
                _settings.FirstRunCompleted = true;
                SaveSettings();
            }
            RegisterHotkeys(showErrors: false);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Licence
    // ------------------------------------------------------------------------------------------

    /// <summary>Vérifie la licence avant une fonction ; propose l'activation si nécessaire.</summary>
    private bool EnsureLicensed()
    {
        if (_license.Evaluate().AllowsUse) return true;
        return ShowLicenseDialog(blocking: false) && _license.Evaluate().AllowsUse;
    }

    /// <returns>true si une licence valide a été activée.</returns>
    private bool ShowLicenseDialog(bool blocking)
    {
        if (_licenseWindow is not null)
        {
            _licenseWindow.Activate();
            return false;
        }
        _licenseWindow = new LicenseWindow(_license, blocking);
        try
        {
            var ok = _licenseWindow.ShowDialog() == true;
            if (ok) Log.Info("Licence activée : " + _license.Evaluate().Message);
            return ok || (!blocking && _license.Evaluate().AllowsUse);
        }
        finally
        {
            _licenseWindow = null;
        }
    }

    /// <summary>Revérifie régulièrement (l'application reste ouverte des semaines) : rappel puis arrêt à l'expiration.</summary>
    private void StartLicenseTimer()
    {
        var lastState = _license.Evaluate().State;
        _licenseTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _licenseTimer.Tick += async (_, _) =>
        {
            var status = _license.Evaluate();
            if (status.State == lastState) return;
            lastState = status.State;
            if (status.ShouldRemind) _tray?.ShowBalloon(AppInfo.ProductName, status.Message);
            if (!status.AllowsUse)
            {
                _live?.HideWidget();
                _history?.Close();
                await _hub.ApplySettingsAsync(DisabledSettings());
                _tray?.ShowBalloon(AppInfo.ProductName, status.Message);
            }
            else
            {
                await _hub.ApplySettingsAsync(_settings);
            }
        };
        _licenseTimer.Start();
    }

    /// <summary>Copie des paramètres sans aucune surveillance (licence expirée).</summary>
    private AppSettings DisabledSettings()
    {
        var copy = SettingsStore.Clone(_settings);
        copy.Reolink.MonitorEvents = false;
        copy.Notifications.FromFrigate = false;
        copy.Notifications.FromHomeAssistant = false;
        copy.MultiView.JumpOnDetection = false;
        return copy;
    }

    private void HandleToastActivation(ToastArguments args)
    {
        if (!args.TryGetValue(NotificationService.ArgAction, out var action)) action = NotificationService.ActionLive;
        switch (action)
        {
            case NotificationService.ActionClip when args.TryGetValue(NotificationService.ArgEvent, out var id):
                ShowHistory(id);
                break;
            case NotificationService.ActionHistory:
                ShowHistory();
                break;
            default:
                ShowLive(activate: true);
                break;
        }
    }

    // ------------------------------------------------------------------------------------------
    // Divers
    // ------------------------------------------------------------------------------------------

    private void RegisterHotkeys(bool showErrors)
    {
        if (_hotkeys is null) return;
        _hotkeys.UnregisterAll();
        var failures = new List<string>();
        var live = Hotkey.Parse(_settings.Hotkeys.ToggleLive);
        var history = Hotkey.Parse(_settings.Hotkeys.OpenHistory);
        if (!_hotkeys.Register(live, ToggleLive)) failures.Add(live.ToString());
        if (!_hotkeys.Register(history, () => ShowHistory())) failures.Add(history.ToString());
        _tray?.SetShortcutHints(live.ToString(), history.ToString());
        if (failures.Count > 0)
        {
            Log.Error("Raccourci(s) déjà utilisé(s) : " + string.Join(", ", failures));
            if (showErrors)
                _tray?.ShowBalloon("Raccourci clavier indisponible",
                    $"{string.Join(", ", failures)} est déjà utilisé par une autre application. Choisissez-en un autre dans les paramètres.");
        }
    }

    private void SaveSettings()
    {
        try { _store.Save(_settings); }
        catch (Exception ex) { Log.Error("Enregistrement des paramètres", ex); }
    }

    private void UpdateTooltip()
    {
        if (_tray is null) return;
        if (_hub.Paused)
        {
            _tray.SetTooltip($"{AppInfo.ProductName} — notifications suspendues");
            return;
        }
        var statuses = _hub.Statuses.Values.ToList();
        if (statuses.Count == 0)
        {
            _tray.SetTooltip(_settings.IsReolinkConfigured ? AppInfo.ProductName : $"{AppInfo.ProductName} — à configurer");
            return;
        }
        var errors = statuses.Where(s => s.State == MonitorState.Error).ToList();
        _tray.SetTooltip(errors.Count == 0
            ? $"{AppInfo.ProductName} — connecté"
            : $"{AppInfo.ProductName} — {errors[0].Source} : {errors[0].Message}");
    }

    private bool AcquireSingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
        if (created) return true;
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
        }
        catch
        {
            // L'autre instance est en cours de démarrage ou de fermeture.
        }
    }

    private void StartListeningForSecondInstance()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (!_exiting)
            {
                try
                {
                    if (_showEvent.WaitOne(1000) && !_exiting)
                        Dispatcher.BeginInvoke(() => ShowLive(activate: true));
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        })
        { IsBackground = true, Name = "SingleInstanceListener" };
        thread.Start();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Info("Fermeture");
        _licenseTimer?.Stop();
        _hotkeys?.Dispose();
        _history?.Close();
        _live?.CloseForShutdown();
        _tray?.Dispose();
        _tray = null;
        try
        {
            if (_hub is not null)
            {
                var dispose = _hub.DisposeAsync().AsTask();
                await Task.WhenAny(dispose, Task.Delay(3000));
            }
        }
        catch { }
        VlcService.Shutdown();
        _showEvent?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _ = ExitAsync();
        base.OnSessionEnding(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Exception de l'interface", e.Exception);
        e.Handled = true; // une application de zone de notification ne doit pas disparaître silencieusement
    }
}
