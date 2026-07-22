using System.Text;
using Palera1nWin.Core.Releases;

namespace Palera1nWin.Core.Tests;

public sealed class PongoCompatibilityTests
{
    private static byte[] FakeBinary(params string[] embeddedStrings)
    {
        // Simulate a stripped executable: real code/data bytes surrounding
        // plain ASCII strings, same as how `strings` finds them for real.
        var sb = new StringBuilder();
        sb.Append('\x01', 32);
        foreach (var s in embeddedStrings)
        {
            sb.Append(s).Append('\0');
            sb.Append('\x02', 16);
        }

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Theory]
    [InlineData("pongoOS-2.6.3-02051bf2", "2.6.3")]
    [InlineData("PongoOS-2.6.3-02051bf2", "2.6.3")]
    [InlineData("pongoOS-2.6.1-742d92a0", "2.6.1")]
    [InlineData("pongoOS-2.6.2-3c6a76da", "2.6.2")]
    public void ExtractEmbeddedPongoVersion_FindsVersionInStrippedStyleBinary(string embedded, string expected)
    {
        var data = FakeBinary("SRTG:[iBoot-3332.0.0.1.23]", embedded, "some other unrelated string");

        var version = PongoCompatibility.ExtractEmbeddedPongoVersion(data);

        Assert.Equal(expected, version);
    }

    [Fact]
    public void ExtractEmbeddedPongoVersion_ReturnsNullWhenNoMatch()
    {
        var data = FakeBinary("nothing pongo-related here", "checkra1n team");

        var version = PongoCompatibility.ExtractEmbeddedPongoVersion(data);

        Assert.Null(version);
    }

    [Fact]
    public void ExtractEmbeddedPongoVersion_FindsFirstOfMultipleEmbeddedBuilds()
    {
        // Real palera1n binaries embed two PongoOS builds (different checkm8
        // variants); extraction should still return a well-formed version.
        var data = FakeBinary("pongoOS-2.6.3-7ddd2752", "pongoOS-2.6.3-c2e123aa");

        var version = PongoCompatibility.ExtractEmbeddedPongoVersion(data);

        Assert.Equal("2.6.3", version);
    }

    [Theory]
    [InlineData("2.6.3", "2.6.3", true)]
    [InlineData("2.6.3", "2.6.3.9", true)]
    [InlineData("2.6.2", "2.6.3", false)]
    [InlineData("2.6.1", "2.6.3", false)]
    [InlineData("2.7.0", "2.6.3", false)]
    public void AreCompatible_ComparesMajorMinorOnly(string a, string b, bool expected)
    {
        Assert.Equal(expected, PongoCompatibility.AreCompatible(a, b));
    }

    [Theory]
    [InlineData("v2.0.0-beta.4", "2.6.1")]
    [InlineData("v2.0.0-beta.7", "2.6.1")]
    [InlineData("v2.0.0-beta.8", "2.6.2")]
    [InlineData("v2.0.1", "2.6.2")]
    [InlineData("v2.0.2", "2.6.3")]
    [InlineData("v2.1-beta.1", "2.6.3")]
    [InlineData("v2.2.1", "2.6.3")]
    [InlineData("v2.3", "2.6.3")]
    public void KnownEras_MatchesEmpiricallyTestedHistory(string tag, string expectedEra)
    {
        Assert.True(PongoCompatibility.KnownEras.TryGetValue(tag, out var era));
        Assert.Equal(expectedEra, era);
    }

    [Fact]
    public void CheckTag_ReturnsCompatible_ForCurrentEraRelease()
    {
        var result = PongoCompatibility.CheckTag("v2.2.1", bundledOpenRa1nPongoVersion: "2.6.3");

        Assert.Equal(PongoCompatibilityLevel.Compatible, result.Level);
        Assert.Equal("2.6.3", result.DetectedVersion);
    }

    [Fact]
    public void CheckTag_ReturnsIncompatible_ForOldEraRelease()
    {
        var result = PongoCompatibility.CheckTag("v2.0.0-beta.4", bundledOpenRa1nPongoVersion: "2.6.3");

        Assert.Equal(PongoCompatibilityLevel.Incompatible, result.Level);
        Assert.Equal("2.6.1", result.DetectedVersion);
        Assert.Contains("2.6.1", result.Summary);
    }

    [Fact]
    public void CheckTag_ReturnsUnknown_ForUntestedTag()
    {
        // Simulates a brand-new palera1n release not yet in the static map —
        // must not silently claim compatibility.
        var result = PongoCompatibility.CheckTag("v99.0.0-future", bundledOpenRa1nPongoVersion: "2.6.3");

        Assert.Equal(PongoCompatibilityLevel.Unknown, result.Level);
    }

    [Fact]
    public void CheckTag_ReturnsUnknown_WhenBundledVersionUnavailable()
    {
        var result = PongoCompatibility.CheckTag("v2.3", bundledOpenRa1nPongoVersion: null);

        Assert.Equal(PongoCompatibilityLevel.Unknown, result.Level);
    }

    [Fact]
    public void CheckBinary_ReturnsDefinitiveAnswerFromActualFileContents()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, FakeBinary("pongoOS-2.6.2-deadbeef"));

            var result = PongoCompatibility.CheckBinary(tempFile, bundledOpenRa1nPongoVersion: "2.6.3");

            Assert.Equal(PongoCompatibilityLevel.Incompatible, result.Level);
            Assert.Equal("2.6.2", result.DetectedVersion);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CheckBinary_ReturnsUnknown_WhenFileMissing()
    {
        var result = PongoCompatibility.CheckBinary(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            bundledOpenRa1nPongoVersion: "2.6.3");

        Assert.Equal(PongoCompatibilityLevel.Unknown, result.Level);
    }
}
