using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.Services;

public sealed class LogService : IDisposable
{
    private readonly object _sync = new();
    private const int MaxLines = 5000;

    private readonly StreamWriter? _fileWriter;
    private readonly string? _logFilePath;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ObservableCollection<string> Lines { get; } = [];

    public event EventHandler<LogLine>? LineAdded;

    public string? LogFilePath => _logFilePath;

    public LogService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Allow WPF to read the collection from any thread by acquiring _sync.
        BindingOperations.EnableCollectionSynchronization(Lines, _sync);

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

        // File write can happen on any thread.
        lock (_sync)
        {
            try
            {
                _fileWriter?.WriteLine(formatted);
            }
            catch
            {
                // Disk full / locked — don't crash the app.
            }
        }

        // Collection mutation must be marshalled to the UI thread to avoid
        // NotSupportedException ("cross-thread CollectionChanged").
        if (_dispatcher.CheckAccess())
        {
            AddLine(formatted);
        }
        else
        {
            _dispatcher.BeginInvoke(new Action<string>(AddLine), formatted);
        }

        LineAdded?.Invoke(this, line);
    }

    private void AddLine(string formatted)
    {
        lock (_sync)
        {
            Lines.Add(formatted);
            while (Lines.Count > MaxLines)
            {
                Lines.RemoveAt(0);
            }
        }
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
        if (_dispatcher.CheckAccess())
        {
            ClearCore();
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(ClearCore));
        }
    }

    private void ClearCore()
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
