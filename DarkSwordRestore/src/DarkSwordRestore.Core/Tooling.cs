using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DarkSwordRestore.Core;

public sealed record ToolchainPaths(
    string Root,
    string OpenRa1n,
    string IdeviceRestore,
    string PongoBridge,
    string WdiSimple,
    string Gaster)
{
    public static ToolchainPaths FromApplicationDirectory(string? applicationDirectory = null)
    {
        var baseDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        var root = ResolveToolchainRoot(baseDirectory);
        var native = Path.Combine(root, "native");
        return new ToolchainPaths(
            root,
            ResolveTool(root, native, "openra1n.exe"),
            ResolveTool(root, native, "turdus_merula.exe"),
            ResolveTool(root, native, "darksword-pongo.exe"),
            ResolveTool(root, native, "wdi-simple.exe"),
            ResolveTool(root, native, "gaster.exe"));
    }

    public IReadOnlyList<string> MissingFiles() =>
        new[]
        {
            OpenRa1n,
            IdeviceRestore,
            PongoBridge,
            WdiSimple,
            Gaster,
            ResolveTool(Root, Path.Combine(Root, "native"), "ideviceinfo.exe"),
            ResolveTool(Root, Path.Combine(Root, "native"), "irecovery.exe")
        }
        .Where(path => !File.Exists(path))
        .ToArray();

    private static string ResolveToolchainRoot(string baseDirectory)
    {
        foreach (var candidate in EnumerateToolchainCandidates(baseDirectory))
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetFullPath(Path.Combine(baseDirectory, "toolchain"));
    }

    private static IEnumerable<string> EnumerateToolchainCandidates(string baseDirectory)
    {
        yield return Path.Combine(baseDirectory, "toolchain");

        var darkSwordEnv = Environment.GetEnvironmentVariable("DARKSWORD_TOOLCHAIN");
        if (!string.IsNullOrWhiteSpace(darkSwordEnv))
        {
            yield return darkSwordEnv.Trim();
        }

        var palera1nEnv = Environment.GetEnvironmentVariable("PALERA1N_TOOLCHAIN");
        if (!string.IsNullOrWhiteSpace(palera1nEnv))
        {
            yield return palera1nEnv.Trim();
        }

        var current = new DirectoryInfo(baseDirectory);
        while (current is not null)
        {
            yield return Path.Combine(current.FullName, "DarkSwordRestore", "toolchain");
            yield return Path.Combine(current.FullName, "toolchain");
            current = current.Parent;
        }
    }

    private static string ResolveTool(string root, string native, string fileName)
    {
        var nativePath = Path.Combine(native, fileName);
        return File.Exists(nativePath) ? nativePath : Path.Combine(root, fileName);
    }
}

public sealed class ToolProcessRunner
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorCancelled = 1223;

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

    public async Task<ToolResult> RunWithTimeoutAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        Action<string>? onOutput,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool requireZeroExitCode = true)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await RunAsync(
                    fileName,
                    arguments,
                    workingDirectory,
                    onOutput,
                    linkedCts.Token,
                    requireZeroExitCode)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalSeconds:F0} seconds.");
        }
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
                $"Windows administrator approval was cancelled. DarkSword did not install the Apple DFU libusbK driver. " +
                $"Run Palera1nWin as administrator, retry the downgrade, and choose Yes on the User Account Control prompt for {Path.GetFileName(startInfo.FileName)}.",
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
                    $"{Path.GetFileName(startInfo.FileName)} could not install the Apple DFU libusbK driver and exited with code {result.ExitCode}. " +
                    "Reconnect the device in DFU mode, run Palera1nWin as administrator, and retry.");
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
