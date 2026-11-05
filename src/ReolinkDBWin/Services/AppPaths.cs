using System.IO;

namespace ReolinkDBWin.Services;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.TechnicalName);

    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.TechnicalName, "cache");

    public static string SnapshotsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppInfo.ProductName);

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");
    public static string LogFile => Path.Combine(DataDirectory, "app.log");

    /// <summary>
    /// Reprend les paramètres de l'ancien nom de l'application (%AppData%\ReolinkDBWin) au premier lancement.
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReolinkDBWin");
            if (!Directory.Exists(legacy) || File.Exists(SettingsFile)) return;
            Directory.CreateDirectory(DataDirectory);
            foreach (var file in Directory.EnumerateFiles(legacy))
                File.Copy(file, Path.Combine(DataDirectory, Path.GetFileName(file)), overwrite: false);
        }
        catch
        {
            // Migration facultative.
        }
    }

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
