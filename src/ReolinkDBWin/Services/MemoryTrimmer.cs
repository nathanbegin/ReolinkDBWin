using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ReolinkDBWin.Services;

/// <summary>
/// Rend la mémoire inutilisée à Windows après la fermeture des flux vidéo (widget masqué, alerte fermée…) :
/// collecte complète avec compactage, puis réduction de l'ensemble de travail du processus.
/// </summary>
public static class MemoryTrimmer
{
    private static DispatcherTimer? _pending;

    /// <summary>Planifie un nettoyage dans quelques secondes (le temps que les lecteurs se ferment).</summary>
    public static void TrimSoon(TimeSpan? delay = null)
    {
        _pending?.Stop();
        _pending = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = delay ?? TimeSpan.FromSeconds(4) };
        _pending.Tick += (_, _) =>
        {
            _pending?.Stop();
            _pending = null;
            Trim();
        };
        _pending.Start();
    }

    public static void Trim()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            var before = p.WorkingSet64;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(p.Handle, -1, -1);
            p.Refresh();
            Log.Info($"Mémoire libérée : {before / 1048576} Mo → {p.WorkingSet64 / 1048576} Mo (flux ouverts : {CameraStream.AliveCount})");
        }
        catch (Exception ex)
        {
            Log.Error("Nettoyage de la mémoire", ex);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
