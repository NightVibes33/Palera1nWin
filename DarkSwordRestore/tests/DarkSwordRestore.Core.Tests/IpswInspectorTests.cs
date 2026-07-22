using System.IO.Compression;
using DarkSwordRestore.Core;

namespace DarkSwordRestore.Core.Tests;

public sealed class IpswInspectorTests
{
    [Fact]
    public async Task AcceptsStructuredIpad5Ios15Ipsw()
    {
        var path = Path.Combine(Path.GetTempPath(), $"darksword-{Guid.NewGuid():N}.ipsw");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(archive, "BuildManifest.plist", """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0"><dict>
                      <key>ProductVersion</key><string>15.4.1</string>
                      <key>ProductBuildVersion</key><string>19E258</string>
                      <key>SupportedProductTypes</key><array><string>iPad6,11</string><string>iPad6,12</string></array>
                    </dict></plist>
                    """);
                Write(archive, "Restore.plist", "<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>");
                Write(archive, "Firmware/dfu/iBSS.ipad.RELEASE.im4p", "ibss");
                Write(archive, "Firmware/dfu/iBEC.ipad.RELEASE.im4p", "ibec");
                Write(archive, "Firmware/all_flash/sep-firmware.ipad.RELEASE.im4p", "sep");
            }

            var result = await new IpswInspector().InspectAsync(path);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
            Assert.True(result.SupportsDarkSword);
            Assert.True(result.SupportsWindowsA9Restore);
            Assert.Equal("15.4.1", result.ProductVersion);
            Assert.Equal("19E258", result.BuildVersion);
            Assert.NotEmpty(result.Sha256);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AcceptsA10Ios15IpswForDownloaderAndInspection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"darksword-{Guid.NewGuid():N}.ipsw");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(archive, "BuildManifest.plist", """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0"><dict>
                      <key>ProductVersion</key><string>15.8.4</string>
                      <key>ProductBuildVersion</key><string>19H390</string>
                      <key>SupportedProductTypes</key><array><string>iPhone9,3</string></array>
                    </dict></plist>
                    """);
                Write(archive, "Restore.plist", "restore");
                Write(archive, "Firmware/dfu/iBSS.test", "ibss");
                Write(archive, "Firmware/dfu/iBEC.test", "ibec");
                Write(archive, "Firmware/sep-firmware.test", "sep");
            }

            var result = await new IpswInspector().InspectAsync(path);

            Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
            Assert.True(result.SupportsDarkSword);
            Assert.False(result.SupportsWindowsA9Restore);
            Assert.True(result.MatchesProductType("iPhone9,3"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsA11Firmware()
    {
        var path = Path.Combine(Path.GetTempPath(), $"darksword-{Guid.NewGuid():N}.ipsw");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Write(archive, "BuildManifest.plist", """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <plist version="1.0"><dict>
                      <key>ProductVersion</key><string>15.4.1</string>
                      <key>ProductBuildVersion</key><string>19E258</string>
                      <key>SupportedProductTypes</key><array><string>iPhone10,6</string></array>
                    </dict></plist>
                    """);
                Write(archive, "Restore.plist", "restore");
                Write(archive, "Firmware/dfu/iBSS.test", "ibss");
                Write(archive, "Firmware/dfu/iBEC.test", "ibec");
                Write(archive, "Firmware/sep-firmware.test", "sep");
            }

            var result = await new IpswInspector().InspectAsync(path);

            Assert.False(result.IsValid);
            Assert.Contains(result.Errors, error => error.Contains("A9 through A10X", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
