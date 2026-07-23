using System.Text.RegularExpressions;

namespace DarkSwordRestore.Core;

public static class DowngradeStagePlan
{
    public const string PwnedDfuOnlyArgument = "--pwned-dfu-only";
    public const string RequiredPwnedDfuMarker = "PWND:[yolo]";

    private static readonly Regex PwnedDfuPattern = new(
        @"(?im)(?:^|\r?\n)\s*PWND:\s*\[?yolo\]?\s*(?:\r?$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsPwnedDfuQueryOutput(string? output) =>
        !string.IsNullOrWhiteSpace(output) && PwnedDfuPattern.IsMatch(output);
}
