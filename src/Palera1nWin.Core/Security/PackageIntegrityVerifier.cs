using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palera1nWin.Core.Security;

public sealed record PackageIntegrityIssue(string Path, string Problem);

public sealed record PackageIntegrityReport(
    bool ManifestPresent,
    bool IsValid,
    int VerifiedFiles,
    IReadOnlyList<PackageIntegrityIssue> Issues)
{
    public string Summary => !ManifestPresent
        ? "Development build: no package manifest is present."
        : IsValid
            ? $"Package integrity verified for {VerifiedFiles} files."
            : string.Join(Environment.NewLine, Issues.Select(issue => $"{issue.Path}: {issue.Problem}"));
}

public sealed class PackageIntegrityVerifier
{
    private static readonly string[] CriticalRelativePaths =
    [
        "Palera1nWin.exe",
        "Palera1nWin.dll",
        "Palera1nWin.Core.dll",
        "DarkSwordRestore.Core.dll",
        "toolchain/openra1n.exe",
        "toolchain/openra1n-core.exe",
        "toolchain/turdus_merula.exe",
        "toolchain/darksword-pongo.exe",
        "toolchain/wdi-simple.exe",
        "toolchain/ideviceinfo.exe",
        "toolchain/irecovery.exe",
        "toolchain/libusb-1.0.dll",
        "toolchain/resources/sep_racer.bin",
        "toolchain/resources/kpf.bin",
        "toolchain/palera1n.cmd",
        "toolchain/windows/palera1n.ps1",
        "toolchain/build/fake-checkra1n.sh",
        "toolchain/build/provision-wsl.sh",
        "toolchain/dist/palera1n-linux-x86_64",
    ];

    private readonly string _packageRoot;

    public PackageIntegrityVerifier(string? packageRoot = null) =>
        _packageRoot = Path.GetFullPath(packageRoot ?? AppContext.BaseDirectory);

    public async Task<PackageIntegrityReport> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var manifestPath = Path.Combine(_packageRoot, "manifest.json");
        var detachedPath = Path.Combine(_packageRoot, "manifest.sha256");
        if (!File.Exists(manifestPath) && !File.Exists(detachedPath))
            return new PackageIntegrityReport(false, true, 0, []);

        var issues = new List<PackageIntegrityIssue>();
        if (!File.Exists(manifestPath)) issues.Add(new("manifest.json", "missing"));
        if (!File.Exists(detachedPath)) issues.Add(new("manifest.sha256", "missing"));
        if (issues.Count > 0) return new PackageIntegrityReport(true, false, 0, issues);

        var expectedManifestHash = ParseDetachedHash(await File.ReadAllTextAsync(detachedPath, cancellationToken)
            .ConfigureAwait(false));
        if (expectedManifestHash is null)
        {
            issues.Add(new("manifest.sha256", "invalid detached checksum format"));
            return new PackageIntegrityReport(true, false, 0, issues);
        }

        var actualManifestHash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!actualManifestHash.Equals(expectedManifestHash, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("manifest.json", "detached SHA-256 mismatch"));
            return new PackageIntegrityReport(true, false, 0, issues);
        }

        IReadOnlyList<ManifestEntry> entries;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            entries = await JsonSerializer.DeserializeAsync<List<ManifestEntry>>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception exception)
        {
            issues.Add(new("manifest.json", $"invalid JSON: {exception.Message}"));
            return new PackageIntegrityReport(true, false, 0, issues);
        }

        if (entries.Count < CriticalRelativePaths.Length)
            issues.Add(new("manifest.json", $"contains only {entries.Count} entries"));

        var byPath = new Dictionary<string, ManifestEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = NormalizeRelativePath(entry.Path);
            if (normalized is null)
            {
                issues.Add(new(entry.Path ?? "(null)", "unsafe or invalid relative path"));
                continue;
            }
            if (!byPath.TryAdd(normalized, entry))
            {
                issues.Add(new(normalized, "duplicate manifest entry"));
                continue;
            }
            if (entry.Size < 0 || !IsSha256(entry.Sha256))
            {
                issues.Add(new(normalized, "invalid size or SHA-256 value"));
                continue;
            }
        }

        foreach (var critical in CriticalRelativePaths)
        {
            if (!byPath.ContainsKey(critical)) issues.Add(new(critical, "critical file is not covered by the manifest"));
        }

        var verified = 0;
        foreach (var pair in byPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolute = Path.GetFullPath(Path.Combine(
                _packageRoot,
                pair.Key.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(_packageRoot, absolute))
            {
                issues.Add(new(pair.Key, "resolved outside package root"));
                continue;
            }
            if (!File.Exists(absolute))
            {
                issues.Add(new(pair.Key, "missing"));
                continue;
            }

            var info = new FileInfo(absolute);
            if (info.Length != pair.Value.Size)
            {
                issues.Add(new(pair.Key, $"size mismatch: expected {pair.Value.Size}, found {info.Length}"));
                continue;
            }
            var actual = await HashFileAsync(absolute, cancellationToken).ConfigureAwait(false);
            if (!actual.Equals(pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(pair.Key, "SHA-256 mismatch"));
                continue;
            }
            verified++;
        }

        return new PackageIntegrityReport(true, issues.Count == 0, verified, issues);
    }

    private static string? ParseDetachedHash(string text)
    {
        var token = text.Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return IsSha256(token) ? token!.ToLowerInvariant() : null;
    }

    private static string? NormalizeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return null;
        var normalized = value.Replace('\\', '/').TrimStart('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or "..")) return null;
        return string.Join('/', parts);
    }

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private sealed record ManifestEntry(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("size")] long Size);
}
