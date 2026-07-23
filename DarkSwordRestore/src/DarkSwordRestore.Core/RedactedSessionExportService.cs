using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkSwordRestore.Core;

public sealed class RedactedSessionExportService
{
    private const long MaximumLogBytes = 2L * 1024L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly Regex WindowsPath = new(
        "(?i)(?:[a-z]:\\\\|\\\\\\\\)[^\\r\\n\\t\\\"']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EcidLine = new(
        @"(?im)(ECID\s*[=:]\s*)(?:0x)?[0-9a-f]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string ExportDirectory { get; }

    public RedactedSessionExportService(string? exportDirectory = null)
    {
        ExportDirectory = exportDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore",
            "exports");
        Directory.CreateDirectory(ExportDirectory);
    }

    public async Task<string> ExportAsync(
        RestoreSession session,
        string? appLogPath,
        DeviceDowngradeProfile? profile,
        CableHealthSnapshot cableHealth,
        CancellationToken cancellationToken = default)
    {
        var sessionRoot = Path.GetFullPath(session.SessionDirectory);
        if (!Directory.Exists(sessionRoot)) throw new DirectoryNotFoundException(sessionRoot);
        Directory.CreateDirectory(ExportDirectory);

        var safeProduct = session.Ipsw.SupportedProductTypes.FirstOrDefault() ?? "AppleDevice";
        var destination = Path.Combine(
            ExportDirectory,
            $"DarkSword-support-{safeProduct}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        var temporary = destination + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);

        var pseudonym = BuildDevicePseudonym(session, profile);
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                await WriteTextAsync(
                    archive,
                    "PRIVACY.txt",
                    "This is a redacted diagnostic bundle. Raw SHC/PTE payloads, boot-profile.json, IPSW files, ECID, USB instance IDs and absolute local paths are intentionally excluded.\n" +
                    "Review the files before sharing. The device pseudonym is a one-way abbreviated SHA-256 value used only to correlate files in this bundle.\n",
                    cancellationToken);

                var manifest = new
                {
                    schemaVersion = 2,
                    exportedAt = DateTimeOffset.UtcNow,
                    devicePseudonym = pseudonym,
                    session = new
                    {
                        sessionIdHash = ShortHash(session.SessionId),
                        session.LastStage,
                        session.CreatedAt,
                        session.UpdatedAt,
                        productTypes = session.Ipsw.SupportedProductTypes,
                        session.Ipsw.ProductVersion,
                        session.Ipsw.BuildVersion,
                        session.Ipsw.Sha256,
                        session.Ipsw.FileSize,
                        hasPreOrPostShc = !string.IsNullOrWhiteSpace(session.ShcBlockPath),
                        hasPte = !string.IsNullOrWhiteSpace(session.PteBlockPath),
                        exactIdentityBound = session.HasBoundIdentity,
                    },
                    profile = profile is null ? null : new
                    {
                        profile.ProductType,
                        profile.DisplayName,
                        profile.LastVersion,
                        profile.LastBuild,
                        profile.IpswSha256,
                        profile.UpdatedAt,
                        hasPte = !string.IsNullOrWhiteSpace(profile.LastPteBlockPath),
                    },
                    cableHealth,
                };
                await WriteJsonAsync(archive, "manifest.json", manifest, cancellationToken);

                foreach (var name in new[] { "recovery-progress.json", "recovery-artifact-index.json" })
                {
                    var path = Path.Combine(sessionRoot, name);
                    if (!File.Exists(path)) continue;
                    var text = Redact(await File.ReadAllTextAsync(path, cancellationToken), session, profile);
                    await WriteTextAsync(archive, $"session/{name}", text, cancellationToken);
                }

                var metadataSummary = BuildMetadataSummary(session, sessionRoot);
                await WriteJsonAsync(archive, "session/artifact-summary.json", metadataSummary, cancellationToken);

                if (!string.IsNullOrWhiteSpace(appLogPath) && File.Exists(appLogPath))
                {
                    var log = await ReadTailAsync(appLogPath, MaximumLogBytes, cancellationToken);
                    await WriteTextAsync(
                        archive,
                        $"logs/{Path.GetFileName(appLogPath)}.redacted.txt",
                        Redact(log, session, profile),
                        cancellationToken);
                }
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            throw;
        }
    }

    private static IReadOnlyList<object> BuildMetadataSummary(RestoreSession session, string sessionRoot)
    {
        var result = new List<object>();
        foreach (var path in Directory.EnumerateFiles(sessionRoot, "*.metadata.json", SearchOption.AllDirectories))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<RestoreArtifactMetadata>(File.ReadAllText(path), JsonOptions);
                if (metadata is null || !string.Equals(metadata.SessionId, session.SessionId, StringComparison.Ordinal)) continue;
                result.Add(new
                {
                    metadata.ArtifactType,
                    fileName = Path.GetFileName(metadata.Path),
                    metadata.Size,
                    metadata.Sha256,
                    metadata.ProductVersion,
                    metadata.BuildVersion,
                    metadata.CreatedAt,
                });
            }
            catch
            {
                // A damaged metadata entry is omitted rather than copied verbatim.
            }
        }
        return result;
    }

    private static string Redact(string value, RestoreSession session, DeviceDowngradeProfile? profile)
    {
        var result = value;
        foreach (var secret in new[]
                 {
                     session.BoundEcid,
                     profile?.Ecid,
                     profile?.InstanceId,
                     session.SessionDirectory,
                     session.IpswPath,
                     session.ShcBlockPath,
                     session.PteBlockPath,
                     profile?.LastSessionDirectory,
                     profile?.LastIpswPath,
                     profile?.LastPteBlockPath,
                 }.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            result = result.Replace(secret!, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
        }
        result = EcidLine.Replace(result, "$1[REDACTED]");
        result = WindowsPath.Replace(result, "[LOCAL_PATH_REDACTED]");
        return result;
    }

    private static string BuildDevicePseudonym(RestoreSession session, DeviceDowngradeProfile? profile)
    {
        var identity = AppleDeviceSnapshot.NormalizeEcid(session.BoundEcid ?? profile?.Ecid)
                       ?? profile?.ProductType
                       ?? session.Ipsw.SupportedProductTypes.FirstOrDefault()
                       ?? "unknown";
        return ShortHash(identity);
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    private static async Task<string> ReadTailAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var start = Math.Max(0, stream.Length - maximumBytes);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var value = await reader.ReadToEndAsync(cancellationToken);
        return start == 0 ? value : "[Earlier log content omitted]\n" + value;
    }

    private static async Task WriteTextAsync(
        ZipArchive archive,
        string name,
        string text,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await using var writer = new StreamWriter(
            output,
            new UTF8Encoding(false),
            bufferSize: 1024,
            leaveOpen: false);
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }

    private static async Task WriteJsonAsync<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await JsonSerializer.SerializeAsync(output, value, JsonOptions, cancellationToken);
    }
}
