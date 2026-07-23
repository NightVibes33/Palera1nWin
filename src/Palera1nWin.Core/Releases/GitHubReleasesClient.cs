using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.Core.Releases;

public sealed class Palera1nReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

public sealed class Palera1nRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("published_at")] public DateTimeOffset PublishedAt { get; set; }
    [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
    [JsonPropertyName("assets")] public List<Palera1nReleaseAsset> Assets { get; set; } = new();

    public Palera1nReleaseAsset? PreferredLinuxBinary =>
        Assets.FirstOrDefault(a => a.Name.Equals("palera1n-linux-x86_64", StringComparison.OrdinalIgnoreCase)) ??
        Assets.FirstOrDefault(a => a.Name.Contains("palera1n-linux-x86_64", StringComparison.OrdinalIgnoreCase));
}

public sealed class VerifiedDownloadReceipt : IDisposable
{
    private int _finished;

    internal VerifiedDownloadReceipt(string destinationPath, string? backupPath, string sha256, long size)
    {
        DestinationPath = destinationPath;
        BackupPath = backupPath;
        Sha256 = sha256;
        Size = size;
    }

    public string DestinationPath { get; }
    public string? BackupPath { get; }
    public string Sha256 { get; }
    public long Size { get; }

    public void Commit()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        TryDelete(BackupPath);
    }

    public void Rollback()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        TryDelete(DestinationPath);
        if (!string.IsNullOrWhiteSpace(BackupPath) && File.Exists(BackupPath))
            File.Move(BackupPath, DestinationPath, overwrite: true);
    }

    public void Dispose() => Rollback();

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }
}

