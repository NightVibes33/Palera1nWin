using System.Security.Cryptography;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed record TetherBootProfile(
    int SchemaVersion,
    string Key,
    string ProductType,
    string DisplayName,
    string Ecid,
    string? Cpid,
    string? Bdid,
    string SessionId,
    string TargetVersion,
    string? TargetBuild,
    string IpswSha256,
    string PteBlockPath,
    string PteSha256,
    string SepRacerSha256,
    string KpfSha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 2;
}

public sealed record TetherBootProfileValidation(
    bool IsValid,
    IReadOnlyList<string> Errors)
{
    public string Message => IsValid
        ? "Exact-device tether boot profile verified."
        : string.Join(Environment.NewLine, Errors);
}

public sealed class TetherBootProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string RootDirectory { get; }

    public TetherBootProfileStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore",
            "boot-profiles");
        Directory.CreateDirectory(RootDirectory);
    }

    public async Task<TetherBootProfile> CreateAsync(
        RestoreSession session,
        string productType,
        string displayName,
        string? ecid,
        ToolchainPaths tools,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(productType))
        {
            throw new DarkSwordException(RestoreStage.GeneratingPteBlock, "Cannot create a boot profile without ProductType.");
        }
        if (string.IsNullOrWhiteSpace(ecid))
        {
            throw new DarkSwordException(
                RestoreStage.GeneratingPteBlock,
                "Cannot create an exact-device boot profile because ECID was not captured. Re-enter DFU and retry the profile stage.");
        }
        if (session.PteBlockPath is not { Length: > 0 } ptePath || !File.Exists(ptePath))
        {
            throw new DarkSwordException(RestoreStage.GeneratingPteBlock, "Cannot create a boot profile because the PTE block is missing.");
        }

        var resources = ResolveResources(tools);
        var now = DateTimeOffset.UtcNow;
        return new TetherBootProfile(
            TetherBootProfile.CurrentSchemaVersion,
            BuildKey(productType, ecid),
            productType,
            displayName,
            NormalizeEcid(ecid),
            null,
            null,
            session.SessionId,
            session.Ipsw.ProductVersion ?? "unknown",
            session.Ipsw.BuildVersion,
            session.Ipsw.Sha256,
            Path.GetFullPath(ptePath),
            await HashFileAsync(ptePath, cancellationToken).ConfigureAwait(false),
            await HashFileAsync(resources.SepRacer, cancellationToken).ConfigureAwait(false),
            await HashFileAsync(resources.Kpf, cancellationToken).ConfigureAwait(false),
            now,
            now);
    }

    public async Task<string> SaveAsync(
        TetherBootProfile profile,
        string? sessionDirectory = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var profilePath = Path.Combine(RootDirectory, Sanitize(profile.Key) + ".json");
        await WriteAtomicAsync(profilePath, profile, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(sessionDirectory))
        {
            Directory.CreateDirectory(sessionDirectory);
            await WriteAtomicAsync(
                Path.Combine(sessionDirectory, "boot-profile.json"),
                profile,
                cancellationToken).ConfigureAwait(false);
        }

        return profilePath;
    }

    public async Task<TetherBootProfile?> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<TetherBootProfile>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TetherBootProfile?> FindAsync(
        string? productType,
        string? ecid,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return null;
        var normalizedEcid = NormalizeEcid(ecid);
        TetherBootProfile? productFallback = null;

        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var profile = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
                if (profile is null) continue;

                if (!string.IsNullOrWhiteSpace(normalizedEcid) &&
                    string.Equals(profile.Ecid, normalizedEcid, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }

                if (!string.IsNullOrWhiteSpace(productType) &&
                    string.Equals(profile.ProductType, productType, StringComparison.Ordinal) &&
                    (productFallback is null || profile.UpdatedAt > productFallback.UpdatedAt))
                {
                    productFallback = profile;
                }
            }
            catch
            {
                // Ignore damaged or outdated profiles and continue searching.
            }
        }

        return productFallback;
    }

    public async Task<TetherBootProfile?> FindByPteAsync(
        string? ptePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ptePath) || !Directory.Exists(RootDirectory)) return null;
        var fullPath = Path.GetFullPath(ptePath);
        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var profile = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
                if (profile is not null &&
                    string.Equals(Path.GetFullPath(profile.PteBlockPath), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
            catch
            {
                // Continue searching other profiles.
            }
        }
        return null;
    }

    public async Task<TetherBootProfileValidation> ValidateAsync(
        TetherBootProfile profile,
        AppleDeviceSnapshot device,
        ToolchainPaths tools,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (profile.SchemaVersion != TetherBootProfile.CurrentSchemaVersion)
        {
            errors.Add($"Boot profile schema {profile.SchemaVersion} is unsupported; expected {TetherBootProfile.CurrentSchemaVersion}.");
        }
        if (device.Mode is not AppleDeviceMode.Dfu and not AppleDeviceMode.PwnedDfu)
        {
            errors.Add($"Exact boot-profile validation requires DFU, but the current mode is {device.Mode}.");
        }
        if (string.IsNullOrWhiteSpace(device.ProductType))
        {
            errors.Add("ProductType could not be read from the connected DFU device.");
        }
        else if (!string.Equals(profile.ProductType, device.ProductType, StringComparison.Ordinal))
        {
            errors.Add($"Profile targets {profile.ProductType}, but the connected device is {device.ProductType}.");
        }

        var connectedEcid = NormalizeEcid(device.Ecid);
        if (string.IsNullOrWhiteSpace(profile.Ecid))
        {
            errors.Add("The saved boot profile is not bound to an ECID.");
        }
        else if (string.IsNullOrWhiteSpace(connectedEcid))
        {
            errors.Add("ECID could not be read from the connected DFU device.");
        }
        else if (!string.Equals(profile.Ecid, connectedEcid, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Profile ECID {profile.Ecid} does not match connected ECID {connectedEcid}.");
        }

        if (string.IsNullOrWhiteSpace(profile.SessionId)) errors.Add("Boot profile has no originating restore session.");
        if (!profile.TargetVersion.StartsWith("15.", StringComparison.Ordinal))
            errors.Add($"Boot profile target {profile.TargetVersion} is not an iOS/iPadOS 15 downgrade.");

        await ValidateHashAsync(profile.PteBlockPath, profile.PteSha256, "PTE block", errors, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var resources = ResolveResources(tools);
            await ValidateHashAsync(resources.SepRacer, profile.SepRacerSha256, "sep_racer.bin", errors, cancellationToken)
                .ConfigureAwait(false);
            await ValidateHashAsync(resources.Kpf, profile.KpfSha256, "kpf.bin", errors, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception.Message);
        }

        return new TetherBootProfileValidation(errors.Count == 0, errors);
    }

    public static string BuildKey(string productType, string ecid) =>
        $"{productType}-{NormalizeEcid(ecid)}";

    private static (string SepRacer, string Kpf) ResolveResources(ToolchainPaths tools)
    {
        var root = Path.Combine(tools.Root, "resources");
        var sepRacer = Path.Combine(root, "sep_racer.bin");
        var kpf = Path.Combine(root, "kpf.bin");
        if (!File.Exists(sepRacer) || !File.Exists(kpf))
        {
            throw new FileNotFoundException("The packaged tether-boot resources sep_racer.bin and kpf.bin are required.");
        }
        return (sepRacer, kpf);
    }

    private static async Task ValidateHashAsync(
        string path,
        string expectedHash,
        string label,
        ICollection<string> errors,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            errors.Add($"{label} is missing: {path}");
            return;
        }
        var actual = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{label} hash changed. Expected {expectedHash}, found {actual}.");
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static async Task WriteAtomicAsync(
        string path,
        TetherBootProfile profile,
        CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(profile, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static string NormalizeEcid(string? value) =>
        (value ?? string.Empty)
        .Trim()
        .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
        .ToUpperInvariant();

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) || character is '\\' or '/' ? '_' : character).ToArray());
    }
}
