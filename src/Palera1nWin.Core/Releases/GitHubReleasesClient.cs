using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Releases;

public sealed class Palera1nReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class Palera1nRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTimeOffset PublishedAt { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("assets")]
    public List<Palera1nReleaseAsset> Assets { get; set; } = new();

    public Palera1nReleaseAsset? PreferredLinuxBinary =>
        Assets.FirstOrDefault(a => a.Name.Contains("palera1n-linux-x86_64", StringComparison.OrdinalIgnoreCase)) ??
        Assets.FirstOrDefault(a => a.Name.Contains("palera1n", StringComparison.OrdinalIgnoreCase) &&
                                   a.Name.Contains("linux", StringComparison.OrdinalIgnoreCase));
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
        if (httpClient is null)
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        _cachePath = cachePath ?? Path.Combine(AppSettings.RuntimeDirectory, "releases-cache.json");

        if (_httpClient.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Palera1nWin", "1.0"));
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/vnd.github+json"))
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    public async Task<IReadOnlyList<Palera1nRelease>> GetReleasesAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && TryReadCache(out var cached) && cached is not null)
        {
            return cached;
        }

        using var response = await _httpClient.GetAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var releases = await JsonSerializer.DeserializeAsync<List<Palera1nRelease>>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? new List<Palera1nRelease>();

        WriteCache(releases);
        return releases;
    }

    public async Task DownloadReleaseBinaryAsync(
        string tag,
        string destinationPath,
        IProgress<ProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var releases = await GetReleasesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var release = releases.FirstOrDefault(r =>
            string.Equals(r.TagName, tag, StringComparison.OrdinalIgnoreCase));

        if (release is null)
        {
            throw new InvalidOperationException($"Release tag not found: {tag}");
        }

        var asset = release.PreferredLinuxBinary;
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
        {
            throw new InvalidOperationException($"Release {tag} has no palera1n-linux-x86_64 asset.");
        }

        // Supply-chain guard: only download from GitHub's known hosts.
        if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri) ||
            (!downloadUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
             !downloadUri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
             !downloadUri.Host.Equals("codeload.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Refusing to download from untrusted host: {asset.BrowserDownloadUrl}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? AppSettings.RuntimeDirectory);

        progress?.Report(new ProgressEventArgs("download", $"Downloading {asset.Name}...", 0));

        try
        {
            using var response = await _httpClient.GetAsync(
                asset.BrowserDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = File.Create(destinationPath);

            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                readTotal += read;

                if (total > 0)
                {
                    var percent = (int)(readTotal * 100 / total);
                    progress?.Report(new ProgressEventArgs("download", $"Downloading {asset.Name}...", percent));
                }
            }

            progress?.Report(new ProgressEventArgs("download", $"Saved {destinationPath}", 100));
        }
        catch
        {
            // Delete partial download so a subsequent run doesn't use a truncated file.
            try { if (File.Exists(destinationPath)) File.Delete(destinationPath); }
            catch { /* best effort */ }
            throw;
        }
    }

    public static IReadOnlyList<Palera1nRelease> ParseReleasesJson(string json)
    {
        var releases = JsonSerializer.Deserialize<List<Palera1nRelease>>(json);
        return releases ?? new List<Palera1nRelease>();
    }

    private bool TryReadCache(out IReadOnlyList<Palera1nRelease>? releases)
    {
        releases = null;
        try
        {
            if (!File.Exists(_cachePath))
            {
                return false;
            }

            var info = new FileInfo(_cachePath);
            if (DateTimeOffset.UtcNow - info.LastWriteTimeUtc > CacheDuration)
            {
                return false;
            }

            var json = File.ReadAllText(_cachePath);
            releases = ParseReleasesJson(json);
            return releases.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private void WriteCache(IReadOnlyList<Palera1nRelease> releases)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var json = JsonSerializer.Serialize(releases, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // Cache write failures are non-fatal.
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
