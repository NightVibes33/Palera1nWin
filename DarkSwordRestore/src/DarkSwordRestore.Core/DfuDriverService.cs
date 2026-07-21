namespace DarkSwordRestore.Core;

public sealed class DfuDriverService
{
    private readonly ToolchainLocator _tools;
    private readonly ProcessRunner _runner;
    private readonly SessionLogger _log;

    public DfuDriverService(ToolchainLocator tools, ProcessRunner runner, SessionLogger log)
    {
        _tools = tools;
        _runner = runner;
        _log = log;
    }

    public bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public async Task InstallLibusbKForDfuAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAdministrator())
        {
            throw new DarkSwordException(
                RestoreStage.InstallingDfuDriver,
                "Run DarkSword Restore as Administrator before installing the Apple DFU driver.");
        }

        var destination = Path.Combine(Path.GetDirectoryName(_tools.WdiSimple)!, "darksword-dfu-driver");
        Directory.CreateDirectory(destination);

        var result = await _runner.RunAsync(
            _tools.WdiSimple,
            new[]
            {
                "--vid", "0x05AC",
                "--pid", "0x1227",
                "--type", "1",
                "--name", "Apple Mobile Device (DFU Mode)",
                "--dest", destination,
                "--progressbar=0"
            },
            Path.GetDirectoryName(_tools.WdiSimple),
            timeout: TimeSpan.FromMinutes(3),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new DarkSwordException(
                RestoreStage.InstallingDfuDriver,
                $"DFU driver installation failed with exit code {result.ExitCode}. {result.StandardError.Trim()}");
        }

        _log.Info("Apple DFU mode is assigned to libusbK.");
    }
}
