namespace ClientPlugin;

internal enum AutoDockPilotStatus
{
    Running,
    Cancelled,
    Completed
}

internal readonly struct AutoDockPilotUpdateResult
{
    public static readonly AutoDockPilotUpdateResult Running = new AutoDockPilotUpdateResult(AutoDockPilotStatus.Running, null, null);

    public readonly AutoDockPilotStatus Status;
    public readonly string Message;
    public readonly string Font;

    public AutoDockPilotUpdateResult(AutoDockPilotStatus status, string message, string font)
    {
        Status = status;
        Message = message;
        Font = font;
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}
