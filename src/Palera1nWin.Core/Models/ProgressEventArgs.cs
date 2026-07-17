namespace Palera1nWin.Core.Models;

public sealed class ProgressEventArgs : EventArgs
{
    public ProgressEventArgs(string stage, string message, int? percent = null, bool isError = false)
    {
        Stage = stage;
        Message = message;
        Percent = percent;
        IsError = isError;
    }

    public string Stage { get; }

    public string Message { get; }

    public int? Percent { get; }

    public bool IsError { get; }
}
