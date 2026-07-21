namespace DarkSwordRestore.Core;

public enum DarkSwordChipFamily
{
    A9,
    A9X,
    A10,
    A10X
}

public enum DfuButtonProfile
{
    HomeButton,
    VolumeDown
}

public sealed record DarkSwordDevice(
    string ProductType,
    string DisplayName,
    DarkSwordChipFamily Chip,
    DfuButtonProfile DfuProfile)
{
    public bool UsesA9SepBlocks => Chip is DarkSwordChipFamily.A9 or DarkSwordChipFamily.A9X;
    public string PlatformName => ProductType.StartsWith("iPad", StringComparison.Ordinal) ? "iPadOS" : "iOS";
}

public static class DarkSwordDeviceCatalog
{
    private static readonly IReadOnlyDictionary<string, DarkSwordDevice> ByProductType =
        new Dictionary<string, DarkSwordDevice>(StringComparer.Ordinal)
        {
            ["iPhone8,1"] = new("iPhone8,1", "iPhone 6s", DarkSwordChipFamily.A9, DfuButtonProfile.HomeButton),
            ["iPhone8,2"] = new("iPhone8,2", "iPhone 6s Plus", DarkSwordChipFamily.A9, DfuButtonProfile.HomeButton),
            ["iPhone8,4"] = new("iPhone8,4", "iPhone SE (1st generation)", DarkSwordChipFamily.A9, DfuButtonProfile.HomeButton),

            ["iPhone9,1"] = new("iPhone9,1", "iPhone 7 (Global)", DarkSwordChipFamily.A10, DfuButtonProfile.VolumeDown),
            ["iPhone9,2"] = new("iPhone9,2", "iPhone 7 Plus (Global)", DarkSwordChipFamily.A10, DfuButtonProfile.VolumeDown),
            ["iPhone9,3"] = new("iPhone9,3", "iPhone 7 (GSM)", DarkSwordChipFamily.A10, DfuButtonProfile.VolumeDown),
            ["iPhone9,4"] = new("iPhone9,4", "iPhone 7 Plus (GSM)", DarkSwordChipFamily.A10, DfuButtonProfile.VolumeDown),
            ["iPod9,1"] = new("iPod9,1", "iPod touch (7th generation)", DarkSwordChipFamily.A10, DfuButtonProfile.HomeButton),

            ["iPad6,3"] = new("iPad6,3", "iPad Pro 9.7-inch (Wi-Fi)", DarkSwordChipFamily.A9X, DfuButtonProfile.HomeButton),
            ["iPad6,4"] = new("iPad6,4", "iPad Pro 9.7-inch (Cellular)", DarkSwordChipFamily.A9X, DfuButtonProfile.HomeButton),
            ["iPad6,7"] = new("iPad6,7", "iPad Pro 12.9-inch (1st generation, Wi-Fi)", DarkSwordChipFamily.A9X, DfuButtonProfile.HomeButton),
            ["iPad6,8"] = new("iPad6,8", "iPad Pro 12.9-inch (1st generation, Cellular)", DarkSwordChipFamily.A9X, DfuButtonProfile.HomeButton),
            ["iPad6,11"] = new("iPad6,11", "iPad (5th generation, Wi-Fi)", DarkSwordChipFamily.A9, DfuButtonProfile.HomeButton),
            ["iPad6,12"] = new("iPad6,12", "iPad (5th generation, Cellular)", DarkSwordChipFamily.A9, DfuButtonProfile.HomeButton),

            ["iPad7,1"] = new("iPad7,1", "iPad Pro 12.9-inch (2nd generation, Wi-Fi)", DarkSwordChipFamily.A10X, DfuButtonProfile.HomeButton),
            ["iPad7,2"] = new("iPad7,2", "iPad Pro 12.9-inch (2nd generation, Cellular)", DarkSwordChipFamily.A10X, DfuButtonProfile.HomeButton),
            ["iPad7,3"] = new("iPad7,3", "iPad Pro 10.5-inch (Wi-Fi)", DarkSwordChipFamily.A10X, DfuButtonProfile.HomeButton),
            ["iPad7,4"] = new("iPad7,4", "iPad Pro 10.5-inch (Cellular)", DarkSwordChipFamily.A10X, DfuButtonProfile.HomeButton),
            ["iPad7,5"] = new("iPad7,5", "iPad (6th generation, Wi-Fi)", DarkSwordChipFamily.A10, DfuButtonProfile.HomeButton),
            ["iPad7,6"] = new("iPad7,6", "iPad (6th generation, Cellular)", DarkSwordChipFamily.A10, DfuButtonProfile.HomeButton),
            ["iPad7,11"] = new("iPad7,11", "iPad (7th generation, Wi-Fi)", DarkSwordChipFamily.A10, DfuButtonProfile.HomeButton),
            ["iPad7,12"] = new("iPad7,12", "iPad (7th generation, Cellular)", DarkSwordChipFamily.A10, DfuButtonProfile.HomeButton)
        };

    public static IReadOnlyCollection<DarkSwordDevice> All { get; } =
        ByProductType.Values
            .OrderBy(device => device.ProductType.StartsWith("iPad", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(device => device.DisplayName, StringComparer.Ordinal)
            .ThenBy(device => device.ProductType, StringComparer.Ordinal)
            .ToArray();

    public static bool IsSupported(string? productType) =>
        productType is not null && ByProductType.ContainsKey(productType);

    public static DarkSwordDevice? Find(string? productType) =>
        productType is not null && ByProductType.TryGetValue(productType, out var device) ? device : null;
}
