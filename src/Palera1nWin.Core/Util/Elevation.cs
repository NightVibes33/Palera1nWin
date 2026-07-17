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
    /// Returns false if the user cancels UAC or the process cannot start.
    /// </summary>
    public static bool RunElevatedWait(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null)
    {
        var quotedArgs = string.Join(
            " ",
            arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = quotedArgs,
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
}
