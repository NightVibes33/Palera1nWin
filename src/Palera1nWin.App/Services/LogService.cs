using System.Collections.ObjectModel;
using System.IO;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.Services;

public sealed class LogService : IDisposable
{
    private readonly object _sync = new();
    private const int MaxLines = 5000;

    private readonly StreamWriter? _fileWriter;
    private readonly string? _logFilePath;
    private bool _disposed;

    public ObservableCollection<string> Lines { get; } = [];

    public event EventHandler<LogLine>? LineAdded;

    public string? LogFilePath => _logFilePath;

    public LogService()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.LogsDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _logFilePath = Path.Combine(AppSettings.LogsDirectory, $"session-{stamp}.log");
            _fileWriter = new StreamWriter(_logFilePath, append: true) { AutoFlush = true };
        }
        catch
        {
            // File logging is best-effort; in-memory logs still work.
        }
    }

    public void Append(LogLine line)
    {
        var formatted = line.ToString();
        lock (_sync)
        {
            Lines.Add(formatted);
            while (Lines.Count > MaxLines)
            {
                Lines.RemoveAt(0);
            }

            try
            {
                _fileWriter?.WriteLine(formatted);
            }
            catch
            {
                // Disk full / locked — don't crash the app.
            }
        }

        LineAdded?.Invoke(this, line);
    }

    public void Append(string source, string message, bool isError = false)
    {
        Append(new LogLine
        {
            Source = source,
            Message = message,
            IsError = isError,
        });
    }

    public void Clear()
    {
        lock (_sync)
        {
            Lines.Clear();
        }
    }

    public IReadOnlyList<string> GetRecent(int count)
    {
        lock (_sync)
        {
            if (Lines.Count <= count)
            {
                return Lines.ToList();
            }

            return Lines.Skip(Lines.Count - count).ToList();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_sync)
        {
            try
            {
                _fileWriter?.Flush();
                _fileWriter?.Dispose();
            }
            catch
            {
                // Ignore.
            }
        }
    }
}
