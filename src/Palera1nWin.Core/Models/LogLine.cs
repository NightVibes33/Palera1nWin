namespace Palera1nWin.Core.Models;

public sealed class LogLine
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string Source { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public bool IsError { get; init; }

    public override string ToString()
    {
        var prefix = IsError ? "ERR" : "INF";
        return $"[{Timestamp:HH:mm:ss.fff}] [{prefix}] [{Source}] {Message}";
    }
}
