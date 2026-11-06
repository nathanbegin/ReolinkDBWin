using LibVLCSharp.Shared;

namespace ReolinkDBWin.Services;

/// <summary>Instance LibVLC partagée (lecture RTSP en direct et clips Frigate).</summary>
public static class VlcService
{
    private static LibVLC? _libVlc;

    public static LibVLC Instance => _libVlc ?? throw new InvalidOperationException("LibVLC non initialisé");

    public static void Initialize()
    {
        if (_libVlc is not null) return;
        LibVLCSharp.Shared.Core.Initialize();
        // Décodage : chaque thread de décodage garde ses propres images en mémoire (plusieurs Mo chacune
        // en 5 MP). Par défaut, FFmpeg en lance presque autant que de cœurs : on plafonne.
        var threads = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
        _libVlc = new LibVLC(enableDebugLogs: false, "--no-video-title-show", "--no-snapshot-preview",
            $"--avcodec-threads={threads}", "--no-sub-autodetect-file", "--no-stats");
        _libVlc.Log += OnVlcLog;
        Log.Info($"LibVLC {_libVlc.Version} initialisé");
    }

    private static readonly object LogGate = new();
    private static string _lastVlcMessage = "";
    private static DateTime _lastVlcAt;
    private static int _repeats;

    /// <summary>Recopie les erreurs de VLC dans app.log (sans répéter un même message en rafale).</summary>
    private static void OnVlcLog(object? sender, LogEventArgs e)
    {
        try
        {
            if (e.Level != LogLevel.Error) return;
            var text = $"VLC [{e.Module}] {e.Message}";
            lock (LogGate)
            {
                var now = DateTime.UtcNow;
                if (text == _lastVlcMessage && now - _lastVlcAt < TimeSpan.FromSeconds(30))
                {
                    _repeats++;
                    return;
                }
                if (_repeats > 0) Log.Info($"VLC : message précédent répété {_repeats} fois");
                _repeats = 0;
                _lastVlcMessage = text;
                _lastVlcAt = now;
            }
            Log.Info(text);
        }
        catch
        {
            // Jamais d'exception vers le code natif.
        }
    }

    public static void Shutdown()
    {
        _libVlc?.Dispose();
        _libVlc = null;
    }
}
