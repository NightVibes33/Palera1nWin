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
    DateTimeOffset UpdatedAt);

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
    public const int CurrentSchemaVersion = 1;
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
        var key = BuildKey(productType, ecid);
        var profile = new DarkSwordBootProfile(
            CurrentSchemaVersion,
            key,
            productType,
            NormalizeEcid(ecid),
            session.Ipsw.ProductVersion ?? "unknown",
            session.Ipsw.BuildVersion ?? "unknown",
            session.SessionId,
            session.SessionDirectory,
            pte,
            pteHash,
            sepHash,
            kpfHash,
            now,
            now);

        await SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task SaveAsync(DarkSwordBootProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var path = Path.Combine(RootDirectory, Sanitize(profile.Key) + ".json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(profile with { UpdatedAt = DateTimeOffset.UtcNow }, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<DarkSwordBootProfile?> FindAsync(
        string? productType,
        string? ecid,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return null;
        var normalizedEcid = NormalizeEcid(ecid);
        DarkSwordBootProfile? best = null;

        foreach (var path in Directory.EnumerateFiles(RootDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var profile = await JsonSerializer.DeserializeAsync<DarkSwordBootProfile>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (profile is null) continue;

                var ecidMatch = !string.IsNullOrWhiteSpace(normalizedEcid) &&
                                string.Equals(profile.Ecid, normalizedEcid, StringComparison.OrdinalIgnoreCase);
                var productMatch = !string.IsNullOrWhiteSpace(productType) &&
                                   string.Equals(profile.ProductType, productType, StringComparison.Ordinal);
                if ((ecidMatch || productMatch) && (best is null || profile.UpdatedAt > best.UpdatedAt))
                {
                    best = profile;
                }
            }
            catch
            {
                // Ignore damaged or incompatible profile files and continue scanning.
            }
        }

        return best;
    }

    public async Task<BootProfileValidationResult> ValidateAsync(
        DarkSwordBootProfile profile,
        string? connectedProductType,
        string? connectedEcid,
        string sepRacerPath,
        string kpfPath,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        if (profile.SchemaVersion != CurrentSchemaVersion)
            errors.Add($"Unsupported boot profile schema {profile.SchemaVersion}; expected {CurrentSchemaVersion}.");
        if (!string.IsNullOrWhiteSpace(connectedProductType) &&
            !string.Equals(profile.ProductType, connectedProductType, StringComparison.Ordinal))
            errors.Add($"Profile targets {profile.ProductType}, but the connected device is {connectedProductType}.");

        var normalizedEcid = NormalizeEcid(connectedEcid);
        if (!string.IsNullOrWhiteSpace(normalizedEcid) &&
            !string.IsNullOrWhiteSpace(profile.Ecid) &&
            !string.Equals(profile.Ecid, normalizedEcid, StringComparison.OrdinalIgnoreCase))
            errors.Add("The connected ECID does not match this cold-boot profile.");

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

    public async Task<BootProfileValidationResult> ValidatePteImportAsync(
        string ptePath,
        string? connectedProductType,
        string? connectedEcid,
        string sepRacerPath,
        string kpfPath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(ptePath);
        var metadataPath = fullPath + ".metadata.json";
        if (!File.Exists(metadataPath))
        {
            return new BootProfileValidationResult(
                false,
                null,
                ["The selected PTE has no DarkSword .metadata.json file and cannot be trusted for cold boot."]);
        }

        RestoreArtifactMetadata? metadata;
        try
        {
            await using var stream = File.OpenRead(metadataPath);
            metadata = await JsonSerializer.DeserializeAsync<RestoreArtifactMetadata>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return new BootProfileValidationResult(false, null, [$"PTE metadata could not be read: {exception.Message}"]);
        }

        if (metadata is null || !metadata.ArtifactType.Contains("pte", StringComparison.OrdinalIgnoreCase))
            return new BootProfileValidationResult(false, null, ["The selected file is not a validated PTE artifact."]);
        if (!string.IsNullOrWhiteSpace(connectedProductType) &&
            !string.IsNullOrWhiteSpace(metadata.ProductVersion) &&
            string.IsNullOrWhiteSpace(metadata.BuildVersion))
            return new BootProfileValidationResult(false, null, ["The PTE metadata is incomplete."]);

        var pteHash = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(pteHash, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
            return new BootProfileValidationResult(false, null, ["The PTE file hash no longer matches its metadata."]);

        var now = DateTimeOffset.UtcNow;
        var profile = new DarkSwordBootProfile(
            CurrentSchemaVersion,
            BuildKey(connectedProductType ?? "unknown", connectedEcid),
            connectedProductType ?? "unknown",
            NormalizeEcid(connectedEcid),
            metadata.ProductVersion ?? "unknown",
            metadata.BuildVersion ?? "unknown",
            metadata.SessionId,
            Path.GetDirectoryName(fullPath) ?? string.Empty,
            fullPath,
            pteHash,
            await HashFileAsync(sepRacerPath, cancellationToken).ConfigureAwait(false),
            await HashFileAsync(kpfPath, cancellationToken).ConfigureAwait(false),
            now,
            now);

        return await ValidateAsync(
            profile,
            connectedProductType,
            connectedEcid,
            sepRacerPath,
            kpfPath,
            cancellationToken).ConfigureAwait(false);
    }

    public static string BuildKey(string productType, string? ecid) =>
        !string.IsNullOrWhiteSpace(ecid)
            ? $"ecid-{NormalizeEcid(ecid)}"
            : $"product-{productType}";

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
