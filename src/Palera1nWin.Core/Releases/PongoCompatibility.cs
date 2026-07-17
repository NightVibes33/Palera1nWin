namespace Palera1nWin.Core.Releases;

public enum PongoCompatibilityLevel
{
    /// <summary>Not checked yet (release binary not downloaded, tag unknown).</summary>
    Unknown,

    /// <summary>Embedded PongoOS version matches our bundled openra1n's.</summary>
    Compatible,

    /// <summary>Embedded PongoOS version differs from our bundled openra1n's.</summary>
    Incompatible,
}

public sealed record PongoCompatibilityResult(PongoCompatibilityLevel Level, string? DetectedVersion, string? BundledVersion)
{
    public string Summary => Level switch
    {
        PongoCompatibilityLevel.Compatible =>
            $"PongoOS {DetectedVersion} — matches the bundled openra1n ({BundledVersion}).",
        PongoCompatibilityLevel.Incompatible =>
            $"PongoOS {DetectedVersion ?? "unknown"} — differs from the bundled openra1n ({BundledVersion ?? "unknown"}). " +
            "The device-side checkm8/PongoOS upload always uses openra1n's fixed image; this palera1n build's payload " +
            "logic was written against a different PongoOS and may not work correctly with it.",
        _ => "Pongo compatibility not yet checked — will be verified after download.",
    };
}

