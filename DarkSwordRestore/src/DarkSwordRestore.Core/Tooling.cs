using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DarkSwordRestore.Core;

public sealed record ToolchainPaths(
    string Root,
    string OpenRa1n,
    string IdeviceRestore,
    string PongoBridge,
    string WdiSimple)
{
    public static ToolchainPaths FromApplicationDirectory(string? applicationDirectory = null)
    {
        var baseDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        var root = Path.Combine(baseDirectory, "toolchain");
        return new ToolchainPaths(
            root,
            Path.Combine(root, "openra1n.exe"),
            Path.Combine(root, "turdus_merula.exe"),
            Path.Combine(root, "darksword-pongo.exe"),
            Path.Combine(root, "wdi-simple.exe"));
    }

    public IReadOnlyList<string> MissingFiles() =>
        new[]
        {
            OpenRa1n,
            IdeviceRestore,
            PongoBridge,
            WdiSimple,
            Path.Combine(Root, "ideviceinfo.exe"),
            Path.Combine(Root, "irecovery.exe")
        }
        .Where(path => !File.Exists(path))
        .ToArray();
}

public sealed class ToolProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private int _disposed;

    internal ToolProcessSession(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        FileName = fileName;
        Arguments = string.Join(' ', startInfo.ArgumentList.Select(QuoteForLog));
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (_stdout) _stdout.Add(args.Data);
            onOutput?.Invoke(args.Data);
        };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (_stderr) _stderr.Add(args.Data);
            onOutput?.Invoke(args.Data);
        };

        if (!_process.Start()) throw new InvalidOperationException($"Unable to start {fileName}.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _cancellationRegistration = cancellationToken.Register(Kill);
        Completion = CompleteAsync();
    }

    public string FileName { get; }
    public string Arguments { get; }
    public Task<ToolResult> Completion { get; }

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; }
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process may exit while cancellation/cleanup is running.
        }
    }

    private async Task<ToolResult> CompleteAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        _process.WaitForExit();
        _stopwatch.Stop();
        string stdout;
        string stderr;
        lock (_stdout) stdout = string.Join(Environment.NewLine, _stdout);
        lock (_stderr) stderr = string.Join(Environment.NewLine, _stderr);
        return new ToolResult(FileName, Arguments, _process.ExitCode, stdout, stderr, _stopwatch.Elapsed);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Kill();
        try { await Completion.ConfigureAwait(false); } catch { }
        _cancellationRegistration.Dispose();
        _process.Dispose();
    }

    private static string QuoteForLog(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}

public sealed class ToolProcessRunner
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorCancelled = 1223;

    public ToolProcessSession StartSession(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fileName)) throw new FileNotFoundException("Required tool was not found.", fileName);
        return new ToolProcessSession(fileName, arguments, workingDirectory, onOutput, cancellationToken);
    }

    public async Task<ToolResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        CancellationToken cancellationToken,
        bool requireZeroExitCode = true)
    {
        if (!File.Exists(fileName))
        {
            throw new FileNotFoundException("Required tool was not found.", fileName);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new List<string>();
        var stderr = new List<string>();
        var started = Stopwatch.StartNew();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (stdout) stdout.Add(args.Data);
            onOutput?.Invoke(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (stderr) stderr.Add(args.Data);
            onOutput?.Invoke(args.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start {fileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have exited between the state check and Kill.
            }
        });

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        started.Stop();
        var result = new ToolResult(
            fileName,
            string.Join(' ', startInfo.ArgumentList.Select(QuoteForLog)),
            process.ExitCode,
            string.Join(Environment.NewLine, stdout),
            string.Join(Environment.NewLine, stderr),
            started.Elapsed);

        if (requireZeroExitCode && !result.Success)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} exited with code {result.ExitCode}.{Environment.NewLine}{result.StandardError}");
        }

        return result;
    }

    public Task<ToolResult> RunElevatedAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Driver operations are Windows-only.");
        }
        if (!File.Exists(fileName))
        {
            throw new FileNotFoundException("The elevated tool was not found.", fileName);
        }

        var argumentText = string.Join(' ', arguments.Select(QuoteForCommandLine));
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = argumentText,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas"
        };

        return RunElevatedCoreAsync(startInfo, cancellationToken);
    }

    private static async Task<ToolResult> RunElevatedCoreAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("The elevated process did not start.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            throw new InvalidOperationException(
                $"Windows administrator approval was cancelled. DarkSword did not install the required Apple USB driver. " +
                $"Run Palera1nWin as administrator, retry, and choose Yes for {Path.GetFileName(startInfo.FileName)}.",
                exception);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorAccessDenied)
        {
            throw new InvalidOperationException(
                $"Windows blocked administrator access for {Path.GetFileName(startInfo.FileName)}. " +
                "Run Palera1nWin as administrator and allow the driver installer through Windows Security if it was blocked.",
                exception);
        }

        using (process)
        using (var registration = cancellationToken.Register(() =>
               {
                   try
                   {
                       if (!process.HasExited) process.Kill(entireProcessTree: true);
                   }
                   catch
                   {
                       // Best-effort cancellation of an elevated child process.
                   }
               }))
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            var result = new ToolResult(startInfo.FileName, startInfo.Arguments, process.ExitCode, string.Empty, string.Empty, stopwatch.Elapsed);
            if (!result.Success)
            {
                throw new InvalidOperationException(
                    $"{Path.GetFileName(startInfo.FileName)} could not install the required Apple USB driver and exited with code {result.ExitCode}. " +
                    "Reconnect the device in the requested mode, run Palera1nWin as administrator, and retry.");
            }
            return result;
        }
    }

    private static string QuoteForLog(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private static string QuoteForCommandLine(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
}
