using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DarkSwordRestore.Core;

namespace DarkSwordRestore.VirtualDeviceSmoke;

public sealed record SmokeScenarioResult(
    string Name,
    bool Passed,
    double DurationSeconds,
    string Detail,
    IReadOnlyList<VirtualDeviceEvent> Events);

public sealed record VirtualSmokeReport(
    string Harness,
    string Target,
    string Host,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int Passed,
    int Failed,
    IReadOnlyList<SmokeScenarioResult> Scenarios);

public sealed record ToolInvocationResult(
    string Tool,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError)
{
    public bool Success => !TimedOut && ExitCode == 0;
    public string CombinedOutput => string.Join(Environment.NewLine,
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class VirtualSmokeSuite
{
    private readonly string _workRoot;
    private readonly string _reportPath;
    private readonly string _transcriptPath;
    private readonly List<SmokeScenarioResult> _results = [];
    private readonly List<string> _transcript = [];

    public VirtualSmokeSuite(string workRoot, string reportPath, string transcriptPath)
    {
        _workRoot = Path.GetFullPath(workRoot);
        _reportPath = Path.GetFullPath(reportPath);
        _transcriptPath = Path.GetFullPath(transcriptPath);
    }

    public async Task<int> RunAsync()
    {
        Directory.CreateDirectory(_workRoot);
        PrepareToolAliases();
        var started = DateTimeOffset.UtcNow;

        await RunScenarioAsync("hardware-gate-dfu-pwned-pongo", RunHardwareGateAsync).ConfigureAwait(false);
        await RunScenarioAsync("downgrade-complete-happy-path", RunCompleteDowngradeAsync).ConfigureAwait(false);
        await RunScenarioAsync("downgrade-blocks-pongo-before-erase", RunDowngradeBlocksPongoAsync).ConfigureAwait(false);
        await RunScenarioAsync("downgrade-blocks-wrong-pwn-marker", RunDowngradeBlocksWrongMarkerAsync).ConfigureAwait(false);
        await RunScenarioAsync("downgrade-blocks-ecid-swap", RunDowngradeBlocksEcidSwapAsync).ConfigureAwait(false);
        await RunScenarioAsync("downgrade-blocks-missing-pte", RunDowngradeBlocksMissingPteAsync).ConfigureAwait(false);
        await RunScenarioAsync("jailbreak-complete-rootless-happy-path", RunCompleteJailbreakAsync).ConfigureAwait(false);
        await RunScenarioAsync("jailbreak-blocks-wsl-before-pongo", RunJailbreakBlocksEarlyWslAsync).ConfigureAwait(false);
        await RunScenarioAsync("jailbreak-blocks-disconnect-after-pongo", RunJailbreakBlocksDisconnectAsync).ConfigureAwait(false);
        await RunScenarioAsync("jailbreak-detects-openra1n-timeout", RunJailbreakDetectsTimeoutAsync).ConfigureAwait(false);

        var finished = DateTimeOffset.UtcNow;
        var report = new VirtualSmokeReport(
            "DarkSwordRestore.VirtualDeviceSmoke",
            "iPad6,11 / A9 / CPID 0x8003",
            Environment.MachineName,
            started,
            finished,
            _results.Count(result => result.Passed),
            _results.Count(result => !result.Passed),
            _results);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_reportPath)!);
        File.WriteAllText(_reportPath, JsonSerializer.Serialize(report, options));
        File.WriteAllLines(_transcriptPath, _transcript);

        Console.WriteLine($"Virtual iPad smoke: {report.Passed} passed, {report.Failed} failed.");
        Console.WriteLine($"Report: {_reportPath}");
        Console.WriteLine($"Transcript: {_transcriptPath}");
        return report.Failed == 0 ? 0 : 1;
    }

    private async Task RunScenarioAsync(string name, Func<string, string, Task> scenario)
    {
        var scenarioRoot = Path.Combine(_workRoot, name);
        if (Directory.Exists(scenarioRoot)) Directory.Delete(scenarioRoot, recursive: true);
        Directory.CreateDirectory(scenarioRoot);
        var statePath = Path.Combine(scenarioRoot, "virtual-device.json");
        var stopwatch = Stopwatch.StartNew();
        _transcript.Add($"=== {name} ===");
        try
        {
            await scenario(scenarioRoot, statePath).ConfigureAwait(false);
            stopwatch.Stop();
            var state = VirtualDeviceStore.Load(statePath);
            _results.Add(new SmokeScenarioResult(name, true, stopwatch.Elapsed.TotalSeconds, "PASS", state.Events));
            _transcript.Add($"PASS {name} ({stopwatch.Elapsed.TotalSeconds:F3}s)");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            var events = File.Exists(statePath) ? VirtualDeviceStore.Load(statePath).Events : [];
            _results.Add(new SmokeScenarioResult(name, false, stopwatch.Elapsed.TotalSeconds, exception.ToString(), events));
            _transcript.Add($"FAIL {name}: {exception.Message}");
        }
        _transcript.Add(string.Empty);
    }

    private async Task RunHardwareGateAsync(string root, string statePath)
    {
        VirtualDeviceStore.CreateClean(statePath);
        var identity = VirtualDeviceStore.Load(statePath);
        await EnterAndVerifyPwnedDfuAsync(root, statePath, identity.ProductType, identity.Ecid).ConfigureAwait(false);
        await RequireSuccessAsync("openra1n-core", [], root, statePath).ConfigureAwait(false);
        await RequireSuccessAsync("darksword-pongo", ["probe"], root, statePath).ConfigureAwait(false);
        var state = VirtualDeviceStore.Load(statePath);
        Require(state.Mode == VirtualDeviceMode.Pongo, "Hardware gate did not end in PongoOS.");
        Require(state.Events.Any(item => item.Action == "checkm8-pwned-dfu"), "Hardware gate did not exercise pwned DFU.");
        Require(state.Events.Any(item => item.Action == "boot-pongo"), "Hardware gate did not exercise PongoOS.");
    }

    private async Task RunCompleteDowngradeAsync(string root, string statePath)
    {
        var inputs = CreateRestoreInputs(root);
        var initial = VirtualDeviceStore.CreateClean(statePath);

        await EnterAndVerifyPwnedDfuAsync(root, statePath, initial.ProductType, initial.Ecid).ConfigureAwait(false);
        await RequireSuccessAsync("turdus_merula", ["--get-shcblock", "--cache-path", inputs.Cache, inputs.Ipsw], root, statePath).ConfigureAwait(false);
        var preShc = RequireArtifact(statePath, "shcblock-1");

        EnterCleanDfu(statePath, "pre-restore SHC reboot completed");
        await EnterAndVerifyPwnedDfuAsync(root, statePath, initial.ProductType, initial.Ecid).ConfigureAwait(false);
        await RequireSuccessAsync(
            "turdus_merula",
            ["-o", "--plain-progress", "--no-input", "--cache-path", inputs.Cache, "--load-shcblock", preShc, inputs.Ipsw],
            root,
            statePath).ConfigureAwait(false);

        EnterCleanDfu(statePath, "firmware restore completed");
        await EnterAndVerifyPwnedDfuAsync(root, statePath, initial.ProductType, initial.Ecid).ConfigureAwait(false);
        await RequireSuccessAsync("turdus_merula", ["--get-shcblock", "--cache-path", inputs.Cache, inputs.Ipsw], root, statePath).ConfigureAwait(false);
        var postShc = RequireArtifact(statePath, "shcblock-2");

        EnterCleanDfu(statePath, "post-restore SHC reboot completed");
        await EnterAndVerifyPwnedDfuAsync(root, statePath, initial.ProductType, initial.Ecid).ConfigureAwait(false);
        await RequireSuccessAsync(
            "turdus_merula",
            ["--get-pteblock", "--load-shcblock", postShc, "--cache-path", inputs.Cache, inputs.Ipsw],
            root,
            statePath).ConfigureAwait(false);
        var pte = RequireArtifact(statePath, "pteblock");

        EnterCleanDfu(statePath, "PTE generation completed");
        await RequireSuccessAsync("openra1n-core", [], root, statePath).ConfigureAwait(false);
        await RequireSuccessAsync("darksword-pongo", ["probe"], root, statePath).ConfigureAwait(false);
        await RequireSuccessAsync(
            "darksword-pongo",
            ["boot", "--pteblock", pte, "--sep-racer", inputs.SepRacer, "--kpf", inputs.Kpf],
            root,
            statePath).ConfigureAwait(false);

        var state = VirtualDeviceStore.Load(statePath);
        Require(state.EraseStarted, "The happy-path simulation never reached the erase stage.");
        Require(state.RestoreCompleted, "The virtual firmware restore did not complete.");
        Require(state.TetherBootCompleted && state.Mode == VirtualDeviceMode.BootedIos15, "The virtual iOS 15 tether boot did not complete.");
        Require(state.Events.Count(item => item.Action == "checkm8-pwned-dfu") == 4, "Downgrade did not use four verified pwned-DFU entries.");
        Require(state.Events.Count(item => item.Action == "get-shcblock") == 2, "Downgrade did not create both SHC blocks.");
        Require(state.Events.Count(item => item.Action == "get-pteblock") == 1, "Downgrade did not create exactly one PTE block.");
        Require(state.Events.Count(item => item.Action == "boot-pongo") == 1, "PongoOS was entered before final tether boot or more than once.");
        var erase = state.Events.Single(item => item.Action == "erase-start").Sequence;
        var verifiedBeforeErase = state.Events.Last(item => item.Action == "verify-pwned-dfu" && item.Sequence < erase).Sequence;
        Require(verifiedBeforeErase < erase, "Erase started before pwned DFU verification.");
        var pteSequence = state.Events.Single(item => item.Action == "get-pteblock").Sequence;
        var pongoSequence = state.Events.Single(item => item.Action == "boot-pongo").Sequence;
        Require(pongoSequence > pteSequence, "PongoOS was uploaded during SHC, restore, or PTE generation.");
    }

    private async Task RunDowngradeBlocksPongoAsync(string root, string statePath)
    {
        var inputs = CreateRestoreInputs(root);
        var dummyShc = Path.Combine(root, "dummy-shcblock.bin");
        File.WriteAllText(dummyShc, "dummy");
        VirtualDeviceStore.CreateClean(statePath);
        await RequireSuccessAsync("openra1n-core", [], root, statePath).ConfigureAwait(false);
        var result = await InvokeToolAsync("turdus_merula", ["-o", "--load-shcblock", dummyShc, inputs.Ipsw], root, statePath).ConfigureAwait(false);
        Require(!result.Success, "turdus accepted PongoOS instead of pwned DFU.");
        Require(!VirtualDeviceStore.Load(statePath).EraseStarted, "Erase started while the virtual device was in PongoOS.");
    }

    private async Task RunDowngradeBlocksWrongMarkerAsync(string root, string statePath)
    {
        VirtualDeviceStore.CreateClean(statePath, "wrong-marker");
        await RequireSuccessAsync("openra1n-core", [DowngradeStagePlan.PwnedDfuOnlyArgument], root, statePath).ConfigureAwait(false);
        var query = await RequireSuccessAsync("irecovery", ["-q"], root, statePath).ConfigureAwait(false);
        Require(!DowngradeStagePlan.IsPwnedDfuQueryOutput(query.CombinedOutput), "Legacy YOLO marker was accepted as PWND:[yolo].");
        var result = await InvokeToolAsync("turdus_merula", ["--get-shcblock", "fake.ipsw"], root, statePath).ConfigureAwait(false);
        Require(!result.Success, "turdus accepted the wrong pwned-DFU marker.");
        Require(VirtualDeviceStore.Load(statePath).Artifacts.Count == 0, "A restore artifact was created from an unverified marker.");
    }

    private async Task RunDowngradeBlocksEcidSwapAsync(string root, string statePath)
    {
        var initial = VirtualDeviceStore.CreateClean(statePath, "ecid-swap-after-pwn");
        var rejected = false;
        try
        {
            await EnterAndVerifyPwnedDfuAsync(root, statePath, initial.ProductType, initial.Ecid).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Require(rejected, "The virtual ECID swap was not rejected.");
        Require(!VirtualDeviceStore.Load(statePath).EraseStarted, "Erase started after the ECID changed.");
    }

    private async Task RunDowngradeBlocksMissingPteAsync(string root, string statePath)
    {
        var inputs = CreateRestoreInputs(root);
        VirtualDeviceStore.CreateClean(statePath);
        await RequireSuccessAsync("openra1n-core", [], root, statePath).ConfigureAwait(false);
        var missingPte = Path.Combine(root, "missing-pteblock.bin");
        var result = await InvokeToolAsync(
            "darksword-pongo",
            ["boot", "--pteblock", missingPte, "--sep-racer", inputs.SepRacer, "--kpf", inputs.Kpf],
            root,
            statePath).ConfigureAwait(false);
        Require(!result.Success, "Tether boot accepted a missing PTE block.");
        Require(!VirtualDeviceStore.Load(statePath).TetherBootCompleted, "Tether boot completed without a PTE block.");
    }

    private async Task RunCompleteJailbreakAsync(string root, string statePath)
    {
        VirtualDeviceStore.CreateClean(statePath);
        await RequireSuccessAsync("openra1n-core", [], root, statePath).ConfigureAwait(false);
        await RequireSuccessAsync("darksword-pongo", ["probe"], root, statePath).ConfigureAwait(false);
        await RequireSuccessAsync("usbipd", ["attach", "--wsl", "--busid", "1-2"], root, statePath).ConfigureAwait(false);
        await RequireSuccessAsync("wsl", ["palera1n", "--continue-from-pongo", "--rootless"], root, statePath).ConfigureAwait(false);

        var state = VirtualDeviceStore.Load(statePath);
        Require(state.Mode == VirtualDeviceMode.Jailbroken && state.JailbreakInstalled, "Virtual rootless jailbreak did not complete.");
        var pongo = state.Events.Single(item => item.Action == "boot-pongo").Sequence;
        var attach = state.Events.Single(item => item.Action == "attach-wsl").Sequence;
        var continuation = state.Events.Single(item => item.Action == "palera1n-rootless").Sequence;
        Require(pongo < attach && attach < continuation, "Windows-to-WSL Pongo ownership order was incorrect.");
        Require(!state.EraseStarted, "Jailbreak workflow unexpectedly started a restore erase.");
    }

    private async Task RunJailbreakBlocksEarlyWslAsync(string root, string statePath)
    {
        VirtualDeviceStore.CreateClean(statePath);
        var attach = await InvokeToolAsync("usbipd", ["attach", "--wsl", "--busid", "1-2"], root, statePath).ConfigureAwait(false);
        var continuation = await InvokeToolAsync("wsl", ["palera1n", "--continue-from-pongo", "--rootless"], root, statePath).ConfigureAwait(false);
        Require(!attach.Success && !continuation.Success, "WSL continuation started before PongoOS existed.");
        Require(!VirtualDeviceStore.Load(statePath).JailbreakInstalled, "Jailbreak installed without PongoOS.");
    }

    private async Task RunJailbreakBlocksDisconnectAsync(string root, string statePath)
    {
        VirtualDeviceStore.CreateClean(statePath);
        await RequireSuccessAsync("openra1n-core", [], root, statePath).ConfigureAwait(false);
        await RequireSuccessAsync("usbipd", ["attach", "--wsl", "--busid", "1-2"], root, statePath).ConfigureAwait(false);
        VirtualDeviceStore.Mutate(statePath, document =>
        {
            document.Mode = VirtualDeviceMode.Disconnected;
            document.AddEvent("harness", "disconnect", "Virtual USB cable disconnected after Pongo handoff.");
        });
        var continuation = await InvokeToolAsync("wsl", ["palera1n", "--continue-from-pongo", "--rootless"], root, statePath).ConfigureAwait(false);
        Require(!continuation.Success, "WSL continuation ignored a disconnected Pongo device.");
        Require(!VirtualDeviceStore.Load(statePath).JailbreakInstalled, "Jailbreak installed after USB disconnect.");
    }

    private async Task RunJailbreakDetectsTimeoutAsync(string root, string statePath)
    {
        VirtualDeviceStore.CreateClean(statePath, "openra1n-timeout");
        var result = await InvokeToolAsync("openra1n-core", [], root, statePath, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        Require(result.TimedOut, "The simulated openra1n timeout was not detected.");
        var state = VirtualDeviceStore.Load(statePath);
        Require(state.Mode == VirtualDeviceMode.CleanDfu && !state.JailbreakInstalled, "Timeout changed the virtual device into a successful state.");
    }

    private async Task EnterAndVerifyPwnedDfuAsync(
        string root,
        string statePath,
        string expectedProductType,
        string expectedEcid)
    {
        await RequireSuccessAsync("openra1n-core", [DowngradeStagePlan.PwnedDfuOnlyArgument], root, statePath).ConfigureAwait(false);
        var query = await RequireSuccessAsync("irecovery", ["-q"], root, statePath).ConfigureAwait(false);
        Require(DowngradeStagePlan.IsPwnedDfuQueryOutput(query.CombinedOutput), "irecovery did not report the exact PWND:[yolo] marker.");
        Require(QueryValue(query.StandardOutput, "PRODUCT") == expectedProductType, "ProductType changed during pwned-DFU entry.");
        Require(QueryValue(query.StandardOutput, "ECID") == expectedEcid, "ECID changed during pwned-DFU entry.");
        VirtualDeviceStore.Mutate(statePath, document =>
        {
            document.VerifiedPwnGeneration = document.PwnGeneration;
            document.AddEvent("harness", "verify-pwned-dfu", $"Verified {DowngradeStagePlan.RequiredPwnedDfuMarker}, ProductType, and ECID.");
        });
    }

    private static void EnterCleanDfu(string statePath, string reason)
    {
        VirtualDeviceStore.Mutate(statePath, document =>
        {
            document.Mode = VirtualDeviceMode.CleanDfu;
            document.PwnedMarker = null;
            document.WslAttached = false;
            document.AddEvent("harness", "enter-clean-dfu", reason);
        });
    }

    private async Task<ToolInvocationResult> RequireSuccessAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string statePath)
    {
        var result = await InvokeToolAsync(tool, arguments, workingDirectory, statePath).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException($"{tool} failed. Exit={result.ExitCode}; timeout={result.TimedOut}; {result.CombinedOutput}");
        return result;
    }

    private async Task<ToolInvocationResult> InvokeToolAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string statePath,
        TimeSpan? timeout = null)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, tool + ".exe");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment["DARKSWORD_VIRTUAL_DEVICE_STATE"] = statePath;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException($"Unable to start virtual tool {tool}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var timedOut = false;
        using var timeoutSource = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        var standardOutput = await stdoutTask.ConfigureAwait(false);
        var standardError = await stderrTask.ConfigureAwait(false);
        var exitCode = timedOut ? -1 : process.ExitCode;
        var result = new ToolInvocationResult(tool, arguments, exitCode, timedOut, standardOutput, standardError);
        _transcript.Add($"> {tool}.exe {string.Join(' ', arguments.Select(Quote))}");
        if (!string.IsNullOrWhiteSpace(standardOutput)) _transcript.Add(standardOutput.TrimEnd());
        if (!string.IsNullOrWhiteSpace(standardError)) _transcript.Add(standardError.TrimEnd());
        _transcript.Add($"exit={exitCode} timeout={timedOut}");
        return result;
    }

    private static (string Ipsw, string Cache, string SepRacer, string Kpf) CreateRestoreInputs(string root)
    {
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(cache);
        var ipsw = Path.Combine(root, "iPad_64bit_TouchID_ASTC_15.0_19A346_Restore.ipsw");
        var sepRacer = Path.Combine(root, "sep_racer.bin");
        var kpf = Path.Combine(root, "kpf.bin");
        File.WriteAllText(ipsw, "virtual IPSW fixture for iPad6,11");
        File.WriteAllBytes(sepRacer, Enumerable.Repeat((byte)0x53, 512).ToArray());
        File.WriteAllBytes(kpf, Enumerable.Repeat((byte)0x4B, 512).ToArray());
        return (ipsw, cache, sepRacer, kpf);
    }

    private static string RequireArtifact(string statePath, string key)
    {
        var state = VirtualDeviceStore.Load(statePath);
        if (!state.Artifacts.TryGetValue(key, out var path) || !File.Exists(path))
            throw new InvalidOperationException($"Expected virtual artifact {key} was not created.");
        return path;
    }

    private static string? QueryValue(string output, string key)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            if (string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return line[(separator + 1)..].Trim();
        }
        return null;
    }

    private static void PrepareToolAliases()
    {
        var currentExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("Unable to locate the smoke harness executable.");
        foreach (var tool in VirtualToolProgram.KnownToolNames)
        {
            var destination = Path.Combine(AppContext.BaseDirectory, tool + ".exe");
            if (!string.Equals(Path.GetFullPath(currentExecutable), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                File.Copy(currentExecutable, destination, overwrite: true);
        }
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
