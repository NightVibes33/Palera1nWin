using System.Text.Json.Serialization;

namespace DarkSwordRestore.Core;

public enum AppleDeviceMode
{
    Disconnected,
    Normal,
    Recovery,
    Dfu,
    PwnedDfu,
    Pongo,
    Restore,
    Unknown
}

public enum RestoreStage
{
    Idle,
    Preflight,
    WaitingForDfu,
    InstallingDfuDriver,
    EnteringPwnedDfu,
    GeneratingShcBlock,
    RestoringFirmware,
    GeneratingPteBlock,
    BootingPongo,
    LoadingSepExploit,
    LoadingKernelPatchfinder,
    BootingXnu,
    Completed,
    Failed,
    Cancelled
}

public sealed record AppleDeviceSnapshot(
    AppleDeviceMode Mode,
    string? ProductType,
    string? DisplayName,
    string? HardwareId,
    string? Service,
    string? InstanceId,
    string? Ecid,
    DateTimeOffset ObservedAt)
{
    public static AppleDeviceSnapshot Disconnected { get; } =
        new(AppleDeviceMode.Disconnected, null, null, null, null, null, null, DateTimeOffset.UtcNow);

    [JsonIgnore]
    public bool HasExactIdentity => !string.IsNullOrWhiteSpace(ProductType) && !string.IsNullOrWhiteSpace(Ecid);

    public string? NormalizedEcid => NormalizeEcid(Ecid);

    public bool MatchesIdentity(string? productType, string? ecid) =>
        !string.IsNullOrWhiteSpace(productType) &&
        !string.IsNullOrWhiteSpace(ecid) &&
        string.Equals(ProductType, productType, StringComparison.Ordinal) &&
        string.Equals(NormalizedEcid, NormalizeEcid(ecid), StringComparison.OrdinalIgnoreCase);

    public static string? NormalizeEcid(string? ecid) =>
        string.IsNullOrWhiteSpace(ecid)
            ? null
            : ecid.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
}

public sealed record IpswInspectionResult(
    bool IsValid,
    string Path,
    string? ProductVersion,
    string? BuildVersion,
    IReadOnlyList<string> SupportedProductTypes,
    long FileSize,
    string Sha256,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    [JsonIgnore]
    public bool SupportsDarkSword => SupportedProductTypes.Any(DarkSwordDeviceCatalog.IsSupported);

    [JsonIgnore]
    public bool SupportsWindowsA9Restore => SupportedProductTypes
        .Select(DarkSwordDeviceCatalog.Find)
        .Any(device => device?.UsesA9SepBlocks == true);

    [JsonIgnore]
    public bool SupportsIpad5 => SupportsWindowsA9Restore;

    public bool MatchesProductType(string? productType) =>
        productType is not null && SupportedProductTypes.Contains(productType, StringComparer.Ordinal);
}

public sealed record RestoreProgress(
    RestoreStage Stage,
    double Percent,
    string Title,
    string Detail,
    bool IsDestructive = false,
    int Attempt = 1);

public sealed record ToolResult(
    string FileName,
    string Arguments,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Success => ExitCode == 0;
}

public sealed record RestoreSession(
    string SessionId,
    string SessionDirectory,
    string IpswPath,
    IpswInspectionResult Ipsw,
    string? ShcBlockPath,
    string? PteBlockPath,
    RestoreStage LastStage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? BoundProductType = null,
    string? BoundEcid = null)
{
    [JsonIgnore]
    public bool HasBoundIdentity =>
        !string.IsNullOrWhiteSpace(BoundProductType) && !string.IsNullOrWhiteSpace(BoundEcid);

    public bool MatchesBoundIdentity(AppleDeviceSnapshot snapshot) =>
        snapshot.MatchesIdentity(BoundProductType, BoundEcid);
}

public sealed class DarkSwordException : Exception
{
    public RestoreStage Stage { get; }

    public DarkSwordException(RestoreStage stage, string message)
        : base(message) => Stage = stage;

    public DarkSwordException(RestoreStage stage, string message, Exception innerException)
        : base(message, innerException) => Stage = stage;
}
