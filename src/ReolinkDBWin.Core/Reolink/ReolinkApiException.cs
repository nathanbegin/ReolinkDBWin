namespace ReolinkDBWin.Core.Reolink;

public sealed class ReolinkApiException : Exception
{
    public string Command { get; }
    public int RspCode { get; }

    public ReolinkApiException(string command, int rspCode, string? detail)
        : base(BuildMessage(command, rspCode, detail))
    {
        Command = command;
        RspCode = rspCode;
    }

    public bool IsAuthenticationError => RspCode is -6 or -7 or -10 or -11 or -12;
    public bool IsLoginRequired => RspCode == -6;
    public bool IsNotSupported => RspCode is -9 or -13 or -4;

    private static string BuildMessage(string command, int rspCode, string? detail)
    {
        var hint = rspCode switch
        {
            -6 => "session expirée",
            -7 => "nom d'utilisateur ou mot de passe invalide",
            -9 => "commande non supportée",
            -502 or -503 => "trop de tentatives, réessayez plus tard",
            _ => null,
        };
        var text = string.IsNullOrWhiteSpace(detail) ? hint ?? "erreur inconnue" : detail;
        return $"Reolink {command} : {text} (code {rspCode})";
    }
}
