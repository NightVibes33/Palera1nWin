using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed class SessionLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public event EventHandler<string>? LineWritten;
    public string LogPath { get; }

    public SessionLogger(string directory)
    {
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, $"session-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _writer = new StreamWriter(new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} [{level}] {message}";
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
        LineWritten?.Invoke(this, line);
    }

    public void Dispose() => _writer.Dispose();
}

public sealed class ProcessRunner
{
    private readonly SessionLogger _log;

    public ProcessRunner(SessionLogger log) => _log = log;

    public async Task<ToolResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var argumentList = arguments.ToArray();
        var displayArguments = string.Join(" ", argumentList.Select(Quote));
        _log.Info($"> {fileName} {displayArguments}");

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var entry in environment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            output.AppendLine(e.Data);
            _log.Info(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            error.AppendLine(e.Data);
            _log.Warn(e.Data);
        };

        var stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start {fileName}.");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts?.Token ?? CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            await Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Path.GetFileName(fileName)} exceeded {timeout}.");
            }
            throw;
        }
        finally
        {
            stopwatch.Stop();
        }

        return new ToolResult(fileName, displayArguments, process.ExitCode, output.ToString(), error.ToString(), stopwatch.Elapsed);
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }
}

public sealed class ToolchainLocator
{
    public string Root { get; }

    public ToolchainLocator(string? root = null)
    {
        Root = root ?? FindToolchainRoot();
    }

    public string OpenRa1n => Require("openra1n.exe", "native/openra1n.exe", "dist/native/openra1n.exe");
    public string TurdusRestore => Require("turdus_merula.exe", "native/turdus_merula.exe", "dist/native/turdus_merula.exe");
    public string IRecovery => Require("irecovery.exe", "native/irecovery.exe", "dist/native/irecovery.exe");
    public string WdiSimple => Require("wdi-simple.exe", "native/wdi-simple.exe", "dist/native/wdi-simple.exe");
    public string LibUsb => Require("libusb-1.0.dll", "native/libusb-1.0.dll", "dist/native/libusb-1.0.dll");
    public string ResourcesDirectory => Path.Combine(Root, "resources");

    public IReadOnlyList<string> MissingRequiredTools()
    {
        var names = new[] { "openra1n.exe", "turdus_merula.exe", "irecovery.exe", "wdi-simple.exe", "libusb-1.0.dll" };
        return names.Where(name => Find(name, $"native/{name}", $"dist/native/{name}") is null).ToArray();
    }

    private string Require(params string[] candidates) =>
        Find(candidates) ?? throw new FileNotFoundException($"Required tool not found under {Root}: {string.Join(", ", candidates)}");

    private string? Find(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(Root, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    private static string FindToolchainRoot()
    {
        var starts = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory
        };

        foreach (var start in starts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = new DirectoryInfo(start);
            for (var depth = 0; directory is not null && depth < 4; depth++, directory = directory.Parent)
            {
                var direct = Path.Combine(directory.FullName, "toolchain");
                if (Directory.Exists(direct)) return direct;
                var nested = Path.Combine(directory.FullName, "DarkSwordRestore", "toolchain");
                if (Directory.Exists(nested)) return nested;
            }
        }
        return Path.Combine(AppContext.BaseDirectory, "toolchain");
    }
}

public static class SessionStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static async Task SaveAsync(RestoreSession session, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(session.SessionDirectory);
        var path = Path.Combine(session.SessionDirectory, "session.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(session, Options), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<RestoreSession?> LoadAsync(string sessionDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(sessionDirectory, "session.json");
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<RestoreSession>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), Options);
    }
}