public sealed class GitHubReleasesClient : IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/palera1n/palera1n/releases";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cachePath;

    public GitHubReleasesClient(HttpClient? httpClient = null, string? cachePath = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _ownsHttpClient = httpClient is null;
        _cachePath = cachePath ?? Path.Combine(AppSettings.RuntimeDirectory, "releases-cache.json");

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Palera1nWin", "1.0"));
        if (!_httpClient.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/vnd.github+json"))
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<IReadOnlyList<Palera1nRelease>> GetReleasesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryReadCache(out var cached) && cached is not null) return cached;

        using var response = await _httpClient.GetAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync<List<Palera1nRelease>>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
        WriteCache(releases);
        return releases;
    }

    public async Task<VerifiedDownloadReceipt> DownloadReleaseBinaryAsync(
        string tag,
        string destinationPath,
        IProgress<ProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var releases = await GetReleasesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var release = releases.FirstOrDefault(r => string.Equals(r.TagName, tag, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException($"Release tag not found: {tag}");
        var asset = release.PreferredLinuxBinary
                    ?? throw new InvalidOperationException($"Release {tag} has no exact palera1n-linux-x86_64 asset.");
        var expectedHash = await ResolveExpectedSha256Async(release, asset, cancellationToken).ConfigureAwait(false);
        var uri = RequireTrustedDownloadUri(asset.BrowserDownloadUrl);

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppSettings.RuntimeDirectory);
        var temporary = destinationPath + $".download-{Guid.NewGuid():N}";
        var backup = File.Exists(destinationPath) ? destinationPath + ".previous" : null;
        TryDelete(temporary);
        TryDelete(backup);
        progress?.Report(new ProgressEventArgs("download", $"Downloading {asset.Name}...", 0));

        try
        {
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var headerLength = response.Content.Headers.ContentLength;
            if (headerLength is > 0 && asset.Size > 0 && headerLength.Value != asset.Size)
                throw new InvalidDataException($"GitHub asset length changed: API={asset.Size}, response={headerLength.Value}.");

            var expectedLength = asset.Size > 0 ? asset.Size : headerLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            long readTotal = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                readTotal += read;
                if (expectedLength > 0)
                    progress?.Report(new ProgressEventArgs(
                        "download", $"Downloading {asset.Name}...", (int)Math.Clamp(readTotal * 100 / expectedLength, 0, 100)));
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (expectedLength > 0 && readTotal != expectedLength)
                throw new InvalidDataException($"Download ended early: expected {expectedLength} bytes, received {readTotal}.");
            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 verification failed. Expected {expectedHash}, received {actualHash}.");

            if (backup is not null) File.Move(destinationPath, backup, overwrite: true);
            File.Move(temporary, destinationPath, overwrite: true);
            await WriteReceiptAsync(destinationPath, tag, asset.Name, actualHash, readTotal, cancellationToken).ConfigureAwait(false);
            progress?.Report(new ProgressEventArgs("download", $"Verified SHA-256 and staged {destinationPath}", 100));
            return new VerifiedDownloadReceipt(destinationPath, backup, actualHash, readTotal);
        }
        catch
        {
            TryDelete(temporary);
            if (!File.Exists(destinationPath) && backup is not null && File.Exists(backup))
                File.Move(backup, destinationPath, overwrite: true);
            throw;
        }
    }

    private async Task<string> ResolveExpectedSha256Async(
        Palera1nRelease release,
        Palera1nReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        if (TryParseDigest(asset.Digest, out var digest)) return digest;

        var checksumAsset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.Contains("sha256", StringComparison.OrdinalIgnoreCase) ||
            candidate.Name.Contains("checksum", StringComparison.OrdinalIgnoreCase));
        if (checksumAsset is null)
            throw new InvalidDataException(
                $"Release {release.TagName} does not publish a GitHub SHA-256 digest or checksum asset; automatic installation is refused.");

        var checksumUri = RequireTrustedDownloadUri(checksumAsset.BrowserDownloadUrl);
        var text = await _httpClient.GetStringAsync(checksumUri, cancellationToken).ConfigureAwait(false);
        foreach (var line in text.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains(asset.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var first = trimmed.Split(' ', '\t', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first?.Length == 64 && first.All(Uri.IsHexDigit)) return first.ToLowerInvariant();
        }
        throw new InvalidDataException($"Checksum asset did not contain SHA-256 for {asset.Name}.");
    }

    private static bool TryParseDigest(string? value, out string digest)
    {
        digest = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split(':', 2);
        if (parts.Length != 2 || !parts[0].Equals("sha256", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[1].Length != 64 || !parts[1].All(Uri.IsHexDigit)) return false;
        digest = parts[1].ToLowerInvariant();
        return true;
    }

    private static Uri RequireTrustedDownloadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"Invalid release download URL: {value}");
        var trusted = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                      uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                      uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!trusted) throw new InvalidOperationException($"Refusing untrusted release host: {uri.Host}");
        return uri;
    }

    private static async Task WriteReceiptAsync(
        string destinationPath,
        string tag,
        string assetName,
        string sha256,
        long size,
        CancellationToken cancellationToken)
    {
        var receiptPath = destinationPath + ".verified.json";
        var temporary = receiptPath + ".tmp";
        var payload = new { schema = 1, tag, assetName, sha256, size, verifiedAt = DateTimeOffset.UtcNow };
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, receiptPath, overwrite: true);
    }

    public static IReadOnlyList<Palera1nRelease> ParseReleasesJson(string json) =>
        JsonSerializer.Deserialize<List<Palera1nRelease>>(json) ?? [];

    private bool TryReadCache(out IReadOnlyList<Palera1nRelease>? releases)
    {
        releases = null;
        try
        {
            if (!File.Exists(_cachePath)) return false;
            var info = new FileInfo(_cachePath);
            if (DateTimeOffset.UtcNow - info.LastWriteTimeUtc > CacheDuration) return false;
            releases = ParseReleasesJson(File.ReadAllText(_cachePath));
            return releases.Count > 0;
        }
        catch { return false; }
    }

    private void WriteCache(IReadOnlyList<Palera1nRelease> releases)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temporary = _cachePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(releases, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch { }
    }

    private static void TryDelete(string? path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
