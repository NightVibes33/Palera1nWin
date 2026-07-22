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
    public bool PreferUsbA { get; set; } = true;

    /// <summary>
    /// Mutable application data must not live beside an elevated executable. Keeping
    /// settings, logs and downloads in LocalAppData also makes installed/protected-folder
    /// deployments work consistently.
    /// </summary>
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Palera1nWin");

    public static string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");
    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public static string RuntimeDirectory => Path.Combine(RootDirectory, "runtime");
    public static string LegacySettingsFilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            EnsureDirectories();
            MigrateLegacySettingsIfNeeded();

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
                fallback.Save();
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

    private static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(RuntimeDirectory);
    }

    private static void MigrateLegacySettingsIfNeeded()
    {
        if (File.Exists(SettingsFilePath) || !File.Exists(LegacySettingsFilePath)) return;

        try
        {
            Directory.CreateDirectory(RootDirectory);
            File.Copy(LegacySettingsFilePath, SettingsFilePath, overwrite: false);
        }
        catch
        {
            // A failed migration is non-fatal; clean defaults are created below.
        }
    }

    private static void BackupCorruptSettings(string reason)
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var safeReason = string.Concat(reason.Where(char.IsLetterOrDigit));
            var backup = SettingsFilePath + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}-{safeReason}";
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
        EnsureDirectories();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        var tempPath = SettingsFilePath + ".tmp";
        File.WriteAllText(tempPath, json);

        try
        {
            if (File.Exists(SettingsFilePath))
                File.Replace(tempPath, SettingsFilePath, destinationBackupFileName: null);
            else
                File.Move(tempPath, SettingsFilePath);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(tempPath, SettingsFilePath, overwrite: true);
        }
    }

    public void Clamp()
    {
        ToolchainRoot = (ToolchainRoot ?? string.Empty).Trim().TrimEnd('\\', '/');
        WslDistro = string.IsNullOrWhiteSpace(WslDistro) ? "Ubuntu" : WslDistro.Trim();
        SelectedReleaseTag = string.IsNullOrWhiteSpace(SelectedReleaseTag) ? "v2.3" : SelectedReleaseTag.Trim();
        JailbreakMode = NormalizeJailbreakMode(JailbreakMode);
    }

    public static string NormalizeJailbreakMode(string? mode) =>
        string.Equals(mode, "rootful", StringComparison.OrdinalIgnoreCase) ? "rootful" : "rootless";

    public bool IsRootful => string.Equals(JailbreakMode, "rootful", StringComparison.OrdinalIgnoreCase);
    public bool IsRootless => !IsRootful;

    private static AppSettings CreateDefault() => new()
    {
        ToolchainRoot = ResolveDefaultToolchainRoot(),
    };

    private static string ResolveDefaultToolchainRoot()
    {
        foreach (var candidate in Util.Paths.GetToolchainCandidates())
        {
            if (Directory.Exists(candidate)) return candidate;
        }
        return string.Empty;
    }
}
