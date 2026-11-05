using Microsoft.Win32;

namespace ReolinkDBWin.Services;

/// <summary>Démarrage automatique avec Windows (clé Run de l'utilisateur courant).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ReolinkDBWin";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is not null) key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Politique de groupe restrictive : on ignore.
        }
    }
}
