using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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
        var root = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory, "toolchain");
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
            OpenRa1n, IdeviceRestore, PongoBridge, WdiSimple,
            Path.Combine(Root, "ideviceinfo.exe"),
            Path.Combine(Root, "irecovery.exe"),
        }.Where(path => !File.Exists(path)).ToArray();
}

internal sealed class BoundedLineBuffer
{
    private readonly int _capacity;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();

    public BoundedLineBuffer(int capacity = 10_000) => _capacity = Math.Max(100, capacity);

    public void Add(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity) _lines.Dequeue();
        }
    }

    public override string ToString()
    {
        lock (_gate) return string.Join(Environment.NewLine, _lines);
    }
}

public sealed class ToolProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly BoundedLineBuffer _stdout = new();
    private readonly BoundedLineBuffer _stderr = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly TaskCompletionSource _stdoutClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stderrClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _timeout;
    private int _disposed;

    internal ToolProcessSession(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var startInfo = CreateStartInfo(fileName, arguments, workingDirectory);
        FileName = fileName;
        Arguments = string.Join(' ', startInfo.ArgumentList.Select(QuoteForLog));
        _timeout = timeout;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lifetime.CancelAfter(timeout);
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) { _stdoutClosed.TrySetResult(); return; }
            _stdout.Add(args.Data);
            InvokeOutput(onOutput, args.Data);
        };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) { _stderrClosed.TrySetResult(); return; }
            _stderr.Add(args.Data);
            InvokeOutput(onOutput, args.Data);
        };

        if (!_process.Start()) throw new InvalidOperationException($"Unable to start {fileName}.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _cancellationRegistration = _lifetime.Token.Register(Kill);
        Completion = CompleteAsync(cancellationToken);
    }

    public string FileName { get; }
    public string Arguments { get; }
    public Task<ToolResult> Completion { get; }
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { }
    }

    private async Task<ToolResult> CompleteAsync(CancellationToken callerToken)
    {
        try
        {
            await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            Kill();
            try { await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw new TimeoutException(
                $"{Path.GetFileName(FileName)} exceeded its hard timeout of {_timeout}. Its process tree was terminated and the restore session was preserved.");
        }
        finally
        {
            if (_lifetime.IsCancellationRequested) Kill();
        }

        await WaitForOutputCloseAsync().ConfigureAwait(false);
        _stopwatch.Stop();
        return new ToolResult(FileName, Arguments, _process.ExitCode, _stdout.ToString(), _stderr.ToString(), _stopwatch.Elapsed);
    }

    private async Task WaitForOutputCloseAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Task.WhenAll(_stdoutClosed.Task, _stderrClosed.Task).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        Kill();
        try { await Completion.ConfigureAwait(false); } catch { }
        _cancellationRegistration.Dispose();
        _lifetime.Dispose();
        _process.Dispose();
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments, string? workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static void InvokeOutput(Action<string>? output, string line)
    {
        try { output?.Invoke(line); } catch { }
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
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (!File.Exists(fileName)) throw new FileNotFoundException("Required tool was not found.", fileName);
        return new ToolProcessSession(
            fileName,
            arguments,
            workingDirectory,
            onOutput,
            cancellationToken,
            timeout ?? ResolveDefaultTimeout(fileName));
    }

    public async Task<ToolResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        CancellationToken cancellationToken,
        bool requireZeroExitCode = true,
        TimeSpan? timeout = null)
    {
        await using var session = StartSession(
            fileName,
            arguments,
            workingDirectory,
            onOutput,
            cancellationToken,
            timeout ?? ResolveDefaultTimeout(fileName));
        var result = await session.Completion.ConfigureAwait(false);
        if (requireZeroExitCode && !result.Success)
            throw new InvalidOperationException(
                $"{Path.GetFileName(fileName)} exited with code {result.ExitCode}.{Environment.NewLine}{result.StandardError}");
        return result;
    }

    public Task<ToolResult> RunElevatedAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("Driver operations are Windows-only.");
        if (!File.Exists(fileName)) throw new FileNotFoundException("The elevated tool was not found.", fileName);

        var start = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = BuildWindowsCommandLine(arguments),
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            UseShellExecute = true,
            Verb = "runas",
        };
        return RunElevatedCoreAsync(start, cancellationToken, timeout ?? TimeSpan.FromMinutes(3));
    }

    private static async Task<ToolResult> RunElevatedCoreAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken,
        TimeSpan timeout)
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
                $"Windows administrator approval was cancelled for {Path.GetFileName(startInfo.FileName)}.", exception);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorAccessDenied)
        {
            throw new InvalidOperationException(
                $"Windows blocked administrator access for {Path.GetFileName(startInfo.FileName)}.", exception);
        }

        using (process)
        using (var timeoutCts = new CancellationTokenSource(timeout))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
        using (var registration = linked.Token.Register(() =>
               {
                   try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
               }))
        {
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"{Path.GetFileName(startInfo.FileName)} timed out after {timeout}.");
            }
            stopwatch.Stop();
            var result = new ToolResult(startInfo.FileName, startInfo.Arguments, process.ExitCode, "", "", stopwatch.Elapsed);
            if (!result.Success)
                throw new InvalidOperationException(
                    $"{Path.GetFileName(startInfo.FileName)} exited with code {result.ExitCode}; the USB driver was not changed.");
            return result;
        }
    }

    internal static TimeSpan ResolveDefaultTimeout(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (name.Contains("turdus", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("idevicerestore", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromHours(2);
        if (name.Contains("openra1n", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(3);
        if (name.Contains("pongo", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(5);
        if (name.Contains("wdi", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(3);
        if (name.Contains("irecovery", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ideviceinfo", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("powershell", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromSeconds(30);
        return TimeSpan.FromMinutes(20);
    }

    internal static string BuildWindowsCommandLine(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(character => char.IsWhiteSpace(character) || character == '"')) return value;
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            builder.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }
}
