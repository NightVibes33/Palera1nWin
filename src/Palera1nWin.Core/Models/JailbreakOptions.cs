namespace Palera1nWin.Core.Models;

public sealed class JailbreakOptions
{
    public bool Rootless { get; init; } = true;

    public bool SafeMode { get; init; }

    public bool VerboseBoot { get; init; } = true;

    public bool DebugLogging { get; init; } = true;

    public string? WslDistro { get; init; }

    public string? ForceBusId { get; init; }

    public bool SkipUsbAttach { get; init; }

    public bool KeepShared { get; init; }

    public static JailbreakOptions FromSettings(Settings.AppSettings settings)
    {
        return new JailbreakOptions
        {
            Rootless = settings.IsRootless,
            SafeMode = settings.SafeMode,
            VerboseBoot = settings.VerboseBoot,
            DebugLogging = settings.DebugLogging,
            WslDistro = settings.WslDistro,
        };
    }

    public IEnumerable<string> BuildPalera1nArguments()
    {
        if (Rootless)
        {
            yield return "-l";
        }
        else
        {
            yield return "-f";
        }

        if (DebugLogging)
        {
            yield return "-v";
        }

        if (VerboseBoot)
        {
            yield return "-V";
        }

        if (SafeMode)
        {
            yield return "-s";
        }
    }
}
