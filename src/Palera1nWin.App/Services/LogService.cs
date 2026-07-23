using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Threading;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.Services;

public sealed class LogService : IDisposable
{
    private const int MaxLines = 5000;
    private const long MaxLogBytes = 10L * 1024L * 1024L;
    private const int MaxLogFiles = 12;
    private static readonly Regex EcidRegex = new(
        @"(?i)(?<label>\b(?:ECID|UniqueChipID)\s*[:=]\s*)(?:0x)?[0-9a-f]{6,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UserPathRegex = new(
        @"(?i)(?<root>[A-Z]:\\Users\\)(?<user>[^\\\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TokenRegex = new(
        @"(?i)(?<label>\b(?:token|authorization|ghp_[a-z0-9]*)\s*[:=]\s*)[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private StreamWriter? _fileWriter;
    private string? _logFilePath;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    public ObservableCollection<string> Lines { get; } = [];
    public event EventHandler<LogLine>? LineAdded;
    public string? LogFilePath => _logFilePath;

    public LogService()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        BindingOperations.EnableCollectionSynchronization(Lines, _sync);
        try
        {
            Directory.CreateDirectory(AppSettings.LogsDirectory);
            PruneOldLogs();
            OpenNewLogFile();
        }
        catch
        {
            // File logging is best-effort; in-memory logs still work.
        }
    }

    public void Append(LogLine line)
    {
        if (_disposed) return;
        var sanitizedMessage = Sanitize(line.Message);
        var sanitized = new LogLine
        {
            Source = Sanitize(line.Source),
            Message = sanitizedMessage,
            IsError = line.IsError,
        };
        var formatted = sanitized.ToString();

        lock (_sync)
        {
            try
            {
                RotateIfNeeded(formatted.Length + Environment.NewLine.Length);
                _fileWriter?.WriteLine(formatted);
            }
            catch
            {
                // Disk-full, access, or rotation failures must not interrupt hardware work.
            }
        }

        if (_dispatcher.CheckAccess()) AddLine(formatted);
        else _dispatcher.BeginInvoke(new Action<string>(AddLine), formatted);

        var handlers = LineAdded;
        if (handlers is null) return;
        foreach (EventHandler<LogLine> handler in handlers.GetInvocationList())
        {
            try { handler(this, sanitized); } catch { }
        }
    }

    private void AddLine(string formatted)
    {
        if (_disposed) return;
        lock (_sync)
        {
            Lines.Add(formatted);
            while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        }
    }

    public void Append(string source, string message, bool isError = false) =>
        Append(new LogLine { Source = source, Message = message, IsError = isError });

    public void Clear()
    {
        if (_dispatcher.CheckAccess()) ClearCore();
        else _dispatcher.BeginInvoke(new Action(ClearCore));
    }

    private void ClearCore()
    {
        lock (_sync) Lines.Clear();
    }

    public IReadOnlyList<string> GetRecent(int count)
    {
        lock (_sync)
        {
            var safeCount = Math.Max(0, count);
            return Lines.Count <= safeCount
                ? Lines.ToList()
                : Lines.Skip(Lines.Count - safeCount).ToList();
        }
    }

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var text = EcidRegex.Replace(value, match => match.Groups["label"].Value + "[REDACTED]");
        text = UserPathRegex.Replace(text, match => match.Groups["root"].Value + "[USER]");
        text = TokenRegex.Replace(text, match => match.Groups["label"].Value + "[REDACTED]");
        return text;
    }

    private void RotateIfNeeded(int incomingCharacters)
    {
        if (_fileWriter is null || _logFilePath is null) return;
        var size = 0L;
        try { size = File.Exists(_logFilePath) ? new FileInfo(_logFilePath).Length : 0; }
        catch { }
        if (size + incomingCharacters * sizeof(char) < MaxLogBytes) return;

        try { _fileWriter.Flush(); } catch { }
        try { _fileWriter.Dispose(); } catch { }
        _fileWriter = null;
        OpenNewLogFile();
        PruneOldLogs();
    }

    private void OpenNewLogFile()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        _logFilePath = Path.Combine(AppSettings.LogsDirectory, $"session-{stamp}.log");
        _fileWriter = new StreamWriter(_logFilePath, append: false) { AutoFlush = true };
    }

    private static void PruneOldLogs()
    {
        try
        {
            var files = Directory.EnumerateFiles(AppSettings.LogsDirectory, "session-*.log")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(MaxLogFiles)
                .ToArray();
            foreach (var file in files)
            {
                try { file.Delete(); } catch { }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_sync)
        {
            try { _fileWriter?.Flush(); } catch { }
            try { _fileWriter?.Dispose(); } catch { }
            _fileWriter = null;
        }
    }
}
