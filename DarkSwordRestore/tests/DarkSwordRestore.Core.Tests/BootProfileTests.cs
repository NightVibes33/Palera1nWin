using System.Security.Cryptography;
using System.Text.Json;
using DarkSwordRestore.Core;

namespace DarkSwordRestore.Core.Tests;

public sealed class BootProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "darksword-boot-profile-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAndValidate_RequiresMatchingProductTypeAndEcid()
    {
        var fixture = await CreateFixtureAsync();
        var store = new DarkSwordBootProfileStore(Path.Combine(_root, "profiles"));
        var profile = await store.CreateAsync(
            fixture.Session,
            "iPad6,11",
            "0x1234ABCD",
            fixture.SepRacer,
            fixture.Kpf);

        var result = await store.ValidateAsync(
            profile,
            "iPad6,11",
            "1234abcd",
            fixture.SepRacer,
            fixture.Kpf);

        Assert.True(result.IsValid, result.Summary);
        Assert.Equal(DarkSwordBootProfileStore.CurrentSchemaVersion, profile.SchemaVersion);
        Assert.Equal("1234ABCD", profile.Ecid);
        Assert.True(File.Exists(Path.Combine(fixture.Session.SessionDirectory, "boot-profile.json")));
    }

    [Fact]
    public async Task Validate_RejectsWrongEcid()
    {
        var fixture = await CreateFixtureAsync();
        var store = new DarkSwordBootProfileStore(Path.Combine(_root, "profiles"));
        var profile = await store.CreateAsync(
            fixture.Session,
            "iPad6,11",
            "1234ABCD",
            fixture.SepRacer,
            fixture.Kpf);

        var result = await store.ValidateAsync(
            profile,
            "iPad6,11",
            "DEADBEEF",
            fixture.SepRacer,
            fixture.Kpf);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("does not match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAssets_RejectsModifiedPte()
    {
        var fixture = await CreateFixtureAsync();
        var store = new DarkSwordBootProfileStore(Path.Combine(_root, "profiles"));
        var profile = await store.CreateAsync(
            fixture.Session,
            "iPad6,11",
            "1234ABCD",
            fixture.SepRacer,
            fixture.Kpf);

        await File.AppendAllTextAsync(profile.PtePath, "tampered");
        var result = await store.ValidateAssetsAsync(profile, fixture.SepRacer, fixture.Kpf);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("PTE hash changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RawPteImport_IsBlocked()
    {
        var fixture = await CreateFixtureAsync();
        var store = new DarkSwordBootProfileStore(Path.Combine(_root, "profiles"));

        var result = await store.ValidatePteImportAsync(
            fixture.Session.PteBlockPath!,
            "iPad6,11",
            "1234ABCD",
            fixture.SepRacer,
            fixture.Kpf);

        Assert.False(result.IsValid);
        Assert.Contains("boot-profile.json", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(RestoreSession Session, string SepRacer, string Kpf)> CreateFixtureAsync()
    {
        Directory.CreateDirectory(_root);
        var sessionDirectory = Path.Combine(_root, "session");
        Directory.CreateDirectory(sessionDirectory);
        var pte = Path.Combine(sessionDirectory, "pteblock.bin");
        var sep = Path.Combine(_root, "sep_racer.bin");
        var kpf = Path.Combine(_root, "kpf.bin");
        await File.WriteAllBytesAsync(pte, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(sep, [5, 6, 7]);
        await File.WriteAllBytesAsync(kpf, [8, 9, 10]);

        var pteHash = await HashAsync(pte);
        var metadata = new RestoreArtifactMetadata(
            "session-1",
            "pteblock",
            pte,
            new FileInfo(pte).Length,
            pteHash,
            "15.0",
            "19A346",
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            pte + ".metadata.json",
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var ipsw = new IpswInspectionResult(
            true,
            Path.Combine(_root, "iPad.ipsw"),
            "15.0",
            "19A346",
            ["iPad6,11"],
            1,
            new string('a', 64),
            [],
            []);
        var now = DateTimeOffset.UtcNow;
        var session = new RestoreSession(
            "session-1",
            sessionDirectory,
            ipsw.Path,
            ipsw,
            null,
            pte,
            RestoreStage.GeneratingPteBlock,
            now,
            now);
        return (session, sep, kpf);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
