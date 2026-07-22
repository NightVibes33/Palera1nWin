using System.IO.Compression;

namespace DarkSwordRestore.Core.Tests;

public sealed class OperationalExperienceTests
{
    [Fact]
    public void CompatibilityScoreIsReadyForVerifiedA9Target()
    {
        var device = DarkSwordDeviceCatalog.Find("iPad6,11")!;
        var ipsw = ValidIpsw("iPad6,11");
        var snapshot = new AppleDeviceSnapshot(
            AppleDeviceMode.Dfu,
            "iPad6,11",
            device.DisplayName,
            "USB\\VID_05AC&PID_1227",
            "libusbK",
            "USB\\VID_05AC&PID_1227\\TEST",
            "1234",
            DateTimeOffset.UtcNow);
        var preflight = new PreflightReport(
            DateTimeOffset.UtcNow,
            [new PreflightCheckResult("all", "All checks", PreflightCheckState.Passed, "Ready")],
            snapshot,
            ipsw,
            "fingerprint");

        var result = new CompatibilityAssessmentService().Assess(device, ipsw, preflight, true, true);

        Assert.True(result.IsSupported);
        Assert.Equal("READY", result.Rating);
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void CompatibilityScoreRejectsA10RestoreBackend()
    {
        var device = DarkSwordDeviceCatalog.Find("iPhone9,1")!;
        var result = new CompatibilityAssessmentService().Assess(device, ValidIpsw("iPhone9,1"), null, true, true);

        Assert.False(result.IsSupported);
        Assert.Equal("UNSUPPORTED", result.Rating);
        Assert.Contains(result.Reasons, reason => reason.Contains("not enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StoragePlannerIncludesWorkingSpaceAndSafetyMargin()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var ipsw = Path.Combine(root, "firmware.ipsw");
            File.WriteAllBytes(ipsw, new byte[1024 * 1024]);

            var plan = DowngradeStoragePlanner.Calculate(ipsw, root);

            Assert.True(plan.RequiredBytes > plan.IpswBytes);
            Assert.True(plan.ExtractionBytes > 0);
            Assert.True(plan.RestoreCacheBytes > 0);
            Assert.True(plan.SafetyMarginBytes > 0);
            Assert.False(string.IsNullOrWhiteSpace(plan.Drive));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CableTrackerFlagsRepeatedDisconnects()
    {
        var tracker = new CableStabilityTracker();
        var connected = Snapshot(AppleDeviceMode.Dfu, "USB-A");
        var disconnected = AppleDeviceSnapshot.Disconnected;

        tracker.Observe(connected);
        tracker.Observe(disconnected);
        tracker.Observe(connected);
        tracker.Observe(disconnected);
        tracker.Observe(connected);
        tracker.Observe(disconnected);

        var health = tracker.GetSnapshot();
        Assert.False(health.IsHealthy);
        Assert.Equal(3, health.Disconnects);
        Assert.Contains(health.Rating, new[] { "UNSTABLE", "CRITICAL" });
    }

    [Fact]
    public void FailureTranslatorExplainsCancelledUac()
    {
        var guidance = DowngradeFailureTranslator.Translate(
            "The operation was canceled by the user. Native error 1223.",
            RestoreStage.InstallingDfuDriver);

        Assert.Equal("WIN-UAC-1223", guidance.DiagnosticCode);
        Assert.Contains("administrator", guidance.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(guidance.Actions, action => action.Contains("UAC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RecoveryAdvisorDistinguishesRecoveryFromDfu()
    {
        var advice = ModeRecoveryAdvisor.GetAdvice(Snapshot(AppleDeviceMode.Recovery, "USB-R"), RestoreStage.WaitingForDfu);

        Assert.Contains("not DFU", advice.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("black", advice.Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionExporterCreatesPortableZipWithoutRestoreCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var sessionDirectory = Path.Combine(root, "session");
            var blockDirectory = Path.Combine(sessionDirectory, "block");
            var cacheDirectory = Path.Combine(sessionDirectory, "cache");
            Directory.CreateDirectory(blockDirectory);
            Directory.CreateDirectory(cacheDirectory);
            var sessionJson = Path.Combine(sessionDirectory, "session.json");
            var pte = Path.Combine(blockDirectory, "device-pteblock.bin");
            File.WriteAllText(sessionJson, "{}");
            File.WriteAllBytes(pte, [1, 2, 3, 4]);
            File.WriteAllBytes(Path.Combine(cacheDirectory, "large-cache.bin"), [9, 9, 9]);
            var log = Path.Combine(root, "darksword.log");
            File.WriteAllText(log, "test log");
            var ipswPath = Path.Combine(root, "firmware.ipsw");
            File.WriteAllText(ipswPath, "placeholder");
            var session = new RestoreSession(
                "test-session",
                sessionDirectory,
                ipswPath,
                ValidIpsw("iPad6,11", ipswPath),
                null,
                pte,
                RestoreStage.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            var exporter = new SessionExportService(Path.Combine(root, "exports"));

            var zipPath = await exporter.ExportAsync(
                session,
                log,
                null,
                new CableHealthSnapshot("EXCELLENT", 0, 0, 0, DateTimeOffset.UtcNow, "Stable"));

            Assert.True(File.Exists(zipPath));
            using var archive = ZipFile.OpenRead(zipPath);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("manifest.json", names);
            Assert.Contains("session/session.json", names);
            Assert.Contains("session/block/device-pteblock.bin", names);
            Assert.Contains("logs/darksword.log", names);
            Assert.DoesNotContain(names, name => name.Contains("cache", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static IpswInspectionResult ValidIpsw(string productType, string path = "firmware.ipsw") =>
        new(
            true,
            path,
            "15.8.4",
            "19H390",
            [productType],
            6L * 1024 * 1024 * 1024,
            new string('a', 64),
            [],
            []);

    private static AppleDeviceSnapshot Snapshot(AppleDeviceMode mode, string instanceId) =>
        new(
            mode,
            null,
            "Apple Mobile Device",
            "USB\\VID_05AC",
            mode == AppleDeviceMode.Dfu ? "libusbK" : "Apple Mobile Device USB Driver",
            instanceId,
            null,
            DateTimeOffset.UtcNow);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DarkSwordTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
