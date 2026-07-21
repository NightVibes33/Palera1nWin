using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DarkSwordRestore.Core;

public sealed class IpswInspector
{
    private static readonly string[] RequiredEntries =
    {
        "BuildManifest.plist",
        "Restore.plist"
    };

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
        {
            return Invalid(path, "The IPSW file does not exist.");
        }
        if (!path.EndsWith(".ipsw", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("The selected file does not use the .ipsw extension.");
        }

        var info = new FileInfo(path);
        if (info.Length < 500L * 1024L * 1024L)
        {
            warnings.Add("The IPSW is unusually small and may be incomplete.");
        }

        string sha256;
        await using (var stream = File.OpenRead(path))
        {
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var required in RequiredEntries)
            {
                if (archive.GetEntry(required) is null)
                {
                    errors.Add($"Missing required IPSW entry: {required}");
                }
            }

            var manifestEntry = archive.GetEntry("BuildManifest.plist");
            if (manifestEntry is not null)
            {
                await using var manifestStream = manifestEntry.Open();
                using var memory = new MemoryStream();
                await manifestStream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                var bytes = memory.ToArray();
                if (bytes.AsSpan().StartsWith("bplist00"u8))
                {
                    errors.Add("Binary BuildManifest.plist is not accepted by the built-in verifier. Re-download the original Apple IPSW or validate it with the bundled native inspector.");
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
                        foreach (var type in ReadStringArray(rootDict, "SupportedProductTypes"))
                        {
                            productTypes.Add(type);
                        }

                        if (productTypes.Count == 0)
                        {
                            foreach (var type in document.Descendants("string")
                                         .Select(x => x.Value)
                                         .Where(x => ProductTypePattern.IsMatch(x)))
                            {
                                productTypes.Add(type);
                            }
                        }
                    }
                }
            }

            if (!archive.Entries.Any(x => x.FullName.Contains("iBSS", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("The IPSW does not contain an iBSS component.");
            }
            if (!archive.Entries.Any(x => x.FullName.Contains("iBEC", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("The IPSW does not contain an iBEC component.");
            }
            if (!archive.Entries.Any(x => x.FullName.Contains("sep-firmware", StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add("The IPSW does not contain SEP firmware.");
            }
        }
        catch (InvalidDataException ex)
        {
            errors.Add($"The IPSW ZIP container is invalid: {ex.Message}");
        }
        catch (System.Xml.XmlException ex)
        {
            errors.Add($"BuildManifest.plist XML is invalid: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"The IPSW could not be read: {ex.Message}");
        }

        if (!productTypes.Any(DarkSwordDeviceCatalog.IsSupported))
        {
            var listed = productTypes.Count == 0 ? "no ProductType" : string.Join(", ", productTypes.OrderBy(x => x, StringComparer.Ordinal));
            errors.Add($"This firmware targets {listed}. DarkSword supports iOS/iPadOS devices with A9 through A10X chips only.");
        }
        if (productVersion is not null && !productVersion.StartsWith("15.", StringComparison.Ordinal))
        {
            warnings.Add($"Target version is {productVersion}; the in-app downloader is intentionally limited to iOS/iPadOS 15.x.");
        }

        return new IpswInspectionResult(
            errors.Count == 0,
            Path.GetFullPath(path),
            productVersion,
            buildVersion,
            productTypes.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            info.Length,
            sha256,
            errors,
            warnings);
    }

    private static IpswInspectionResult Invalid(string? path, string error) =>
        new(false, path ?? string.Empty, null, null, Array.Empty<string>(), 0, string.Empty, new[] { error }, Array.Empty<string>());

    private static string? ReadString(XElement dict, string key)
    {
        var children = dict.Elements().ToArray();
        for (var index = 0; index + 1 < children.Length; index++)
        {
            if (children[index].Name.LocalName == "key" && children[index].Value == key)
            {
                return children[index + 1].Name.LocalName == "string" ? children[index + 1].Value : null;
            }
        }
        return null;
    }

    private static IEnumerable<string> ReadStringArray(XElement dict, string key)
    {
        var children = dict.Elements().ToArray();
        for (var index = 0; index + 1 < children.Length; index++)
        {
            if (children[index].Name.LocalName == "key" && children[index].Value == key && children[index + 1].Name.LocalName == "array")
            {
                return children[index + 1].Elements("string").Select(x => x.Value).ToArray();
            }
        }
        return Array.Empty<string>();
    }
}
