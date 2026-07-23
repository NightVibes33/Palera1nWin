using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed record HardwareValidationRecord(
    int SchemaVersion,
    string ProductType,
    string Ecid,
    DateTimeOffset ValidatedAt,
    string Validation,
    string? ToolchainFingerprint = null)
{
    public bool IsCurrent(TimeSpan? maximumAge = null) =>
        SchemaVersion == HardwareValidationStore.CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(ProductType) &&
        !string.IsNullOrWhiteSpace(Ecid) &&
        DateTimeOffset.UtcNow - ValidatedAt <= (maximumAge ?? TimeSpan.FromDays(7));
}

public sealed class HardwareValidationStore
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public HardwareValidationStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore", "hardware", "pongo-validation-identity.json");
    }

    public string Path { get; }

    public async Task SaveAsync(AppleDeviceSnapshot identity, CancellationToken cancellationToken = default)
    {
        if (!identity.HasExactIdentity)
            throw new InvalidOperationException("ProductType and ECID are required for a hardware validation record.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var record = new HardwareValidationRecord(
            CurrentSchemaVersion,
            identity.ProductType!,
            identity.NormalizedEcid!,
            DateTimeOffset.UtcNow,
            "DFU -> checkm8 -> PongoOS -> driver -> bridge probe");
        var temporary = Path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(record, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, Path, overwrite: true);
    }

    public async Task<HardwareValidationRecord?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(Path)) return null;
            await using var stream = File.OpenRead(Path);
            var record = await JsonSerializer.DeserializeAsync<HardwareValidationRecord>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return record?.IsCurrent() == true ? record : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public async Task RequireMatchAsync(AppleDeviceSnapshot identity, CancellationToken cancellationToken = default)
    {
        var record = await LoadAsync(cancellationToken).ConfigureAwait(false)
                     ?? throw new DarkSwordException(RestoreStage.Preflight,
                         "The ECID-bound DFU -> PongoOS hardware validation is missing or older than seven days. Run the non-destructive test again.");
        if (!identity.MatchesIdentity(record.ProductType, record.Ecid))
            throw new DarkSwordException(RestoreStage.Preflight,
                $"Hardware validation belongs to {record.ProductType} ECID {record.Ecid}, not connected {identity.ProductType} ECID {identity.NormalizedEcid}.");
    }
}
