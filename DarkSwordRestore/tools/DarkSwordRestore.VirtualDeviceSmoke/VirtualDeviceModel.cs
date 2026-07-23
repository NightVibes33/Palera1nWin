using System.Text.Json;
using System.Text.Json.Serialization;

namespace DarkSwordRestore.VirtualDeviceSmoke;

public enum VirtualDeviceMode
{
    Disconnected,
    Normal,
    CleanDfu,
    PwnedDfu,
    Pongo,
    AwaitingDfu,
    BootedIos15,
    Jailbroken,
}

public sealed record VirtualDeviceEvent(
    int Sequence,
    DateTimeOffset Timestamp,
    string Actor,
    string Action,
    VirtualDeviceMode Mode,
    string Detail);

public sealed class VirtualDeviceDocument
{
    public string ProductType { get; set; } = "iPad6,11";
    public string Ecid { get; set; } = "0x1122334455667788";
    public string Cpid { get; set; } = "0x8003";
    public VirtualDeviceMode Mode { get; set; } = VirtualDeviceMode.CleanDfu;
    public string? PwnedMarker { get; set; }
    public string? Fault { get; set; }
    public int Sequence { get; set; }
    public int PwnGeneration { get; set; }
    public int VerifiedPwnGeneration { get; set; }
    public bool EraseStarted { get; set; }
    public bool RestoreCompleted { get; set; }
    public bool WslAttached { get; set; }
    public bool JailbreakInstalled { get; set; }
    public bool TetherBootCompleted { get; set; }
    public Dictionary<string, string> Artifacts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VirtualDeviceEvent> Events { get; set; } = [];

    public void AddEvent(string actor, string action, string detail)
    {
        Sequence++;
        Events.Add(new VirtualDeviceEvent(Sequence, DateTimeOffset.UtcNow, actor, action, Mode, detail));
    }
}

public static class VirtualDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static VirtualDeviceDocument CreateClean(string path, string? fault = null)
    {
        var document = new VirtualDeviceDocument { Fault = fault };
        document.AddEvent("harness", "create", $"Virtual {document.ProductType} created in clean DFU.");
        Save(path, document);
        return document;
    }

    public static VirtualDeviceDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Virtual device state file was not found.", path);
        var document = JsonSerializer.Deserialize<VirtualDeviceDocument>(File.ReadAllText(path), JsonOptions);
        return document ?? throw new InvalidDataException("Virtual device state file was empty or malformed.");
    }

    public static VirtualDeviceDocument Mutate(string path, Action<VirtualDeviceDocument> mutation)
    {
        var document = Load(path);
        mutation(document);
        Save(path, document);
        return document;
    }

    public static void Save(string path, VirtualDeviceDocument document)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, full, overwrite: true);
    }
}
