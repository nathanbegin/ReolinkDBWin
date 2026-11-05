using System.IO;

namespace ReolinkDBWin.Services;

/// <summary>Journal texte simple (%AppData%\ReolinkDBWin\app.log), utile pour le dépannage.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERREUR", ex is null ? message : $"{message} : {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                var path = AppPaths.LogFile;
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Le journal ne doit jamais faire planter l'application.
        }
    }
}
