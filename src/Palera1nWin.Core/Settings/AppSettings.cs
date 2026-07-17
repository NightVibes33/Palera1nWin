using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palera1nWin.Core.Settings;

public sealed class AppSettings
{
    private const string DefaultToolchainCandidate = @"E:\Work\Palera1n-Windows";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToolchainRoot { get; set; } = ResolveDefaultToolchainRoot();

    public string WslDistro { get; set; } = "Ubuntu";

    public string SelectedReleaseTag { get; set; } = "v2.3";

    public string JailbreakMode { get; set; } = "rootless";

    public bool SafeMode { get; set; }

    public bool VerboseBoot { get; set; } = true;

    public bool DebugLogging { get; set; } = true;

    public bool AutoInstallDrivers { get; set; } = true;

    public bool CheckUpdates { get; set; } = true;

    public bool PasscodeAcknowledged { get; set; }

    public bool PreferUsbA { get; set; } = true;

    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Palera1nWin");

    public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public static string RuntimeDirectory => Path.Combine(RootDirectory, "runtime");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                var defaults = CreateDefault();
                defaults.Save();
                return defaults;
            }

            var json = File.ReadAllText(SettingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefault();
            settings.Clamp();
            return settings;
        }
        catch
        {
            var fallback = CreateDefault();
            fallback.Clamp();
            return fallback;
        }
    }

    public void Save()
    {
        Clamp();
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    public void Clamp()
    {
        ToolchainRoot = (ToolchainRoot ?? string.Empty).Trim().TrimEnd('\\', '/');

        if (string.IsNullOrWhiteSpace(WslDistro))
        {
            WslDistro = "Ubuntu";
        }
        else
        {
            WslDistro = WslDistro.Trim();
        }

        if (string.IsNullOrWhiteSpace(SelectedReleaseTag))
        {
            SelectedReleaseTag = "v2.3";
        }
        else
        {
            SelectedReleaseTag = SelectedReleaseTag.Trim();
        }

        JailbreakMode = NormalizeJailbreakMode(JailbreakMode);
    }

    public static string NormalizeJailbreakMode(string? mode)
    {
        if (string.Equals(mode, "rootful", StringComparison.OrdinalIgnoreCase))
        {
            return "rootful";
        }

        return "rootless";
    }

    public bool IsRootful => string.Equals(JailbreakMode, "rootful", StringComparison.OrdinalIgnoreCase);

    public bool IsRootless => !IsRootful;

    private static AppSettings CreateDefault()
    {
        return new AppSettings
        {
            ToolchainRoot = ResolveDefaultToolchainRoot(),
        };
    }

    private static string ResolveDefaultToolchainRoot()
    {
        if (Directory.Exists(DefaultToolchainCandidate))
        {
            return DefaultToolchainCandidate;
        }

        foreach (var candidate in Util.Paths.GetToolchainCandidates())
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}
