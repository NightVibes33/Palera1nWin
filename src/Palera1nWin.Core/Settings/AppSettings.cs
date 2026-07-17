using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palera1nWin.Core.Settings;

public sealed class AppSettings
{
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
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null)
            {
                BackupCorruptSettings("deserialized-null");
                var fallback = CreateDefault();
                fallback.Clamp();
                return fallback;
            }

            settings.Clamp();
            return settings;
        }
        catch (Exception ex)
        {
            BackupCorruptSettings(ex.GetType().Name);
            var fallback = CreateDefault();
            fallback.Clamp();
            return fallback;
        }
    }

    /// <summary>
    /// Rename a corrupt/unreadable settings.json to settings.json.bad-YYYYMMDD-HHmmss
    /// so the user does not lose their config and the next Save can write a clean file.
    /// </summary>
    private static void BackupCorruptSettings(string reason)
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return;
            }

            var backup = SettingsFilePath + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(SettingsFilePath, backup, overwrite: true);
        }
        catch
        {
            // Best effort — do not crash during settings load.
        }
    }

    public void Save()
    {
        Clamp();
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
        var json = JsonSerializer.Serialize(this, JsonOptions);

        // Atomic write: write to a temp file, then rename. Prevents a truncated
        // settings.json if the process crashes (or power fails) mid-write.
        var tempPath = SettingsFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        if (File.Exists(SettingsFilePath))
        {
            File.Replace(tempPath, SettingsFilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, SettingsFilePath);
        }
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
