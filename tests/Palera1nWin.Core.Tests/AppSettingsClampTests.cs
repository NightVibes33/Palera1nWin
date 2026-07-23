using Palera1nWin.Core.Settings;

namespace Palera1nWin.Core.Tests;

public sealed class AppSettingsClampTests
{
    [Fact]
    public void Clamp_NormalizesJailbreakMode()
    {
        var settings = new AppSettings
        {
            JailbreakMode = "ROOTFUL",
            WslDistro = "  Ubuntu  ",
            SelectedReleaseTag = "  v2.3  ",
            ToolchainRoot = @"C:\Palera1n-Windows\",
        };

        settings.Clamp();

        Assert.Equal("rootful", settings.JailbreakMode);
        Assert.Equal("Ubuntu", settings.WslDistro);
        Assert.Equal("v2.3", settings.SelectedReleaseTag);
        Assert.Equal(@"C:\Palera1n-Windows", settings.ToolchainRoot);
    }

    [Fact]
    public void Clamp_DefaultsInvalidJailbreakModeToRootless()
    {
        var settings = new AppSettings { JailbreakMode = "invalid-mode" };
        settings.Clamp();
        Assert.Equal("rootless", settings.JailbreakMode);
    }

    [Fact]
    public void NormalizeJailbreakMode_IsCaseInsensitive()
    {
        Assert.Equal("rootful", AppSettings.NormalizeJailbreakMode("RootFul"));
        Assert.Equal("rootless", AppSettings.NormalizeJailbreakMode(null));
    }

    [Fact]
    public void Directories_AreWritableUnderLocalAppData()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, AppSettings.RootDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("logs", AppSettings.LogsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("runtime", AppSettings.RuntimeDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppSettings.RootDirectory, AppSettings.LogsDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(AppSettings.RootDirectory, AppSettings.RuntimeDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("settings.json", AppSettings.SettingsFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(AppContext.BaseDirectory, AppSettings.RootDirectory);
    }
}