/// <summary>
/// Checks whether a given palera1n release's expected PongoOS build matches the
/// PongoOS actually embedded in our bundled openra1n.exe.
///
/// Background (verified empirically 2026-07-17 against all 16 palera1n releases
/// published between 2023-03 and 2026-05, by extracting each release's own
/// embedded PongoOS version string):
///
///     PongoOS 2.6.1  -&gt;  v2.0.0-beta.4 .. v2.0.0-beta.7      (2023-03 to 2023-05)
///     PongoOS 2.6.2  -&gt;  v2.0.0-beta.8 .. v2.0.1             (2023-10 to 2024-08)
///     PongoOS 2.6.3  -&gt;  v2.0.2 onward (v2.1-beta.*, v2.2,
///                        v2.2.1, v2.3, ...)                  (2024-09+)
///
/// Our bundled openra1n.exe embeds PongoOS 2.6.3, so every release from v2.0.2
/// onward is confirmed compatible. There is no per-release-tag Pongo.bin published
/// upstream — palera1n's own build fetches Pongo from a single, non-versioned CDN
/// URL (https://cdn.nickchan.lol/palera1n/artifacts/kpf/iOS15/Pongo.bin), the same
/// URL across every tag we checked (confirmed via `git show &lt;tag&gt;:src/Makefile`
/// for v2.2.1 and v2.3). PongoOS images also have no in-band header or length
/// field, so carving a specific historical build out of a stripped release binary
/// cannot be done reliably/safely. Given that, this class does NOT attempt to
/// rebuild or patch openra1n per version — it detects and reports mismatches
/// instead, using the same lightweight, symbol-free string scan on any binary
/// (works even fully stripped, since the version string is plain embedded rodata).
/// </summary>
public static class PongoCompatibility
{
    /// <summary>
    /// Empirically-tested tag -&gt; embedded PongoOS "major.minor.patch" map.
    /// Lets the Versions tab show a compatibility badge immediately, without
    /// downloading, for every release tested as of 2026-07-17. Tags not in this
    /// map (e.g. any future release) fall back to "Unknown" until downloaded,
    /// at which point <see cref="ExtractEmbeddedPongoVersion"/> determines the
    /// real answer from the actual binary.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownEras =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["v2.0.0-beta.4"] = "2.6.1",
            ["v2.0.0-beta.5"] = "2.6.1",
            ["v2.0.0-beta.6.2"] = "2.6.1",
            ["v2.0.0-beta.7"] = "2.6.1",
            ["v2.0.0-beta.8"] = "2.6.2",
            ["v2.0.0-beta.9"] = "2.6.2",
            ["v2.0.0-beta.9.1"] = "2.6.2",
            ["v2.0.0-beta.9.2"] = "2.6.2",
            ["v2.0"] = "2.6.2",
            ["v2.0.1"] = "2.6.2",
            ["v2.0.2"] = "2.6.3",
            ["v2.1-beta.1"] = "2.6.3",
            ["v2.1-beta.2"] = "2.6.3",
            ["v2.2"] = "2.6.3",
            ["v2.2.1"] = "2.6.3",
            ["v2.3"] = "2.6.3",
        };

    /// <summary>
    /// Scans raw binary bytes (an openra1n or palera1n executable, stripped or
    /// not) for an embedded "pongoOS-X.Y.Z-hash" string and returns "X.Y.Z", or
    /// null if none is found. No symbol table is required — the version string
    /// is plain ASCII embedded in the binary's data section by PongoOS itself
    /// (it is PongoOS's own USB serial-number banner text, compiled directly
    /// into the raw image that gets embedded in the containing executable).
    /// </summary>
    public static string? ExtractEmbeddedPongoVersion(byte[] data)
    {
        var pattern = "pongoos-"u8.ToArray();

        for (var i = 0; i + pattern.Length < data.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                // Lowercase ASCII letters only differ from uppercase by bit 0x20;
                // digits/'-' already match `pattern` verbatim either way.
                var b = data[i + j];
                if ((b | 0x20) != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (!match)
            {
                continue;
            }

            var start = i + pattern.Length;
            var end = start;
            while (end < data.Length && end - start < 16 && (IsAsciiDigit(data[end]) || data[end] == (byte)'.'))
            {
                end++;
            }

            // Require at least "X.Y" to avoid matching stray digits.
            if (end > start && Array.IndexOf(data, (byte)'.', start, end - start) >= 0)
            {
                return System.Text.Encoding.ASCII.GetString(data, start, end - start).TrimEnd('.');
            }
        }

        return null;
    }

    public static string? ExtractEmbeddedPongoVersion(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            return ExtractEmbeddedPongoVersion(File.ReadAllBytes(filePath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when two PongoOS version strings agree on their first three
    /// dot-separated components ("X.Y.Z"). PongoOS's observed version scheme is
    /// "2.6.1" / "2.6.2" / "2.6.3" — the era-distinguishing digit we found across
    /// all 16 tested palera1n releases is the THIRD component, not the second,
    /// so comparing only major.minor would treat every era as identical. A
    /// trailing 4th+ component (e.g. a hypothetical "2.6.3.1") is ignored.
    /// </summary>
    public static bool AreCompatible(string a, string b)
    {
        var coreA = CoreVersion(a);
        var coreB = CoreVersion(b);
        return coreA is not null && string.Equals(coreA, coreB, StringComparison.Ordinal);
    }

    private static string? CoreVersion(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 3 ? string.Join('.', parts[0], parts[1], parts[2]) : null;
    }

    private static bool IsAsciiDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    /// <summary>
    /// Best-effort, non-throwing compatibility check for a release tag, using
    /// only the static <see cref="KnownEras"/> map (no I/O). Returns Unknown for
    /// tags we have not tested — callers should re-check with the downloaded
    /// binary via <see cref="CheckBinary"/> once available.
    /// </summary>
    public static PongoCompatibilityResult CheckTag(string tag, string? bundledOpenRa1nPongoVersion)
    {
        if (bundledOpenRa1nPongoVersion is null || !KnownEras.TryGetValue(tag, out var era))
        {
            return new PongoCompatibilityResult(PongoCompatibilityLevel.Unknown, null, bundledOpenRa1nPongoVersion);
        }

        var compatible = AreCompatible(era, bundledOpenRa1nPongoVersion);
        return new PongoCompatibilityResult(
            compatible ? PongoCompatibilityLevel.Compatible : PongoCompatibilityLevel.Incompatible,
            era,
            bundledOpenRa1nPongoVersion);
    }

    /// <summary>
    /// Definitive compatibility check against an actual downloaded palera1n
    /// binary. Supersedes <see cref="CheckTag"/> once the file is available.
    /// </summary>
    public static PongoCompatibilityResult CheckBinary(string palera1nBinaryPath, string? bundledOpenRa1nPongoVersion)
    {
        var detected = ExtractEmbeddedPongoVersion(palera1nBinaryPath);
        if (detected is null || bundledOpenRa1nPongoVersion is null)
        {
            return new PongoCompatibilityResult(PongoCompatibilityLevel.Unknown, detected, bundledOpenRa1nPongoVersion);
        }

        var compatible = AreCompatible(detected, bundledOpenRa1nPongoVersion);
        return new PongoCompatibilityResult(
            compatible ? PongoCompatibilityLevel.Compatible : PongoCompatibilityLevel.Incompatible,
            detected,
            bundledOpenRa1nPongoVersion);
    }
}
