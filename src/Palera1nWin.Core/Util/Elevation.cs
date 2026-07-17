using System.Diagnostics;
using System.Security.Principal;

namespace Palera1nWin.Core.Util;

public static class Elevation
{
    public static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool RelaunchAsAdmin(string arguments)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Run a one-shot elevated command (UAC). Output is not captured; check side effects after.
    /// Returns false if the user cancels UAC, the process cannot start, times out, or exits non-zero.
    /// </summary>
    public static bool RunElevatedWait(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null)
    {
        var quotedArgs = string.Join(
            " ",
            arguments.Select(QuoteArgument));

        return RunElevatedRaw(fileName, quotedArgs, timeout);
    }

    /// <summary>
    /// Run a one-shot elevated command with a raw argument string (no re-quoting).
    /// Use this when the caller has already formatted the arguments (e.g. parsed from a registry UninstallString).
    /// </summary>
    public static bool RunElevatedWaitRaw(
        string fileName,
        string rawArguments,
        TimeSpan? timeout = null)
        => RunElevatedRaw(fileName, rawArguments, timeout);

    private static bool RunElevatedRaw(string fileName, string arguments, TimeSpan? timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var waitMs = (int)(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds;
            if (!process.WaitForExit(waitMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore.
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            // UAC cancelled or start failed.
            return false;
        }
    }

    /// <summary>
    /// Quote a single argument for the Windows command line.
    /// Wraps in double-quotes if it contains spaces, and escapes any embedded quotes.
    /// </summary>
    private static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg))
        {
            return "\"\"";
        }

        if (!arg.Contains(' ', StringComparison.Ordinal) &&
            !arg.Contains('"', StringComparison.Ordinal))
        {
            return arg;
        }

        return "\"" + arg.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
