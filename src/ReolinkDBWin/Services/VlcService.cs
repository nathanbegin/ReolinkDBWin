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
        _libVlc = new LibVLC(enableDebugLogs: false, "--no-video-title-show", "--no-snapshot-preview", "--quiet");
    }

    public static void Shutdown()
    {
        _libVlc?.Dispose();
        _libVlc = null;
    }
}
