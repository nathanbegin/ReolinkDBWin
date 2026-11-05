using System.Text;
using Microsoft.Win32;
using ReolinkDBWin.Core.Licensing;

namespace ReolinkDBWin.Services;

/// <summary>Clé de licence et début d'essai dans HKCU\Software\ReolinkDBWin\License.</summary>
public sealed class RegistryLicenseStore : ILicenseStore
{
    private const string KeyPath = @"Software\ReolinkDBWin\License";

    public string? LoadKey() => Read("Key");

    public void SaveKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) Delete("Key");
        else Write("Key", key);
    }

    public DateOnly? LoadTrialStart()
    {
        // Légèrement masqué : simple dissuasion, la vraie protection est la signature des clés.
        var raw = Read("T");
        if (raw is null) return null;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            return DateOnly.TryParseExact(new string(text.Reverse().ToArray()), "yyyyMMdd", out var d) ? d : null;
        }
        catch
        {
            return null;
        }
    }

    public void SaveTrialStart(DateOnly date) =>
        Write("T", Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(date.ToString("yyyyMMdd").Reverse().ToArray()))));

    private static string? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void Write(string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(name, value);
        }
        catch (Exception ex)
        {
            Log.Error("Enregistrement de la licence", ex);
        }
    }

    private static void Delete(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch { }
    }
}
