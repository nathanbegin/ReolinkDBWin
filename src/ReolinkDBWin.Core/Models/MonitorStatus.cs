namespace ReolinkDBWin.Core.Models;

public enum MonitorState
{
    Stopped,
    Connecting,
    Connected,
    Error,
}

public sealed record MonitorStatus(EventSource Source, MonitorState State, string Message);
