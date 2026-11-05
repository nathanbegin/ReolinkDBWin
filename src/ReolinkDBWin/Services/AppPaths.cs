using System.IO;

namespace ReolinkDBWin.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReolinkDBWin");

    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReolinkDBWin", "cache");

    public static string SnapshotsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sonnette Reolink");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LogFile => Path.Combine(DataDirectory, "app.log");

    /// <summary>Supprime les fichiers du cache plus vieux que <paramref name="maxAge"/>.</summary>
    public static void CleanCache(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(CacheDirectory)) return;
            foreach (var f in Directory.EnumerateFiles(CacheDirectory))
            {
                try { if (DateTime.Now - File.GetLastWriteTime(f) > maxAge) File.Delete(f); } catch { }
            }
        }
        catch { }
    }
}
