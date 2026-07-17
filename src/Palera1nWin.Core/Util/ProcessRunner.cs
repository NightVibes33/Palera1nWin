using System.Diagnostics;
using System.Text;

namespace Palera1nWin.Core.Util;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = string.Empty;

    public string StandardError { get; init; } = string.Empty;

    public string CombinedOutput =>
        string.IsNullOrWhiteSpace(StandardError)
            ? StandardOutput
            : StandardOutput + Environment.NewLine + StandardError;

    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Called when process output matches an interactive prompt.
/// Return true to send <paramref name="replyText"/> to stdin (after user confirms in UI).
/// </summary>
public delegate Task<bool> ProcessPromptHandler(
    string matchedLine,
    string promptKey,
    CancellationToken cancellationToken);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        TimeSpan? timeout = null,
        bool redirectStandardInput = false,
        IReadOnlyDictionary<string, string>? interactivePrompts = null,
        ProcessPromptHandler? onInteractivePrompt = null,
        string replyText = "\n")
    {
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        var prompts = interactivePrompts ?? new Dictionary<string, string>();
        var needsStdin = redirectStandardInput ||
                         (prompts.Count > 0 && onInteractivePrompt is not null && !string.IsNullOrEmpty(replyText));
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var promptGate = new SemaphoreSlim(1, 1);
        var watchPrompts = prompts.Count > 0 && onInteractivePrompt is not null;

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = needsStdin,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment != null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var stdoutCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task HandlePossiblePromptAsync(string line)
        {
            if (!watchPrompts || onInteractivePrompt is null || process.HasExited || prompts.Count == 0)
            {
                return;
            }

            string? matchedKey = null;
            foreach (var pair in prompts)
            {
                if (answered.Contains(pair.Key))
                {
                    continue;
                }

                if (line.Contains(pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = pair.Key;
                    break;
                }
            }

            if (matchedKey is null)
            {
                return;
            }

            await promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (answered.Contains(matchedKey) || process.HasExited)
                {
                    return;
                }

                var confirmed = await onInteractivePrompt(line, matchedKey, cancellationToken)
                    .ConfigureAwait(false);

                if (!confirmed || process.HasExited)
                {
                    answered.Add(matchedKey);
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch
                    {
                        // Ignore.
                    }

                    return;
                }

                try
                {
                    if (!string.IsNullOrEmpty(replyText))
                    {
                        await process.StandardInput.WriteAsync(replyText.AsMemory(), cancellationToken)
                            .ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                        onStdoutLine?.Invoke($"[prompt] User confirmed '{matchedKey}' - sent stdin reply.");
                    }
                    else
                    {
                        onStdoutLine?.Invoke($"[prompt] User confirmed '{matchedKey}'.");
                    }

                    answered.Add(matchedKey);
                }
                catch
                {
                    // Child may have closed stdin.
                }
            }
            finally
            {
                promptGate.Release();
            }
        }

        void OnOutput(string? data, TaskCompletionSource completion, Action<string>? sink)
        {
            if (data is null)
            {
                completion.TrySetResult();
                return;
            }

            stdoutBuilder.AppendLine(data);
            sink?.Invoke(data);
            _ = HandlePossiblePromptAsync(data);
        }

        process.OutputDataReceived += (_, e) => OnOutput(e.Data, stdoutCompletion, onStdoutLine);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrCompletion.TrySetResult();
                return;
            }

            stderrBuilder.AppendLine(e.Data);
            onStderrLine?.Invoke(e.Data);
            _ = HandlePossiblePromptAsync(e.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill failures during cancellation.
            }
        });

        if (timeout.HasValue)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout.Value);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore kill failures on timeout.
                }

                throw new TimeoutException($"Process timed out: {fileName}");
            }
        }
        else
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (needsStdin)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
                // Ignore.
            }
        }

        await Task.WhenAll(stdoutCompletion.Task, stderrCompletion.Task).ConfigureAwait(false);

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdoutBuilder.ToString().TrimEnd(),
            StandardError = stderrBuilder.ToString().TrimEnd(),
        };
    }

    public static Task<ProcessResult> RunPowerShellAsync(
        string script,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null)
    {
        return RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script },
            workingDirectory,
            cancellationToken: cancellationToken,
            onStdoutLine: onStdoutLine,
            onStderrLine: onStderrLine);
    }
}
