using Microsoft.Toolkit.Uwp.Notifications;
using ReolinkDBWin.Core.Models;

namespace ReolinkDBWin.Services;

/// <summary>Notifications Windows (toast) avec image, et boutons « Voir en direct » / « Voir le clip ».</summary>
public sealed class NotificationService
{
    public const string ArgAction = "action";
    public const string ActionLive = "live";
    public const string ActionClip = "clip";
    public const string ActionHistory = "history";
    public const string ArgEvent = "event";

    private readonly Action<string, string>? _fallback;

    /// <param name="fallback">Affichage de secours (bulle de la zone de notification) si les toasts échouent.</param>
    public NotificationService(Action<string, string>? fallback)
    {
        _fallback = fallback;
    }

    /// <summary>Déclenché (sur un thread quelconque) quand l'utilisateur clique une notification.</summary>
    public event Action<ToastArguments>? Activated;

    public void Initialize()
    {
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            try { Activated?.Invoke(ToastArguments.Parse(e.Argument)); }
            catch (Exception ex) { Log.Error("Activation de notification", ex); }
        };
    }

    public void Show(DetectionEvent e, string? imagePath, bool sound, bool frigateAvailable)
    {
        var title = DetectionTypeInfo.NotificationTitle(e.Type);
        var details = new List<string>();
        if (e.Type == DetectionType.Other && !string.IsNullOrEmpty(e.RawLabel)) details.Add(e.RawLabel);
        if (e.Score is { } score) details.Add($"{score:P0}");
        if (!string.IsNullOrEmpty(e.Camera)) details.Add(e.Camera);
        var body = $"{e.Timestamp.LocalDateTime:HH:mm:ss}" + (details.Count > 0 ? " · " + string.Join(" · ", details) : "");
        var source = e.Source switch
        {
            EventSource.Frigate => "Frigate",
            EventSource.HomeAssistant => "Home Assistant",
            EventSource.Test => "Test",
            _ => "Sonnette",
        };

        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ArgAction, ActionLive)
                .AddText(title)
                .AddText(body)
                .AddAttributionText(source)
                .AddButton(new ToastButton().SetContent("Voir en direct").AddArgument(ArgAction, ActionLive));

            if (imagePath is not null) builder.AddHeroImage(new Uri(imagePath));

            if (e.FrigateEventId is not null)
                builder.AddButton(new ToastButton().SetContent("Voir le clip").AddArgument(ArgAction, ActionClip).AddArgument(ArgEvent, e.FrigateEventId));
            else if (frigateAvailable)
                builder.AddButton(new ToastButton().SetContent("Historique").AddArgument(ArgAction, ActionHistory));

            if (e.Type == DetectionType.Doorbell)
            {
                // Reste affichée jusqu'à ce qu'on la ferme, avec un son plus insistant.
                builder.SetToastScenario(ToastScenario.Reminder);
                builder.AddButton(new ToastButtonDismiss("Ignorer"));
                builder.AddAudio(new ToastAudio { Src = new Uri("ms-winsoundevent:Notification.Reminder"), Silent = !sound });
            }
            else
            {
                builder.AddAudio(new ToastAudio { Silent = !sound });
            }

            builder.Show(toast =>
            {
                toast.Group = "reolink";
                toast.Tag = e.Type.ToString();
                toast.ExpirationTime = DateTimeOffset.Now.AddHours(e.Type == DetectionType.Doorbell ? 1 : 12);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Affichage de la notification Windows", ex);
            _fallback?.Invoke(title, body);
        }
    }

    public static void ClearAll()
    {
        try { ToastNotificationManagerCompat.History.Clear(); } catch { }
    }
}
