using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ReolinkDBWin.Services;

/// <summary>
/// Traces de diagnostic pour comprendre une fermeture inattendue :
/// <list type="bullet">
/// <item>un fichier témoin est créé au démarrage et supprimé à la fermeture normale : s'il est encore là au
/// lancement suivant, la session précédente s'est terminée brutalement (crash natif, arrêt forcé…) ;</item>
/// <item>dans ce cas, les erreurs que Windows a enregistrées pour l'application (journal « Application » :
/// Application Error, .NET Runtime, Windows Error Reporting) sont recopiées dans app.log ;</item>
/// <item>un battement de vie régulier note la mémoire et le nombre de flux ouverts (fuite, saturation).</item>
/// </list>
/// </summary>
public static class Diagnostics
{
    private static readonly string MarkerFile = Path.Combine(AppPaths.DataDirectory, "session.lock");
    private static DispatcherTimer? _heartbeat;
    private static bool _cleanExit;

    public static void Start()
    {
        Log.Info($"===== Démarrage {AppInfo.ProductName} {AppInfo.Version} — PID {Environment.ProcessId}, " +
                 $"{RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} cœurs =====");

        CheckPreviousSession();
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(MarkerFile, $"{Environment.ProcessId}|{DateTime.Now:O}|{AppInfo.Version}");
        }
        catch (Exception ex)
        {
            Log.Error("Fichier témoin de session", ex);
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!_cleanExit) Log.Error("Le processus se termine sans passer par « Quitter » (ProcessExit).");
            Log.Info("===== Fin du processus =====");
        };

        _heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(10) };
        _heartbeat.Tick += (_, _) => LogHeartbeat();
        _heartbeat.Start();
    }

    /// <summary>Fermeture normale : supprime le témoin et note la raison.</summary>
    public static void MarkCleanExit(string reason)
    {
        _cleanExit = true;
        _heartbeat?.Stop();
        Log.Info($"Fermeture demandée : {reason}");
        LogHeartbeat();
        try { File.Delete(MarkerFile); } catch { }
    }

    /// <summary>Mémoire réservée (privée) du processus, en Mo.</summary>
    public static long PrivateMemoryMb()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.PrivateMemorySize64 / 1048576;
        }
        catch
        {
            return 0;
        }
    }

    public static void LogHeartbeat()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            p.Refresh();
            Log.Info($"Activité — mémoire {p.WorkingSet64 / 1048576} Mo (privée {p.PrivateMemorySize64 / 1048576} Mo, gérée {GC.GetTotalMemory(false) / 1048576} Mo), " +
                     $"threads {p.Threads.Count}, handles {p.HandleCount}, flux vidéo ouverts {CameraStream.AliveCount} (ouvertures depuis le démarrage : {CameraStream.OpenedCount}), " +
                     $"en marche depuis {(DateTime.Now - p.StartTime):d\\.hh\\:mm}");
        }
        catch (Exception ex)
        {
            Log.Error("Battement de vie", ex);
        }
    }

    private static void CheckPreviousSession()
    {
        try
        {
            if (!File.Exists(MarkerFile)) return;
            var parts = File.ReadAllText(MarkerFile).Split('|');
            var since = parts.Length > 1 && DateTime.TryParse(parts[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d : File.GetLastWriteTime(MarkerFile);
            Log.Error($"⚠ La session précédente (PID {parts.ElementAtOrDefault(0)}, démarrée le {since:yyyy-MM-dd HH:mm:ss}, " +
                      $"version {parts.ElementAtOrDefault(2)}) s'est terminée de façon inattendue. Erreurs enregistrées par Windows :");
            var found = ReportWindowsEvents(since);
            if (found == 0)
                Log.Info("  (aucune erreur Windows trouvée : arrêt forcé, fin de session, mise à jour, ou processus tué depuis le Gestionnaire des tâches)");
        }
        catch (Exception ex)
        {
            Log.Error("Analyse de la session précédente", ex);
        }
    }

    /// <summary>Recopie dans le journal les erreurs Windows récentes qui concernent l'application.</summary>
    private static int ReportWindowsEvents(DateTime since)
    {
        var exe = Path.GetFileName(Environment.ProcessPath ?? "VigieWidget.exe");
        var ms = Math.Max(60_000, (long)(DateTime.Now - since).TotalMilliseconds + 60_000);
        var query = "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] " +
                    $"or Provider[@Name='Windows Error Reporting'] or Provider[@Name='Application Hang']) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
        var count = 0;
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query) { ReverseDirection = true });
            for (var record = reader.ReadEvent(); record is not null && count < 5; record = reader.ReadEvent())
            {
                using (record)
                {
                    string? text;
                    try { text = record.FormatDescription(); }
                    catch { text = null; }
                    text ??= string.Join(" | ", record.Properties.Select(p => p.Value?.ToString()));
                    if (!text.Contains(exe, StringComparison.OrdinalIgnoreCase) &&
                        !text.Contains("VigieWidget", StringComparison.OrdinalIgnoreCase)) continue;
                    count++;
                    var trimmed = text.Length > 4000 ? text[..4000] + " […]" : text;
                    Log.Error($"  [{record.TimeCreated:yyyy-MM-dd HH:mm:ss}] {record.ProviderName} (ID {record.Id}) :{Environment.NewLine}{Indent(trimmed)}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Lecture du journal d'événements Windows", ex);
        }
        return count;
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => "      " + l.TrimEnd('\r')));
}
