using System.Security.Cryptography;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed record DarkSwordBootProfile(
    int SchemaVersion,
    string Key,
    string ProductType,
    string? Ecid,
    string TargetVersion,
    string TargetBuild,
    string SessionId,
    string SessionDirectory,
    string PtePath,
    string PteSha256,
    string SepRacerSha256,
    string KpfSha256,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? IpswSha256 = null,
    string? DisplayName = null,
    string? Cpid = null,
    string? Bdid = null);

public sealed record BootProfileValidationResult(
    bool IsValid,
    DarkSwordBootProfile? Profile,
    IReadOnlyList<string> Errors)
{
    public string Summary => IsValid
        ? $"Validated {Profile!.ProductType} {Profile.TargetVersion} ({Profile.TargetBuild}) cold-boot profile."
        : string.Join(Environment.NewLine, Errors);
}

public sealed class DarkSwordBootProfileStore
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DarkSwordBootProfileStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore",
            "boot-profiles");
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public async Task<DarkSwordBootProfile> CreateAsync(
        RestoreSession session,
        string productType,
        string? ecid,
        string sepRacerPath,
        string kpfPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(session.PteBlockPath))
            throw new InvalidOperationException("The completed session does not contain a PTE path.");
        if (string.IsNullOrWhiteSpace(productType))
            throw new InvalidOperationException("ProductType is required for a cold-boot profile.");
        if (string.IsNullOrWhiteSpace(ecid))
            throw new InvalidOperationException(
                "ECID was not captured. Re-enter DFU and retry profile creation; a ProductType-only profile is not safe for repeated cold boot.");

        var pte = Path.GetFullPath(session.PteBlockPath);
        var sep = Path.GetFullPath(sepRacerPath);
        var kpf = Path.GetFullPath(kpfPath);
        var pteHash = await HashFileAsync(pte, cancellationToken).ConfigureAwait(false);
        var sepHash = await HashFileAsync(sep, cancellationToken).ConfigureAwait(false);
        var kpfHash = await HashFileAsync(kpf, cancellationToken).ConfigureAwait(false);

        await ValidateArtifactMetadataAsync(
            pte,
            session.SessionId,
            session.Ipsw.ProductVersion,
            session.Ipsw.BuildVersion,
            pteHash,
            cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var normalizedEcid = NormalizeEcid(ecid)!;
        var profile = new DarkSwordBootProfile(
            CurrentSchemaVersion,
            BuildKey(productType, normalizedEcid),
            productType,
            normalizedEcid,
            session.Ipsw.ProductVersion ?? "unknown",
            session.Ipsw.BuildVersion ?? "unknown",
            session.SessionId,
            session.SessionDirectory,
            pte,
            pteHash,
            sepHash,
            kpfHash,
            now,
            now,
            session.Ipsw.Sha256,
            DarkSwordDeviceCatalog.Find(productType)?.DisplayName);

        await SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        await WriteProfileAsync(
            Path.Combine(session.SessionDirectory, "boot-profile.json"),
            profile,
            cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task SaveAsync(DarkSwordBootProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var path = Path.Combine(RootDirectory, Sanitize(profile.Key) + ".json");
        await WriteProfileAsync(path, profile with { UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DarkSwordBootProfile?> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<DarkSwordBootProfile>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DarkSwordBootProfile?> FindAsync(
        string? productType,
        string? ecid,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return null;
        var normalizedEcid = NormalizeEcid(ecid);
        DarkSwordBootProfile? productFallback = null;

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

                if (string.IsNullOrWhiteSpace(normalizedEcid) &&
                    !string.IsNullOrWhiteSpace(productType) &&
                    string.Equals(profile.ProductType, productType, StringComparison.Ordinal) &&
                    (productFallback is null || profile.UpdatedAt > productFallback.UpdatedAt))
                {
                    productFallback = profile;
                }
            }
            catch
            {
                // Ignore damaged or incompatible profile files and continue scanning.
            }
        }

        return productFallback;
    }

    public async Task<DarkSwordBootProfile?> FindByPteAsync(
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
                    string.Equals(Path.GetFullPath(profile.PtePath), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
            catch
            {
                // Continue scanning other profiles.
            }
        }
        return null;
    }

    public async Task<BootProfileValidationResult> ValidateAssetsAsync(
        DarkSwordBootProfile profile,
        string sepRacerPath,
        string kpfPath,
        CancellationToken cancellationToken = default)
    {
        var errors = ValidateProfileShape(profile);
        await ValidateHashAsync(profile.PtePath, profile.PteSha256, "PTE", errors, cancellationToken).ConfigureAwait(false);
        await ValidateHashAsync(sepRacerPath, profile.SepRacerSha256, "sep_racer", errors, cancellationToken).ConfigureAwait(false);
        await ValidateHashAsync(kpfPath, profile.KpfSha256, "kpf", errors, cancellationToken).ConfigureAwait(false);

        if (errors.Count == 0)
        {
            try
            {
                await ValidateArtifactMetadataAsync(
                    profile.PtePath,
                    profile.SessionId,
                    profile.TargetVersion,
                    profile.TargetBuild,
                    profile.PteSha256,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
            }
        }

        return new BootProfileValidationResult(errors.Count == 0, errors.Count == 0 ? profile : null, errors);
    }

    public async Task<BootProfileValidationResult> ValidateAsync(
        DarkSwordBootProfile profile,
        string? connectedProductType,
        string? connectedEcid,
        string sepRacerPath,
        string kpfPath,
        CancellationToken cancellationToken = default)
    {
        var assetResult = await ValidateAssetsAsync(
            profile,
            sepRacerPath,
            kpfPath,
            cancellationToken).ConfigureAwait(false);
        var errors = assetResult.Errors.ToList();

        if (string.IsNullOrWhiteSpace(connectedProductType))
        {
            errors.Add("ProductType could not be read from the connected DFU device.");
        }
        else if (!string.Equals(profile.ProductType, connectedProductType, StringComparison.Ordinal))
        {
            errors.Add($"Profile targets {profile.ProductType}, but the connected device is {connectedProductType}.");
        }

        var normalizedEcid = NormalizeEcid(connectedEcid);
        if (string.IsNullOrWhiteSpace(profile.Ecid))
        {
            errors.Add("The saved cold-boot profile is not bound to an ECID.");
        }
        else if (string.IsNullOrWhiteSpace(normalizedEcid))
        {
            errors.Add("ECID could not be read from the connected DFU device.");
        }
        else if (!string.Equals(profile.Ecid, normalizedEcid, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Profile ECID {profile.Ecid} does not match connected ECID {normalizedEcid}.");
        }

        return new BootProfileValidationResult(errors.Count == 0, errors.Count == 0 ? profile : null, errors);
    }

    public Task<BootProfileValidationResult> ValidatePteImportAsync(
        string ptePath,
        string? connectedProductType,
        string? connectedEcid,
        string sepRacerPath,
        string kpfPath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new BootProfileValidationResult(
            false,
            null,
            [
                "Raw PTE import is disabled. Import the boot-profile.json created by the completed DarkSword session so ProductType, ECID, session, PTE, SEP, and KPF hashes can all be verified."
            ]));

    public static string BuildKey(string productType, string? ecid) =>
        !string.IsNullOrWhiteSpace(ecid)
            ? $"ecid-{NormalizeEcid(ecid)}"
            : $"product-{productType}";

    private static List<string> ValidateProfileShape(DarkSwordBootProfile profile)
    {
        var errors = new List<string>();
        if (profile.SchemaVersion != CurrentSchemaVersion)
            errors.Add($"Unsupported boot profile schema {profile.SchemaVersion}; expected {CurrentSchemaVersion}.");
        if (string.IsNullOrWhiteSpace(profile.ProductType)) errors.Add("Boot profile ProductType is missing.");
        if (string.IsNullOrWhiteSpace(profile.Ecid)) errors.Add("Boot profile ECID is missing.");
        if (string.IsNullOrWhiteSpace(profile.SessionId)) errors.Add("Boot profile restore session is missing.");
        if (string.IsNullOrWhiteSpace(profile.SessionDirectory)) errors.Add("Boot profile session directory is missing.");
        if (!profile.TargetVersion.StartsWith("15.", StringComparison.Ordinal))
            errors.Add($"Boot profile target {profile.TargetVersion} is not an iOS/iPadOS 15 downgrade.");
        if (string.IsNullOrWhiteSpace(profile.IpswSha256)) errors.Add("Boot profile IPSW SHA-256 is missing.");
        return errors;
    }

    private static async Task ValidateArtifactMetadataAsync(
        string ptePath,
        string sessionId,
        string? productVersion,
        string? buildVersion,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        var metadataPath = ptePath + ".metadata.json";
        if (!File.Exists(metadataPath))
            throw new InvalidDataException("The PTE metadata file is missing.");

        await using var stream = File.OpenRead(metadataPath);
        var metadata = await JsonSerializer.DeserializeAsync<RestoreArtifactMetadata>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The PTE metadata file is invalid.");

        if (!metadata.ArtifactType.Contains("pte", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The artifact metadata does not identify a PTE block.");
        if (!string.Equals(metadata.SessionId, sessionId, StringComparison.Ordinal))
            throw new InvalidDataException("The PTE metadata belongs to a different restore session.");
        if (!string.Equals(metadata.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The PTE metadata hash does not match the PTE file.");
        if (!string.IsNullOrWhiteSpace(productVersion) &&
            !string.Equals(metadata.ProductVersion, productVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The PTE metadata target version does not match the boot profile.");
        if (!string.IsNullOrWhiteSpace(buildVersion) &&
            !string.Equals(metadata.BuildVersion, buildVersion, StringComparison.Ordinal))
            throw new InvalidDataException("The PTE metadata build does not match the boot profile.");
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
            errors.Add($"{label} file is missing: {path}");
            return;
        }

        var actual = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            errors.Add($"{label} hash changed; the boot profile is no longer valid.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Required boot asset was not found.", path);
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static async Task WriteProfileAsync(
        string path,
        DarkSwordBootProfile profile,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Boot profile path has no directory."));
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(profile, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static string? NormalizeEcid(string? ecid) =>
        string.IsNullOrWhiteSpace(ecid)
            ? null
            : ecid.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) || character is '\\' or '/' ? '_' : character).ToArray());
    }
}
