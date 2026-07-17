using Palera1nWin.Core.Releases;

namespace Palera1nWin.Core.Tests;

public sealed class GitHubReleasesClientParseTests
{
    private const string SampleJson = """
        [
          {
            "tag_name": "v2.3.0",
            "name": "v2.3.0",
            "published_at": "2024-05-01T12:00:00Z",
            "body": "Sample release notes",
            "assets": [
              {
                "name": "palera1n-linux-x86_64",
                "browser_download_url": "https://example.com/palera1n-linux-x86_64",
                "size": 1234567
              },
              {
                "name": "Source code (zip)",
                "browser_download_url": "https://example.com/source.zip",
                "size": 999
              }
            ]
          }
        ]
        """;

    [Fact]
    public void ParseReleasesJson_DeserializesReleaseAndPreferredAsset()
    {
        var releases = GitHubReleasesClient.ParseReleasesJson(SampleJson);

        Assert.Single(releases);
        var release = releases[0];
        Assert.Equal("v2.3.0", release.TagName);
        Assert.Equal("Sample release notes", release.Body);
        Assert.Equal(2, release.Assets.Count);

        var preferred = release.PreferredLinuxBinary;
        Assert.NotNull(preferred);
        Assert.Equal("palera1n-linux-x86_64", preferred!.Name);
        Assert.Equal("https://example.com/palera1n-linux-x86_64", preferred.BrowserDownloadUrl);
        Assert.Equal(1234567, preferred.Size);
    }
}
