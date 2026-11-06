using System.Windows.Threading;
using ReolinkDBWin.Core.Settings;
using ReolinkDBWin.Core.Updates;

namespace ReolinkDBWin.Services;

/// <summary>Vérification des mises à jour : au démarrage (après 1 min), puis toutes les 24 h, ou à la demande.</summary>
public sealed class UpdateService
{
    private readonly Func<AppSettings> _settings;
    private readonly Action _save;
    private readonly Action<UpdateInfo> _announce;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _latest;

    public UpdateService(Func<AppSettings> settings, Action save, Action<UpdateInfo> announce)
    {
        _settings = settings;
        _save = save;
        _announce = announce;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(24);
            if (_settings().Updates.AutoCheck) await CheckAsync(manual: false);
        };
    }

    public UpdateInfo? Latest => _latest;

    public static UpdateChecker CreateChecker() =>
        new(AppInfo.UpdateFeedUrl, $"{AppInfo.TechnicalName}/{AppInfo.Version}");

    public void Start() => _timer.Start();

    /// <returns>La mise à jour trouvée (null si à jour), ou lève une exception en mode manuel.</returns>
    public async Task<UpdateInfo?> CheckAsync(bool manual)
    {
        if (string.IsNullOrWhiteSpace(AppInfo.UpdateFeedUrl)) return null;
        try
        {
            using var checker = CreateChecker();
            var latest = await checker.GetLatestAsync();
            var s = _settings();
            s.Updates.LastCheck = DateTimeOffset.Now;
            _save();
            var current = UpdateChecker.ParseVersion(AppInfo.Version) ?? new Version(0, 0, 0);
            if (latest is null || !UpdateChecker.IsNewer(latest.Version, current))
            {
                Log.Info($"Mises à jour : à jour ({AppInfo.Version}{(latest is null ? ", aucune version publiée" : $", dernière {latest.Version.ToString(3)}")})");
                return null;
            }
            _latest = latest;
            Log.Info($"Mises à jour : version {latest.Version.ToString(3)} disponible");
            if (!manual && s.Updates.SkippedVersion == latest.Version.ToString(3)) return latest;
            _announce(latest);
            return latest;
        }
        catch (Exception ex)
        {
            Log.Error("Vérification des mises à jour", ex);
            if (manual) throw;
            return null;
        }
    }
}
