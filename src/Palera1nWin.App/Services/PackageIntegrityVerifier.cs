using System.Security.Cryptography;
using System.Text.Json;

namespace Palera1nWin.App.Services;

public sealed record PackageIntegrityFailure(string Path, string Reason);

public sealed record PackageIntegrityReport(
    bool IsPackagedBuild,
    bool IsValid,
    IReadOnlyList<PackageIntegrityFailure> Failures)
{
    public string Summary => IsValid
        ? IsPackagedBuild ? "Critical package files match manifest.json." : "Development build: no package manifest present."
        : string.Join(Environment.NewLine, Failures.Select(failure => $"{failure.Path}: {failure.Reason}"));
}

public static class PackageIntegrityVerifier
{
    private static readonly string[] CriticalRelativePaths =
    [
        "Palera1nWin.exe",
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

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PackageIntegrityReport? _cached;
    private static DateTimeOffset _cachedAt;

    public static async Task<PackageIntegrityReport> VerifyAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _cached is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(30))
                return _cached;

            var root = Path.GetFullPath(AppContext.BaseDirectory);
            var manifestPath = Path.Combine(root, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                _cached = new PackageIntegrityReport(false, true, []);
                _cachedAt = DateTimeOffset.UtcNow;
                return _cached;
            }

            List<ManifestEntry>? entries;
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                entries = await JsonSerializer.DeserializeAsync<List<ManifestEntry>>(
                    stream,
                    ManifestJsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _cached = new PackageIntegrityReport(
                    true,
                    false,
                    [new PackageIntegrityFailure("manifest.json", $"unreadable: {exception.Message}")]);
                _cachedAt = DateTimeOffset.UtcNow;
                return _cached;
            }

            var lookup = (entries ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .GroupBy(entry => Normalize(entry.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var failures = new List<PackageIntegrityFailure>();
            foreach (var relative in CriticalRelativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = Normalize(relative);
                if (!lookup.TryGetValue(normalized, out var entry))
                {
                    failures.Add(new PackageIntegrityFailure(relative, "not recorded in package manifest"));
                    continue;
                }

                var full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(new PackageIntegrityFailure(relative, "resolved outside package root"));
                    continue;
                }
                if (!File.Exists(full))
                {
                    failures.Add(new PackageIntegrityFailure(relative, "file is missing"));
                    continue;
                }

                var file = new FileInfo(full);
                if (file.Length != entry.Size)
                {
                    failures.Add(new PackageIntegrityFailure(relative, $"size changed ({file.Length} != {entry.Size})"));
                    continue;
                }
                await using var input = new FileStream(
                    full,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
                    .ToLowerInvariant();
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    failures.Add(new PackageIntegrityFailure(relative, "SHA-256 does not match manifest"));
            }

            _cached = new PackageIntegrityReport(true, failures.Count == 0, failures);
            _cachedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task EnsureValidAsync(CancellationToken cancellationToken = default)
    {
        var report = await VerifyAsync(force: true, cancellationToken).ConfigureAwait(false);
        if (!report.IsValid)
            throw new InvalidDataException(
                "The extracted package failed its critical-file integrity check. Do not run drivers, jailbreak, restore, or boot operations from this folder. Re-extract a verified release ZIP.\n\n" +
                report.Summary);
    }

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');

    private sealed class ManifestEntry
    {
        public string Path { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public long Size { get; set; }
    }
}
