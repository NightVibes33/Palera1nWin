using System.IO.Compression;
using DarkSwordRestore.Core;

var failures = new List<string>();
var root = Path.Combine(Path.GetTempPath(), "darksword-selftest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

try
{
    var ipswPath = Path.Combine(root, "iPad_5_15.4.1_test.ipsw");
    using (var archive = ZipFile.Open(ipswPath, ZipArchiveMode.Create))
    {
        Write(archive, "BuildManifest.plist", """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>ProductVersion</key><string>15.4.1</string>
              <key>ProductBuildVersion</key><string>19E258</string>
              <key>SupportedProductTypes</key><array><string>iPad6,11</string><string>iPad6,12</string></array>
            </dict></plist>
            """);
        Write(archive, "Restore.plist", "<plist version=\"1.0\"><dict/></plist>");
        Write(archive, "Firmware/dfu/iBSS.ipad.RELEASE.im4p", "ibss");
        Write(archive, "Firmware/dfu/iBEC.ipad.RELEASE.im4p", "ibec");
        Write(archive, "Firmware/all_flash/sep-firmware.j71.RELEASE.im4p", "sep");
        var padding = archive.CreateEntry("058-00000-001.dmg", CompressionLevel.NoCompression);
        using var stream = padding.Open();
        stream.SetLength(500L * 1024L * 1024L);
    }

    var inspection = await new IpswInspector().InspectAsync(ipswPath);
    Check(inspection.IsValid, "Valid iPad 5 IPSW fixture was rejected.");
    Check(inspection.SupportsIpad5, "iPad 5 support was not detected.");
    Check(inspection.ProductVersion == "15.4.1", "ProductVersion was not parsed.");

    var sessionDirectory = Path.Combine(root, "session");
    var session = new RestoreSession("test", sessionDirectory, ipswPath, inspection, "pre.bin", "pte.bin", RestoreStage.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await SessionStore.SaveAsync(session);
    var loaded = await SessionStore.LoadAsync(sessionDirectory);
    Check(loaded?.PteBlockPath == "pte.bin", "Session persistence failed.");

    Check(Enum.IsDefined(RestoreStage.BootingXnu), "Restore stage enum is incomplete.");
}
catch (Exception ex)
{
    failures.Add(ex.ToString());
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine("DarkSword self-tests failed:");
    foreach (var failure in failures) Console.Error.WriteLine(failure);
    return 1;
}

Console.WriteLine("DarkSword self-tests passed.");
return 0;

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

static void Write(ZipArchive archive, string path, string content)
{
    var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(content);
}
