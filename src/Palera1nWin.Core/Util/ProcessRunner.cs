using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Palera1nWin.Core.Util;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
    public bool Succeeded => ExitCode == 0;
}

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
        var outputGate = new object();
        var prompts = interactivePrompts ?? new Dictionary<string, string>();
        var needsStdin = redirectStandardInput ||
                         (prompts.Count > 0 && onInteractivePrompt is not null && !string.IsNullOrEmpty(replyText));
        var answered = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        using var promptGate = new SemaphoreSlim(1, 1);
        var promptTasks = new ConcurrentBag<Task>();
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

        if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdoutCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task HandlePossiblePromptAsync(string line)
        {
            if (!watchPrompts || onInteractivePrompt is null || prompts.Count == 0) return;

            string? matchedKey = null;
            foreach (var pair in prompts)
            {
                if (!answered.ContainsKey(pair.Key) &&
                    line.Contains(pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = pair.Key;
                    break;
                }
            }
            if (matchedKey is null) return;

            await promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (answered.ContainsKey(matchedKey) || SafeHasExited(process)) return;
                var confirmed = await onInteractivePrompt(line, matchedKey, cancellationToken).ConfigureAwait(false);
                answered.TryAdd(matchedKey, 0);

                if (!confirmed || SafeHasExited(process))
                {
                    KillProcessTree(process);
                    return;
                }

                if (!string.IsNullOrEmpty(replyText))
                {
                    await process.StandardInput.WriteAsync(replyText.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    onStdoutLine?.Invoke($"[prompt] User confirmed '{matchedKey}' - sent stdin reply.");
                }
                else
                {
                    onStdoutLine?.Invoke($"[prompt] User confirmed '{matchedKey}'.");
                }
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
            }
            catch (Exception ex)
            {
                onStderrLine?.Invoke($"[prompt] Failed to handle prompt '{matchedKey}': {ex.Message}");
                KillProcessTree(process);
            }
            finally
            {
                promptGate.Release();
            }
        }

        void QueuePrompt(string line)
        {
            if (!watchPrompts) return;
            var task = HandlePossiblePromptAsync(line);
            promptTasks.Add(task);
        }

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stdoutCompletion.TrySetResult();
                return;
            }
            lock (outputGate) stdoutBuilder.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
            QueuePrompt(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrCompletion.TrySetResult();
                return;
            }
            lock (outputGate) stderrBuilder.AppendLine(e.Data);
            onStderrLine?.Invoke(e.Data);
            QueuePrompt(e.Data);
        };

        if (!process.Start()) throw new InvalidOperationException($"Failed to start process: {fileName}");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout.HasValue) linkedCts.CancelAfter(timeout.Value);
        using var registration = linkedCts.Token.Register(() => KillProcessTree(process));

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.HasValue)
        {
            KillProcessTree(process);
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            throw new TimeoutException($"Process timed out after {timeout.Value}: {fileName}");
        }
        finally
        {
            if (needsStdin)
            {
                try { process.StandardInput.Close(); } catch { }
            }
        }

        await Task.WhenAll(stdoutCompletion.Task, stderrCompletion.Task).ConfigureAwait(false);
        var promptsToObserve = promptTasks.ToArray();
        if (promptsToObserve.Length > 0)
        {
            try { await Task.WhenAll(promptsToObserve).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        }

        string stdout;
        string stderr;
        lock (outputGate)
        {
            stdout = stdoutBuilder.ToString().TrimEnd();
            stderr = stderrBuilder.ToString().TrimEnd();
        }
        return new ProcessResult { ExitCode = process.ExitCode, StandardOutput = stdout, StandardError = stderr };
    }

    public static Task<ProcessResult> RunPowerShellAsync(
        string script,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        Action<string>? onStdoutLine = null,
        Action<string>? onStderrLine = null,
        TimeSpan? timeout = null)
    {
        return RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
            workingDirectory,
            cancellationToken: cancellationToken,
            onStdoutLine: onStdoutLine,
            onStderrLine: onStderrLine,
            timeout: timeout ?? TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// Synchronous process runner for callers that cannot use async. Both redirected
    /// streams are drained concurrently before waiting, preventing the classic full-pipe
    /// deadlock that used to bypass the timeout entirely.
    /// </summary>
    public static ProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) startInfo.WorkingDirectory = workingDirectory;
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return new ProcessResult { ExitCode = -1 };

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var wait = timeout ?? TimeSpan.FromMinutes(2);
            if (!process.WaitForExit((int)Math.Min(int.MaxValue, wait.TotalMilliseconds)))
            {
                KillProcessTree(process);
                try { process.WaitForExit(5000); } catch { }
                ObserveTask(stdoutTask);
                ObserveTask(stderrTask);
                return new ProcessResult
                {
                    ExitCode = -1,
                    StandardError = $"Process timed out after {wait}: {fileName}",
                };
            }

            Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
            return new ProcessResult
            {
                ExitCode = process.ExitCode,
                StandardOutput = stdoutTask.Result.TrimEnd(),
                StandardError = stderrTask.Result.TrimEnd(),
            };
        }
        catch (Exception ex)
        {
            return new ProcessResult { ExitCode = -1, StandardError = ex.Message };
        }
    }

    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort during cancellation/timeout.
        }
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    private static void ObserveTask(Task task)
    {
        try { task.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }
}
