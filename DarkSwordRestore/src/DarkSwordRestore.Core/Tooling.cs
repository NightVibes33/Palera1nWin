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
        new[] { OpenRa1n, IdeviceRestore, PongoBridge, WdiSimple }
            .Where(path => !File.Exists(path))
            .ToArray();
}

public sealed class ToolProcessRunner
{
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

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("The elevated process did not start.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
        });
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        var result = new ToolResult(startInfo.FileName, startInfo.Arguments, process.ExitCode, string.Empty, string.Empty, stopwatch.Elapsed);
        if (!result.Success) throw new InvalidOperationException($"Elevated tool exited with code {result.ExitCode}.");
        return result;
    }

    private static string QuoteForLog(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private static string QuoteForCommandLine(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
}
