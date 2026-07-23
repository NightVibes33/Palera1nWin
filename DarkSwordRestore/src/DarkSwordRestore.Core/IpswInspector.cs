using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DarkSwordRestore.Core;

public sealed class IpswInspector
{
    private static readonly string[] RequiredEntries = ["BuildManifest.plist", "Restore.plist"];
    private static readonly Regex ProductTypePattern = new(
        @"^(?:iPhone|iPad|iPod)\d+,\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IpswInspectionResult> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var productTypes = new HashSet<string>(StringComparer.Ordinal);
        string? productVersion = null;
        string? buildVersion = null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Invalid(path, "The IPSW file does not exist.");
        if (!path.EndsWith(".ipsw", StringComparison.OrdinalIgnoreCase))
            errors.Add("The selected file does not use the .ipsw extension.");

        var info = new FileInfo(path);
        if (info.Length < 500L * 1024L * 1024L)
            warnings.Add("The IPSW is unusually small and may be incomplete.");

        string sha256;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count < 10) errors.Add("The IPSW archive contains too few entries to be an Apple restore image.");
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.FullName.StartsWith('/') || entry.FullName.StartsWith('\\') ||
                    Path.IsPathRooted(entry.FullName) ||
                    entry.FullName.Split('/', '\\').Any(part => part == ".."))
                {
                    errors.Add($"Unsafe archive path: {entry.FullName}");
                    break;
                }
            }

            foreach (var required in RequiredEntries)
            {
                var entry = archive.GetEntry(required);
                if (entry is null) errors.Add($"Missing required IPSW entry: {required}");
                else if (entry.Length <= 0 || entry.Length > 64L * 1024L * 1024L)
                    errors.Add($"Required IPSW entry has an invalid size: {required}");
            }

            var manifestEntry = archive.GetEntry("BuildManifest.plist");
            if (manifestEntry is not null && manifestEntry.Length is > 0 and <= 64L * 1024L * 1024L)
            {
                await using var manifestStream = manifestEntry.Open();
                using var memory = new MemoryStream((int)Math.Min(manifestEntry.Length, int.MaxValue));
                await manifestStream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                var bytes = memory.ToArray();
                if (bytes.AsSpan().StartsWith("bplist00"u8))
                {
                    errors.Add("Binary BuildManifest.plist is not accepted by the built-in verifier. Use an untouched Apple IPSW with a readable manifest.");
                }
                else
                {
                    var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes), LoadOptions.None);
                    var rootDict = document.Root?.Element("dict");
                    if (rootDict is null)
                    {
                        errors.Add("BuildManifest.plist does not contain a valid root dictionary.");
                    }
                    else
                    {
                        productVersion = ReadString(rootDict, "ProductVersion");
                        buildVersion = ReadString(rootDict, "ProductBuildVersion") ?? ReadString(rootDict, "BuildVersion");
                        foreach (var type in ReadStringArray(rootDict, "SupportedProductTypes")) productTypes.Add(type);
                        if (productTypes.Count == 0)
                        {
                            foreach (var type in document.Descendants("string")
                                         .Select(x => x.Value)
                                         .Where(value => ProductTypePattern.IsMatch(value)))
                            {
                                productTypes.Add(type);
                            }
                        }
                    }
                }
            }

            if (!archive.Entries.Any(x => x.FullName.Contains("iBSS", StringComparison.OrdinalIgnoreCase)))
                errors.Add("The IPSW does not contain an iBSS component.");
            if (!archive.Entries.Any(x => x.FullName.Contains("iBEC", StringComparison.OrdinalIgnoreCase)))
                errors.Add("The IPSW does not contain an iBEC component.");
            if (!archive.Entries.Any(x => x.FullName.Contains("sep-firmware", StringComparison.OrdinalIgnoreCase)))
                errors.Add("The IPSW does not contain SEP firmware.");
        }
        catch (InvalidDataException ex)
        {
            errors.Add($"The IPSW ZIP container is invalid: {ex.Message}");
        }
        catch (System.Xml.XmlException ex)
        {
            errors.Add($"BuildManifest.plist XML is invalid: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            errors.Add($"The IPSW could not be read: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(productVersion))
            errors.Add("BuildManifest.plist does not identify a ProductVersion.");
        else if (!productVersion.StartsWith("15.", StringComparison.Ordinal))
            errors.Add($"Target version {productVersion} is rejected. The active DarkSword restore backend accepts only iOS/iPadOS 15.x.");
        if (string.IsNullOrWhiteSpace(buildVersion)) errors.Add("BuildManifest.plist does not identify a build version.");

        if (!productTypes.Any(DarkSwordDeviceCatalog.IsSupported))
        {
            var listed = productTypes.Count == 0 ? "no ProductType" : string.Join(", ", productTypes.OrderBy(x => x, StringComparer.Ordinal));
            errors.Add($"This firmware targets {listed}. DarkSword supports A9 through A10X catalog devices only.");
        }

        return new IpswInspectionResult(
            errors.Count == 0,
            Path.GetFullPath(path),
            productVersion,
            buildVersion,
            productTypes.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            info.Length,
            sha256,
            errors.Distinct(StringComparer.Ordinal).ToArray(),
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static IpswInspectionResult Invalid(string? path, string error) =>
        new(false, path ?? string.Empty, null, null, [], 0, string.Empty, [error], []);

    private static string? ReadString(XElement dict, string key)
    {
        var children = dict.Elements().ToArray();
        for (var index = 0; index + 1 < children.Length; index++)
        {
            if (children[index].Name.LocalName == "key" && children[index].Value == key)
                return children[index + 1].Name.LocalName == "string" ? children[index + 1].Value : null;
        }
        return null;
    }

    private static IEnumerable<string> ReadStringArray(XElement dict, string key)
    {
        var children = dict.Elements().ToArray();
        for (var index = 0; index + 1 < children.Length; index++)
        {
            if (children[index].Name.LocalName == "key" && children[index].Value == key && children[index + 1].Name.LocalName == "array")
                return children[index + 1].Elements("string").Select(x => x.Value).ToArray();
        }
        return [];
    }
}
