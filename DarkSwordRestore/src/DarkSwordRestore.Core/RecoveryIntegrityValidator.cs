using System.Security.Cryptography;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed record RecoveryArtifactIndex(
    int SchemaVersion,
    string SessionId,
    string? ProductVersion,
    string? BuildVersion,
    string? PreRestoreShc,
    string? PreRestoreShcSha256,
    string? PostRestoreShc,
    string? PostRestoreShcSha256,
    string? PteBlock,
    string? PteBlockSha256,
    DateTimeOffset CreatedAt);

public static class RecoveryIntegrityValidator
{
    public const int CurrentSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static RecoveryCandidate ValidateAndNormalize(RecoveryCandidate candidate)
    {
        var session = candidate.Session;
        var sessionRoot = NormalizeDirectory(session.SessionDirectory);
        if (!Directory.Exists(sessionRoot))
            throw new DirectoryNotFoundException($"Recovery session directory is missing: {sessionRoot}");

        var sessionFile = Path.Combine(sessionRoot, "session.json");
        if (!File.Exists(sessionFile))
            throw new InvalidDataException("Recovery session.json is missing.");

        var artifacts = EnumerateValidatedArtifacts(session, sessionRoot).ToArray();
        var indexPath = Path.Combine(sessionRoot, "recovery-artifact-index.json");
        RecoveryArtifactIndex index;
        if (File.Exists(indexPath))
        {
            index = JsonSerializer.Deserialize<RecoveryArtifactIndex>(File.ReadAllText(indexPath), JsonOptions)
                    ?? throw new InvalidDataException("Recovery artifact index is unreadable.");
            ValidateIndex(session, sessionRoot, index, artifacts);
        }
        else
        {
            index = BuildAndFreezeIndex(session, artifacts);
            var temporary = indexPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(index, JsonOptions));
            File.Move(temporary, indexPath, overwrite: true);
        }

        var pre = ResolveIndexedArtifact(sessionRoot, index.PreRestoreShc, index.PreRestoreShcSha256, artifacts);
        var post = ResolveIndexedArtifact(sessionRoot, index.PostRestoreShc, index.PostRestoreShcSha256, artifacts);
        var pte = ResolveIndexedArtifact(sessionRoot, index.PteBlock, index.PteBlockSha256, artifacts);
        var canResume = pte is not null || post is not null || pre is not null;
        var description = pte is not null
            ? "Session-bound PTE is complete; retry final tether boot."
            : post is not null
                ? "Session-bound post-restore SHC exists; continue with PTE generation."
                : pre is not null
                    ? "Session-bound pre-restore SHC exists; retry restore without repeating the first capture."
                    : "No session-bound recovery artifact is available.";
        return new RecoveryCandidate(session, description, canResume, pre, post, pte);
    }

    private static IEnumerable<(string Path, RestoreArtifactMetadata Metadata, string Hash)> EnumerateValidatedArtifacts(
        RestoreSession session,
        string sessionRoot)
    {
        foreach (var metadataPath in Directory.EnumerateFiles(sessionRoot, "*.metadata.json", SearchOption.AllDirectories))
        {
            RestoreArtifactMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<RestoreArtifactMetadata>(File.ReadAllText(metadataPath), JsonOptions);
            }
            catch
            {
                continue;
            }
            if (metadata is null) continue;
            if (!string.Equals(metadata.SessionId, session.SessionId, StringComparison.Ordinal)) continue;
            if (!string.Equals(metadata.ProductVersion, session.Ipsw.ProductVersion, StringComparison.Ordinal)) continue;
            if (!string.Equals(metadata.BuildVersion, session.Ipsw.BuildVersion, StringComparison.Ordinal)) continue;

            string path;
            try { path = Path.GetFullPath(metadata.Path); }
            catch { continue; }
            if (!IsInside(sessionRoot, path) || !File.Exists(path)) continue;
            var metadataExpected = path + ".metadata.json";
            if (!string.Equals(Path.GetFullPath(metadataPath), metadataExpected, StringComparison.OrdinalIgnoreCase)) continue;

            var file = new FileInfo(path);
            if (file.Length <= 0 || file.Length != metadata.Size) continue;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(hash, metadata.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            yield return (path, metadata, hash);
        }
    }

    private static RecoveryArtifactIndex BuildAndFreezeIndex(
        RestoreSession session,
        IReadOnlyCollection<(string Path, RestoreArtifactMetadata Metadata, string Hash)> artifacts)
    {
        // Legacy metadata did not contain a role. Migrate it exactly once by sorting
        // the two independently hashed SHC creations, then freeze paths+hashes in an
        // index. Future timestamp changes cannot swap the roles.
        var shc = artifacts
            .Where(item => item.Metadata.ArtifactType.Contains("shc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Metadata.CreatedAt)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pte = artifacts
            .Where(item => item.Metadata.ArtifactType.Contains("pte", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Metadata.CreatedAt)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        var pre = shc.FirstOrDefault();
        var post = shc.Length >= 2 ? shc[^1] : default;
        return new RecoveryArtifactIndex(
            CurrentSchemaVersion,
            session.SessionId,
            session.Ipsw.ProductVersion,
            session.Ipsw.BuildVersion,
            Relative(session.SessionDirectory, pre.Path),
            pre.Hash,
            Relative(session.SessionDirectory, post.Path),
            post.Hash,
            Relative(session.SessionDirectory, pte.Path),
            pte.Hash,
            DateTimeOffset.UtcNow);
    }

    private static void ValidateIndex(
        RestoreSession session,
        string sessionRoot,
        RecoveryArtifactIndex index,
        IReadOnlyCollection<(string Path, RestoreArtifactMetadata Metadata, string Hash)> artifacts)
    {
        if (index.SchemaVersion != CurrentSchemaVersion ||
            !string.Equals(index.SessionId, session.SessionId, StringComparison.Ordinal) ||
            !string.Equals(index.ProductVersion, session.Ipsw.ProductVersion, StringComparison.Ordinal) ||
            !string.Equals(index.BuildVersion, session.Ipsw.BuildVersion, StringComparison.Ordinal))
            throw new InvalidDataException("Recovery artifact index does not belong to this session and build.");

        _ = ResolveIndexedArtifact(sessionRoot, index.PreRestoreShc, index.PreRestoreShcSha256, artifacts);
        _ = ResolveIndexedArtifact(sessionRoot, index.PostRestoreShc, index.PostRestoreShcSha256, artifacts);
        _ = ResolveIndexedArtifact(sessionRoot, index.PteBlock, index.PteBlockSha256, artifacts);
    }

    private static string? ResolveIndexedArtifact(
        string sessionRoot,
        string? relativePath,
        string? expectedHash,
        IReadOnlyCollection<(string Path, RestoreArtifactMetadata Metadata, string Hash)> artifacts)
    {
        if (string.IsNullOrWhiteSpace(relativePath) && string.IsNullOrWhiteSpace(expectedHash)) return null;
        if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(expectedHash))
            throw new InvalidDataException("Recovery artifact index contains an incomplete path/hash pair.");
        var path = Path.GetFullPath(Path.Combine(sessionRoot, relativePath));
        if (!IsInside(sessionRoot, path)) throw new InvalidDataException("Recovery artifact escapes its session directory.");
        var match = artifacts.FirstOrDefault(item =>
            string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Hash, expectedHash, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(match.Path))
            throw new InvalidDataException($"Indexed recovery artifact is missing or changed: {relativePath}");
        return match.Path;
    }

    private static string? Relative(string sessionDirectory, string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetRelativePath(sessionDirectory, path).Replace('\\', '/');

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static bool IsInside(string root, string path) =>
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
}
