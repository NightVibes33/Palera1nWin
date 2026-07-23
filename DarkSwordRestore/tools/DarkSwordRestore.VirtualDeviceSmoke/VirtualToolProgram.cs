namespace DarkSwordRestore.VirtualDeviceSmoke;

public static class VirtualToolProgram
{
    public static readonly HashSet<string> KnownToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ideviceinfo",
        "irecovery",
        "openra1n-core",
        "turdus_merula",
        "darksword-pongo",
        "usbipd",
        "wsl",
    };

    public static async Task<int> RunAsync(string toolName, string[] args)
    {
        var statePath = Environment.GetEnvironmentVariable("DARKSWORD_VIRTUAL_DEVICE_STATE");
        if (string.IsNullOrWhiteSpace(statePath))
            return Fail("DARKSWORD_VIRTUAL_DEVICE_STATE was not supplied.");

        try
        {
            return toolName.ToLowerInvariant() switch
            {
                "ideviceinfo" => RunIDeviceInfo(statePath, args),
                "irecovery" => RunIRecovery(statePath, args),
                "openra1n-core" => await RunOpenRa1nAsync(statePath, args).ConfigureAwait(false),
                "turdus_merula" => RunTurdus(statePath, args),
                "darksword-pongo" => RunPongoBridge(statePath, args),
                "usbipd" => RunUsbIpd(statePath, args),
                "wsl" => RunWsl(statePath, args),
                _ => Fail($"Unknown virtual tool name: {toolName}"),
            };
        }
        catch (Exception exception)
        {
            return Fail(exception.Message);
        }
    }

    private static int RunIDeviceInfo(string statePath, string[] args)
    {
        var state = VirtualDeviceStore.Load(statePath);
        var key = GetArgumentValue(args, "-k");
        if (string.Equals(key, "ProductType", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine(state.ProductType);
        else if (string.Equals(key, "ProductVersion", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine(state.RestoreCompleted ? "15.0" : "16.7.11");
        else
        {
            Console.WriteLine($"ProductType: {state.ProductType}");
            Console.WriteLine($"UniqueChipID: {state.Ecid}");
            Console.WriteLine($"ProductVersion: {(state.RestoreCompleted ? "15.0" : "16.7.11")}");
        }
        return 0;
    }

    private static int RunIRecovery(string statePath, string[] args)
    {
        if (!args.Any(argument => argument is "-q" or "--query"))
            return Fail("Virtual irecovery supports only -q/--query.");

        var state = VirtualDeviceStore.Load(statePath);
        if (state.Mode == VirtualDeviceMode.Disconnected)
            return Fail("No Apple USB device is connected.");

        Console.WriteLine($"ECID: {state.Ecid}");
        Console.WriteLine($"PRODUCT: {state.ProductType}");
        Console.WriteLine($"CPID: {state.Cpid}");
        Console.WriteLine($"MODE: {ToUsbMode(state.Mode)}");
        if (state.Mode == VirtualDeviceMode.PwnedDfu)
        {
            if (string.Equals(state.PwnedMarker, "PWND:[yolo]", StringComparison.Ordinal))
                Console.WriteLine("PWND: yolo");
            else if (!string.IsNullOrWhiteSpace(state.PwnedMarker))
                Console.WriteLine(state.PwnedMarker);
        }
        return 0;
    }

    private static async Task<int> RunOpenRa1nAsync(string statePath, string[] args)
    {
        var state = VirtualDeviceStore.Load(statePath);
        if (string.Equals(state.Fault, "openra1n-timeout", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        var pwnedDfuOnly = args.Length == 1 && string.Equals(args[0], "--pwned-dfu-only", StringComparison.Ordinal);
        if (args.Length > 0 && !pwnedDfuOnly)
            return Fail("Usage: openra1n-core.exe [--pwned-dfu-only]");
        if (state.Mode is not (VirtualDeviceMode.CleanDfu or VirtualDeviceMode.PwnedDfu))
            return Fail($"checkm8 requires DFU; current virtual mode is {state.Mode}.");

        if (pwnedDfuOnly)
        {
            VirtualDeviceStore.Mutate(statePath, document =>
            {
                document.PwnGeneration++;
                document.VerifiedPwnGeneration = 0;
                document.Mode = VirtualDeviceMode.PwnedDfu;
                document.PwnedMarker = string.Equals(document.Fault, "wrong-marker", StringComparison.OrdinalIgnoreCase)
                    ? "YOLO:checkra1n"
                    : "PWND:[yolo]";
                if (string.Equals(document.Fault, "ecid-swap-after-pwn", StringComparison.OrdinalIgnoreCase))
                    document.Ecid = "0xDEADBEEF00000001";
                document.AddEvent("openra1n-core.exe", "checkm8-pwned-dfu", $"Generation {document.PwnGeneration}; marker={document.PwnedMarker}");
            });
            Console.WriteLine("Pwned DFU ready; PongoOS was not uploaded.");
            return 0;
        }

        VirtualDeviceStore.Mutate(statePath, document =>
        {
            document.Mode = VirtualDeviceMode.Pongo;
            document.PwnedMarker = null;
            document.AddEvent("openra1n-core.exe", "boot-pongo", "Virtual USB changed from 05AC:1227 to 05AC:4141.");
        });
        Console.WriteLine("PongoOS USB 05AC:4141 enumerated.");
        return 0;
    }

    private static int RunTurdus(string statePath, string[] args)
    {
        var state = VirtualDeviceStore.Load(statePath);
        var readinessError = ValidateVerifiedPwnedDfu(state);
        if (readinessError is not null)
            return Fail(readinessError);

        if (args.Contains("--get-shcblock", StringComparer.Ordinal))
        {
            var index = state.Artifacts.Keys.Count(key => key.StartsWith("shcblock-", StringComparison.OrdinalIgnoreCase)) + 1;
            var path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, $"shcblock-{index}.bin"));
            File.WriteAllBytes(path, CreateArtifactBytes($"SHC:{index}:{state.ProductType}:{state.Ecid}"));
            VirtualDeviceStore.Mutate(statePath, document =>
            {
                document.Artifacts[$"shcblock-{index}"] = path;
                document.Mode = VirtualDeviceMode.AwaitingDfu;
                document.PwnedMarker = null;
                document.AddEvent("turdus_merula.exe", "get-shcblock", path);
            });
            Console.WriteLine($"Created SHC block: {path}");
            return 0;
        }

        if (args.Contains("--get-pteblock", StringComparer.Ordinal))
        {
            var shcPath = GetArgumentValue(args, "--load-shcblock");
            if (string.IsNullOrWhiteSpace(shcPath) || !File.Exists(shcPath))
                return Fail("PTE generation requires an existing --load-shcblock file.");
            var path = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "pteblock.bin"));
            File.WriteAllBytes(path, CreateArtifactBytes($"PTE:{state.ProductType}:{state.Ecid}"));
            VirtualDeviceStore.Mutate(statePath, document =>
            {
                document.Artifacts["pteblock"] = path;
                document.Mode = VirtualDeviceMode.AwaitingDfu;
                document.PwnedMarker = null;
                document.AddEvent("turdus_merula.exe", "get-pteblock", path);
            });
            Console.WriteLine($"Created PTE block: {path}");
            return 0;
        }

        if (args.Contains("-o", StringComparer.Ordinal))
        {
            var shcPath = GetArgumentValue(args, "--load-shcblock");
            if (string.IsNullOrWhiteSpace(shcPath) || !File.Exists(shcPath))
                return Fail("Restore requires an existing --load-shcblock file.");
            var ipswPath = args.LastOrDefault(argument => argument.EndsWith(".ipsw", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(ipswPath) || !File.Exists(ipswPath))
                return Fail("Restore requires an existing IPSW file.");

            VirtualDeviceStore.Mutate(statePath, document =>
            {
                document.EraseStarted = true;
                document.AddEvent("turdus_merula.exe", "erase-start", ipswPath);
                document.RestoreCompleted = true;
                document.Mode = VirtualDeviceMode.AwaitingDfu;
                document.PwnedMarker = null;
                document.AddEvent("turdus_merula.exe", "restore-complete", "Virtual iOS 15 filesystem restored.");
            });
            Console.WriteLine("10% Preparing restore");
            Console.WriteLine("55% Restoring filesystem");
            Console.WriteLine("100% Restore complete");
            return 0;
        }

        return Fail("Unsupported virtual turdus_merula operation.");
    }

    private static int RunPongoBridge(string statePath, string[] args)
    {
        var state = VirtualDeviceStore.Load(statePath);
        if (state.Mode != VirtualDeviceMode.Pongo)
            return Fail($"Pongo bridge requires 05AC:4141; current virtual mode is {state.Mode}.");
        if (args.Length == 1 && string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Exactly one PongoOS device found at 05AC:4141.");
            return 0;
        }
        if (args.Length > 0 && string.Equals(args[0], "boot", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var option in new[] { "--pteblock", "--sep-racer", "--kpf" })
            {
                var path = GetArgumentValue(args, option);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return Fail($"Pongo boot requires an existing {option} file.");
            }
            VirtualDeviceStore.Mutate(statePath, document =>
            {
                document.TetherBootCompleted = true;
                document.Mode = VirtualDeviceMode.BootedIos15;
                document.AddEvent("darksword-pongo.exe", "tether-boot", "sep pte; sep pwn_pte; kpf-tethered; bootux");
            });
            Console.WriteLine("Pongo accepted SEP, PTE, KPF, and bootux sequence.");
            return 0;
        }
        return Fail("Unsupported virtual darksword-pongo operation.");
    }

    private static int RunUsbIpd(string statePath, string[] args)
    {
        var state = VirtualDeviceStore.Load(statePath);
        if (!args.Contains("attach", StringComparer.OrdinalIgnoreCase) || !args.Contains("--wsl", StringComparer.OrdinalIgnoreCase))
            return Fail("Virtual usbipd expects attach --wsl.");
        if (state.Mode != VirtualDeviceMode.Pongo)
            return Fail("Only the PongoOS 05AC:4141 device may be attached to WSL.");
        VirtualDeviceStore.Mutate(statePath, document =>
        {
            document.WslAttached = true;
            document.AddEvent("usbipd.exe", "attach-wsl", "05AC:4141 handed from Windows to WSL.");
        });
        Console.WriteLine("PongoOS attached to WSL.");
        return 0;
    }

    private static int RunWsl(string statePath, string[] args)
    {
        var state = VirtualDeviceStore.Load(statePath);
        if (state.Mode != VirtualDeviceMode.Pongo || !state.WslAttached)
            return Fail("palera1n WSL continuation requires an attached PongoOS device.");
        if (!args.Any(argument => argument.Contains("palera1n", StringComparison.OrdinalIgnoreCase)) ||
            !args.Any(argument => argument.Contains("rootless", StringComparison.OrdinalIgnoreCase)))
            return Fail("Virtual WSL continuation requires palera1n rootless arguments.");
        VirtualDeviceStore.Mutate(statePath, document =>
        {
            document.JailbreakInstalled = true;
            document.Mode = VirtualDeviceMode.Jailbroken;
            document.AddEvent("wsl.exe", "palera1n-rootless", "Pongo continuation installed the virtual rootless jailbreak.");
        });
        Console.WriteLine("Virtual palera1n rootless continuation complete.");
        return 0;
    }

    private static string? ValidateVerifiedPwnedDfu(VirtualDeviceDocument state)
    {
        if (state.Mode != VirtualDeviceMode.PwnedDfu)
            return $"turdus requires pwned DFU; current virtual mode is {state.Mode}.";
        if (!string.Equals(state.PwnedMarker, "PWND:[yolo]", StringComparison.Ordinal))
            return $"turdus requires PWND:[yolo]; current marker is {state.PwnedMarker ?? "none"}.";
        if (state.VerifiedPwnGeneration != state.PwnGeneration || state.PwnGeneration == 0)
            return "The current pwned-DFU generation was not verified by irecovery before the destructive operation.";
        return null;
    }

    private static string ToUsbMode(VirtualDeviceMode mode) => mode switch
    {
        VirtualDeviceMode.CleanDfu or VirtualDeviceMode.PwnedDfu => "DFU",
        VirtualDeviceMode.Pongo => "PongoOS",
        VirtualDeviceMode.AwaitingDfu => "Disconnected",
        VirtualDeviceMode.BootedIos15 or VirtualDeviceMode.Jailbroken or VirtualDeviceMode.Normal => "Normal",
        _ => mode.ToString(),
    };

    private static string? GetArgumentValue(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count - 1; index++)
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }

    private static byte[] CreateArtifactBytes(string value) =>
        System.Text.Encoding.UTF8.GetBytes($"DARKSWORD-VIRTUAL-ARTIFACT\n{value}\n{new string('X', 256)}");

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"VIRTUAL TOOL ERROR: {message}");
        return 20;
    }
}
