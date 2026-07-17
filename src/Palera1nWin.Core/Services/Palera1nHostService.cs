using Palera1nWin.Core.Interaction;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Services;

public sealed class Palera1nHostService
{
    private static readonly IReadOnlyDictionary<string, string> DfuInteractivePrompts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dfu-ready"] = "Press Enter when ready for DFU mode",
        };

    private readonly IUserPromptService? _userPrompts;

    public Palera1nHostService(IUserPromptService? userPrompts = null)
    {
        _userPrompts = userPrompts;
    }

    public event EventHandler<LogLine>? LogReceived;

    public Task<int> RunPalera1nAsync(
        string toolchainRoot,
        JailbreakOptions options,
        CancellationToken cancellationToken = default)
        => RunWithArgsAsync(toolchainRoot, BuildArguments(options).ToArray(), enableDfuPrompt: false, cancellationToken);

    public Task<int> RunDfuHelperAsync(
        string toolchainRoot,
        string? wslDistro,
        CancellationToken cancellationToken = default)
    {
        // --yes = skip launcher usbipd "Continue?" only.
        // --gui-dfu-prompt = hold palera1n getchar() until GUI writes the signal file
        // after the user clicks OK on the dialog that appears on the exact Press Enter line.
        var args = new List<string> { "-D", "--yes", "--gui-dfu-prompt" };
        if (!string.IsNullOrWhiteSpace(wslDistro))
        {
            args.Add("--distro");
            args.Add(wslDistro);
        }

        return RunWithArgsAsync(toolchainRoot, args.ToArray(), enableDfuPrompt: true, cancellationToken);
    }

    private async Task<int> RunWithArgsAsync(
        string toolchainRoot,
        string[] args,
        bool enableDfuPrompt,
        CancellationToken cancellationToken)
    {
        if (enableDfuPrompt)
        {
            ClearDfuEnterSignal();
        }

        var interactivePrompts = enableDfuPrompt ? DfuInteractivePrompts : null;
        ProcessPromptHandler? promptHandler = enableDfuPrompt ? HandleDfuReadyPromptAsync : null;

        var cmdPath = Paths.GetPalera1nCmd(toolchainRoot);
        if (File.Exists(cmdPath))
        {
            Emit("palera1n", $"Running {cmdPath} {string.Join(' ', args)}");
            var result = await ProcessRunner.RunAsync(
                "cmd.exe",
                new[] { "/c", cmdPath }.Concat(args).ToArray(),
                workingDirectory: toolchainRoot,
                cancellationToken: cancellationToken,
                onStdoutLine: line => Emit("palera1n", line),
                onStderrLine: line => Emit("palera1n", line, isError: true),
                interactivePrompts: interactivePrompts,
                onInteractivePrompt: promptHandler,
                replyText: string.Empty).ConfigureAwait(false);
            return result.ExitCode;
        }

        var scriptPath = Paths.GetPalera1nScript(toolchainRoot);
        if (File.Exists(scriptPath))
        {
            var psArgs = new List<string>
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                scriptPath,
                "--",
            };
            psArgs.AddRange(args);
            Emit("palera1n", $"Running powershell {scriptPath}");
            var result = await ProcessRunner.RunAsync(
                "powershell.exe",
                psArgs,
                workingDirectory: toolchainRoot,
                cancellationToken: cancellationToken,
                onStdoutLine: line => Emit("palera1n", line),
                onStderrLine: line => Emit("palera1n", line, isError: true),
                interactivePrompts: interactivePrompts,
                onInteractivePrompt: promptHandler,
                replyText: string.Empty).ConfigureAwait(false);
            return result.ExitCode;
        }

        var distro = "Ubuntu";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--distro")
            {
                distro = args[i + 1];
                break;
            }
        }

        var wslService = new WslService(distro);
        var palArgs = string.Join(' ', args.Where(a => a is not "--yes" && a is not "--distro" && a is not "--gui-dfu-prompt")
            .Where((a, idx) => idx == 0 || args[idx - 1] != "--distro")
            .Select(EscapeShellToken));
        var command = $"/opt/palera1n/palera1n {palArgs}";
        var wslResult = await wslService.RunCommandAsync(command, distro, cancellationToken)
            .ConfigureAwait(false);
        return wslResult.ExitCode;
    }

    private async Task<bool> HandleDfuReadyPromptAsync(
        string matchedLine,
        string promptKey,
        CancellationToken cancellationToken)
    {
        Emit("orchestrator", "palera1n asked: Press Enter when ready for DFU mode — showing dialog.");

        if (_userPrompts is null)
        {
            Emit("orchestrator", "No UI prompt service wired - cannot continue DFU without user confirmation.", isError: true);
            return false;
        }

        var ok = await _userPrompts.ConfirmAsync(
            new UserPromptRequest
            {
                Title = "Ready for DFU mode?",
                Message =
                    "palera1n is waiting — same as \"Press Enter when ready for DFU mode\".\n\n" +
                    "Click OK only when your fingers are ready for the button sequence.\n" +
                    "Follow the on-screen timers in the log after OK (Home+Power or volume/side, depending on your device).\n\n" +
                    "Click Cancel to abort.",
                ConfirmText = "OK",
                CancelText = "Cancel",
            },
            cancellationToken).ConfigureAwait(false);

        if (!ok)
        {
            Emit("orchestrator", "User cancelled the DFU ready prompt.", isError: true);
            return false;
        }

        WriteDfuEnterSignal();
        Emit("orchestrator", "User confirmed — signaling palera1n to continue DFU countdown.");
        return true;
    }

    private static void ClearDfuEnterSignal()
    {
        try
        {
            var path = Paths.GetDfuEnterSignalPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void WriteDfuEnterSignal()
    {
        var path = Paths.GetDfuEnterSignalPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
    }

    private static IEnumerable<string> BuildArguments(JailbreakOptions options)
    {
        foreach (var arg in options.BuildPalera1nArguments())
        {
            yield return arg;
        }

        if (!string.IsNullOrWhiteSpace(options.WslDistro))
        {
            yield return "--distro";
            yield return options.WslDistro;
        }

        if (!string.IsNullOrWhiteSpace(options.ForceBusId))
        {
            yield return "--busid";
            yield return options.ForceBusId;
        }

        if (options.SkipUsbAttach)
        {
            yield return "--no-attach";
        }

        if (options.KeepShared)
        {
            yield return "--keep-shared";
        }

        // Launcher-only: skip usbipd "Continue? [y/N]" after user already clicked Start Jailbreak.
        yield return "--yes";
    }

    private static string EscapeShellToken(string token)
    {
        if (token.Contains(' ', StringComparison.Ordinal) || token.Contains('"', StringComparison.Ordinal))
        {
            return '"' + token.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
        }

        return token;
    }

    private void Emit(string source, string message, bool isError = false)
    {
        LogReceived?.Invoke(this, new LogLine
        {
            Source = source,
            Message = message,
            IsError = isError,
        });
    }
}
