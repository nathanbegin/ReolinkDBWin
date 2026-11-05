using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReolinkDBWin.Core.Settings;

/// <summary>Chiffre les secrets (mots de passe, jetons) avant écriture sur disque. Sous Windows : DPAPI.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}

/// <summary>Protecteur sans chiffrement (tests, ou plateforme sans DPAPI).</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public string Protect(string plain) => plain;
    public string Unprotect(string protectedValue) => protectedValue;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly ISecretProtector _protector;

    public SettingsStore(string path, ISecretProtector protector)
    {
        _path = path;
        _protector = protector;
    }

    public string FilePath => _path;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReolinkDBWin");

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
            settings.Reolink.Password = Unprotect(settings.Reolink.PasswordProtected);
            settings.Frigate.Password = Unprotect(settings.Frigate.PasswordProtected);
            settings.HomeAssistant.AccessToken = Unprotect(settings.HomeAssistant.AccessTokenProtected);
            return settings;
        }
        catch (Exception)
        {
            // Fichier corrompu : on le garde de côté et on repart des valeurs par défaut.
            try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { /* ignoré */ }
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        settings.Reolink.PasswordProtected = Protect(settings.Reolink.Password);
        settings.Frigate.PasswordProtected = Protect(settings.Frigate.Password);
        settings.HomeAssistant.AccessTokenProtected = Protect(settings.HomeAssistant.AccessToken);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Copie profonde (pour éditer dans la fenêtre de paramètres sans toucher l'original).</summary>
    public static AppSettings Clone(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var copy = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)!;
        copy.Reolink.Password = settings.Reolink.Password;
        copy.Frigate.Password = settings.Frigate.Password;
        copy.HomeAssistant.AccessToken = settings.HomeAssistant.AccessToken;
        return copy;
    }

    private string? Protect(string? plain) => string.IsNullOrEmpty(plain) ? null : _protector.Protect(plain);

    private string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try { return _protector.Unprotect(value); }
        catch { return ""; }
    }
}
