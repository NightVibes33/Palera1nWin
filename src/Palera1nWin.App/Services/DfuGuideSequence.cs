using System.Diagnostics;

namespace Palera1nWin.App.Services;

public enum DfuGuideButtonProfile
{
    Home,
    VolumeDown,
}

public enum DfuGuidePhase
{
    Hidden,
    Preparing,
    HoldBoth,
    HoldSecond,
    WaitingForDevice,
    Detected,
    Failed,
    Cancelled,
}

public sealed record DfuGuideFrame(
    DfuGuidePhase Phase,
    DfuGuideButtonProfile Profile,
    string Eyebrow,
    string Title,
    string Instruction,
    string Detail,
    int? SecondsRemaining,
    double Progress,
    bool PowerButtonActive,
    bool HomeButtonActive,
    bool VolumeDownButtonActive)
{
    public static DfuGuideFrame Hidden { get; } = new(
        DfuGuidePhase.Hidden,
        DfuGuideButtonProfile.Home,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        0,
        false,
        false,
        false);
}

public static class DfuGuideSequence
{
    private static readonly TimeSpan UiTick = TimeSpan.FromMilliseconds(80);

    public static async Task<bool> RunAsync(
        DfuGuideButtonProfile profile,
        Func<bool> isDfuDetected,
        Action<DfuGuideFrame> present,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isDfuDetected);
        ArgumentNullException.ThrowIfNull(present);

        if (isDfuDetected())
        {
            PresentDetected(profile, present);
            return true;
        }

        var phases = BuildPlan(profile);
        foreach (var phase in phases)
        {
            if (await RunPhaseAsync(phase, isDfuDetected, present, cancellationToken).ConfigureAwait(true))
            {
                PresentDetected(profile, present);
                return true;
            }
        }

        if (isDfuDetected())
        {
            PresentDetected(profile, present);
            return true;
        }

        present(new DfuGuideFrame(
            DfuGuidePhase.Failed,
            profile,
            "DFU NOT DETECTED",
            "The device did not enter DFU mode",
            "Force-restart it, confirm the cable is a data cable, then start the guide again.",
            "A cable/computer graphic is Recovery Mode. Correct DFU mode keeps the display completely black.",
            null,
            0,
            false,
            false,
            false));
        return false;
    }

    public static IReadOnlyList<DfuGuidePlanStep> BuildPlan(DfuGuideButtonProfile profile)
    {
        var secondButton = profile == DfuGuideButtonProfile.VolumeDown ? "Volume Down" : "Home";
        var firstButtons = profile == DfuGuideButtonProfile.VolumeDown
            ? "Side + Volume Down"
            : "Top/Side + Home";

        return
        [
            new DfuGuidePlanStep(
                DfuGuidePhase.Preparing,
                TimeSpan.FromSeconds(3),
                0,
                10,
                "GET READY",
                "Keep the device connected",
                "Place one finger on each highlighted button. The timed hold begins when the countdown reaches zero.",
                "Do not disconnect the cable.",
                false,
                false,
                false),
            new DfuGuidePlanStep(
                DfuGuidePhase.HoldBoth,
                TimeSpan.FromSeconds(8),
                10,
                55,
                "STEP 1 OF 2",
                $"Hold {firstButtons}",
                "Press both highlighted buttons now and keep holding them for the entire countdown.",
                "Keep holding both buttons. Do not release early.",
                true,
                profile == DfuGuideButtonProfile.Home,
                profile == DfuGuideButtonProfile.VolumeDown),
            new DfuGuidePlanStep(
                DfuGuidePhase.HoldSecond,
                TimeSpan.FromSeconds(10),
                55,
                92,
                "STEP 2 OF 2",
                $"Release Power — keep holding {secondButton}",
                $"Release only the Top/Side button. Continue holding {secondButton} until DFU is detected.",
                "The device display must remain completely black.",
                false,
                profile == DfuGuideButtonProfile.Home,
                profile == DfuGuideButtonProfile.VolumeDown),
            new DfuGuidePlanStep(
                DfuGuidePhase.WaitingForDevice,
                TimeSpan.FromSeconds(12),
                92,
                100,
                "VERIFYING USB MODE",
                "Waiting for Windows to detect Apple DFU",
                $"Keep holding {secondButton} while Windows re-enumerates the USB device.",
                "Detection stops this guide immediately; the timer never has to finish after DFU appears.",
                false,
                profile == DfuGuideButtonProfile.Home,
                profile == DfuGuideButtonProfile.VolumeDown),
        ];
    }

    private static async Task<bool> RunPhaseAsync(
        DfuGuidePlanStep phase,
        Func<bool> isDfuDetected,
        Action<DfuGuideFrame> present,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var lastWholeSecond = int.MinValue;

        while (clock.Elapsed < phase.Duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isDfuDetected()) return true;

            var fraction = Math.Clamp(clock.Elapsed.TotalMilliseconds / phase.Duration.TotalMilliseconds, 0, 1);
            var remaining = Math.Max(1, (int)Math.Ceiling((phase.Duration - clock.Elapsed).TotalSeconds));
            var progress = phase.ProgressStart + ((phase.ProgressEnd - phase.ProgressStart) * fraction);

            if (remaining != lastWholeSecond || fraction == 0)
            {
                lastWholeSecond = remaining;
                present(new DfuGuideFrame(
                    phase.Phase,
                    phase.Profile,
                    phase.Eyebrow,
                    phase.Title,
                    phase.Instruction,
                    phase.Detail,
                    remaining,
                    progress,
                    phase.PowerButtonActive,
                    phase.HomeButtonActive,
                    phase.VolumeDownButtonActive));
            }
            else
            {
                // Keep the progress bar smooth without changing the visible whole-second countdown.
                present(new DfuGuideFrame(
                    phase.Phase,
                    phase.Profile,
                    phase.Eyebrow,
                    phase.Title,
                    phase.Instruction,
                    phase.Detail,
                    remaining,
                    progress,
                    phase.PowerButtonActive,
                    phase.HomeButtonActive,
                    phase.VolumeDownButtonActive));
            }

            var remainingTick = phase.Duration - clock.Elapsed;
            await Task.Delay(remainingTick < UiTick ? remainingTick : UiTick, cancellationToken).ConfigureAwait(true);
        }

        present(new DfuGuideFrame(
            phase.Phase,
            phase.Profile,
            phase.Eyebrow,
            phase.Title,
            phase.Instruction,
            phase.Detail,
            0,
            phase.ProgressEnd,
            phase.PowerButtonActive,
            phase.HomeButtonActive,
            phase.VolumeDownButtonActive));
        return isDfuDetected();
    }

    private static void PresentDetected(DfuGuideButtonProfile profile, Action<DfuGuideFrame> present) =>
        present(new DfuGuideFrame(
            DfuGuidePhase.Detected,
            profile,
            "DFU DETECTED",
            "Perfect — release every button",
            "Windows detected the device in Apple DFU mode. The screen should still be completely black.",
            "Palera1nWin can now continue with the verified driver and PongoOS stage.",
            null,
            100,
            false,
            false,
            false));
}

public sealed record DfuGuidePlanStep(
    DfuGuidePhase Phase,
    TimeSpan Duration,
    double ProgressStart,
    double ProgressEnd,
    string Eyebrow,
    string Title,
    string Instruction,
    string Detail,
    bool PowerButtonActive,
    bool HomeButtonActive,
    bool VolumeDownButtonActive)
{
    public DfuGuideButtonProfile Profile => VolumeDownButtonActive
        ? DfuGuideButtonProfile.VolumeDown
        : DfuGuideButtonProfile.Home;
}
