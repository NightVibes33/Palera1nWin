namespace DarkSwordRestore.Core;

public enum PongoBootPlanKind
{
    DarkSwordStock,
    Palera1n,
    DarkSwordJailbroken,
}

public sealed record PongoBootPlanCapability(
    PongoBootPlanKind Kind,
    bool IsEnabled,
    string DisplayName,
    string Reason);

public interface IPongoBootPlan
{
    PongoBootPlanKind Kind { get; }

    Task ExecuteAsync(
        string pteBlockPath,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken);
}

public static class PongoBootPlanRegistry
{
    private static readonly IReadOnlyDictionary<PongoBootPlanKind, PongoBootPlanCapability> Capabilities =
        new Dictionary<PongoBootPlanKind, PongoBootPlanCapability>
        {
            [PongoBootPlanKind.DarkSwordStock] = new(
                PongoBootPlanKind.DarkSwordStock,
                true,
                "DarkSword tether boot",
                "Uses the exact validated PTE, sep_racer, KPF, and one final bootux command."),
            [PongoBootPlanKind.Palera1n] = new(
                PongoBootPlanKind.Palera1n,
                true,
                "palera1n jailbreak boot",
                "Uses the controlled Windows/WSL ownership pipeline and palera1n payload ordering."),
            [PongoBootPlanKind.DarkSwordJailbroken] = new(
                PongoBootPlanKind.DarkSwordJailbroken,
                false,
                "Combined DarkSword + jailbreak boot",
                "Disabled until DarkSword SEP/PTE commands and palera1n kernel payloads are composed into one physically validated Pongo session with one final boot command."),
        };

    public static PongoBootPlanCapability Get(PongoBootPlanKind kind) => Capabilities[kind];

    public static IReadOnlyCollection<PongoBootPlanCapability> All => Capabilities.Values;

    public static void RequireEnabled(PongoBootPlanKind kind)
    {
        var capability = Get(kind);
        if (!capability.IsEnabled)
        {
            throw new NotSupportedException($"{capability.DisplayName} is not enabled. {capability.Reason}");
        }
    }
}
