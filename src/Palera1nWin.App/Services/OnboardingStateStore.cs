using System.Text.Json;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.Services;

public sealed class OnboardingState
{
    public int SchemaVersion { get; set; } = 1;
    public int CompletedContentVersion { get; set; }
    public bool JailbreakGuideCompleted { get; set; }
    public bool DowngradeGuideCompleted { get; set; }
    public bool ColdBootGuideCompleted { get; set; }
    public DateTimeOffset? LastViewedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum OnboardingSection
{
    Overview,
    Jailbreak,
    Downgrade,
    ColdBoot,
}

public static class OnboardingStateStore
{
    public const int CurrentContentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string FilePath => Path.Combine(AppSettings.RootDirectory, "onboarding.json");

    public static OnboardingState Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new OnboardingState();
            var state = JsonSerializer.Deserialize<OnboardingState>(File.ReadAllText(FilePath), JsonOptions);
            return state ?? new OnboardingState();
        }
        catch
        {
            return new OnboardingState();
        }
    }

    public static bool ShouldShowAutomatically() =>
        Load().CompletedContentVersion < CurrentContentVersion;

    public static void MarkViewed(OnboardingState state)
    {
        state.LastViewedAt = DateTimeOffset.UtcNow;
        Save(state);
    }

    public static void MarkSectionComplete(OnboardingState state, OnboardingSection section)
    {
        switch (section)
        {
            case OnboardingSection.Jailbreak:
                state.JailbreakGuideCompleted = true;
                break;
            case OnboardingSection.Downgrade:
                state.DowngradeGuideCompleted = true;
                break;
            case OnboardingSection.ColdBoot:
                state.ColdBootGuideCompleted = true;
                break;
        }

        if (state.JailbreakGuideCompleted &&
            state.DowngradeGuideCompleted &&
            state.ColdBootGuideCompleted)
        {
            state.CompletedContentVersion = CurrentContentVersion;
            state.CompletedAt = DateTimeOffset.UtcNow;
        }

        state.LastViewedAt = DateTimeOffset.UtcNow;
        Save(state);
    }

    public static void CompleteAll(OnboardingState state)
    {
        state.JailbreakGuideCompleted = true;
        state.DowngradeGuideCompleted = true;
        state.ColdBootGuideCompleted = true;
        state.CompletedContentVersion = CurrentContentVersion;
        state.CompletedAt = DateTimeOffset.UtcNow;
        state.LastViewedAt = DateTimeOffset.UtcNow;
        Save(state);
    }

    public static void Save(OnboardingState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
