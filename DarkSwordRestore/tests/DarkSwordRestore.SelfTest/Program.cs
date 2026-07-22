using System.IO.Compression;
using DarkSwordRestore.Core;

if (args.Length > 0 && string.Equals(args[0], "--live-device", StringComparison.OrdinalIgnoreCase))
{
    var ipswPath = args.Length > 1 ? args[1] : null;
    await using var monitor = new AppleDeviceMonitor();
    var snapshot = await monitor.ProbeAsync();
    Console.WriteLine($"Mode={snapshot.Mode}");
    Console.WriteLine($"ProductType={snapshot.ProductType ?? "unresolved"}");
    Console.WriteLine($"Service={snapshot.Service ?? "unknown"}");
    Console.WriteLine($"ECID={snapshot.Ecid ?? "unknown"}");
    Console.WriteLine($"InstanceId={snapshot.InstanceId ?? "unknown"}");

    if (snapshot.Mode == AppleDeviceMode.Disconnected || string.IsNullOrWhiteSpace(snapshot.ProductType))
    {
        Console.Error.WriteLine("Live Apple device identity was not resolved.");
        return 2;
    }

    if (snapshot.Mode is AppleDeviceMode.Dfu or AppleDeviceMode.Pongo &&
        !DfuDriverService.IsAccessibleUsbBackend(snapshot.Service))
    {
        Console.Error.WriteLine("Live DFU/Pongo USB backend is not ready.");
        return 3;
    }

    if (!string.IsNullOrWhiteSpace(ipswPath))
    {
        var inspection = await new IpswInspector().InspectAsync(ipswPath);
        Console.WriteLine($"IpswValid={inspection.IsValid}");
        Console.WriteLine($"IpswVersion={inspection.ProductVersion ?? "unknown"}");
        Console.WriteLine($"IpswBuild={inspection.BuildVersion ?? "unknown"}");
        Console.WriteLine($"IpswTargets={string.Join(",", inspection.SupportedProductTypes)}");
        Console.WriteLine($"IpswMatchesDevice={inspection.MatchesProductType(snapshot.ProductType)}");
        Console.WriteLine($"IpswSha256={inspection.Sha256}");
        if (!inspection.IsValid || !inspection.MatchesProductType(snapshot.ProductType)) return 4;
    }

    return 0;
}

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
        Write(archive, "058-00000-001.dmg", new string('0', 4096));
    }

    var inspection = await new IpswInspector().InspectAsync(ipswPath);
    Check(inspection.IsValid, "Valid iPad 5 IPSW fixture was rejected.");
    Check(inspection.SupportsIpad5, "iPad 5 support was not detected.");
    Check(inspection.ProductVersion == "15.4.1", "ProductVersion was not parsed.");
    Check(inspection.Warnings.Any(x => x.Contains("unusually small", StringComparison.OrdinalIgnoreCase)), "Small fixture warning was not emitted.");

    var invalidPath = Path.Combine(root, "wrong-device.ipsw");
    using (var archive = ZipFile.Open(invalidPath, ZipArchiveMode.Create))
    {
        Write(archive, "BuildManifest.plist", """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>ProductVersion</key><string>15.4.1</string>
              <key>SupportedProductTypes</key><array><string>iPhone8,1</string></array>
            </dict></plist>
            """);
        Write(archive, "Restore.plist", "<plist version=\"1.0\"><dict/></plist>");
        Write(archive, "Firmware/dfu/iBSS.phone.RELEASE.im4p", "ibss");
        Write(archive, "Firmware/dfu/iBEC.phone.RELEASE.im4p", "ibec");
        Write(archive, "Firmware/all_flash/sep-firmware.phone.RELEASE.im4p", "sep");
    }
    var invalid = await new IpswInspector().InspectAsync(invalidPath);
    Check(invalid.IsValid, "Supported iPhone fixture was not parsed as a valid IPSW.");
    Check(!invalid.MatchesProductType("iPad6,11"), "Wrong-device firmware matched iPad6,11.");

    var sessionDirectory = Path.Combine(root, "session");
    var session = new RestoreSession("test", sessionDirectory, ipswPath, inspection, "post-shc.bin", "pte.bin", RestoreStage.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var sessionStore = new RestoreSessionStore(root);
    await sessionStore.SaveAsync(session, CancellationToken.None);
    var loaded = await sessionStore.LoadAsync(sessionDirectory, CancellationToken.None);
    Check(loaded?.PteBlockPath == "pte.bin", "Session persistence failed.");
    Check(loaded?.LastStage == RestoreStage.Completed, "Session stage persistence failed.");

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
