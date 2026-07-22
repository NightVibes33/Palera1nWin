namespace Palera1nWin.Core.Interaction;

public sealed class UserPromptRequest
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public string ConfirmText { get; init; } = "OK";

    public string CancelText { get; init; } = "Cancel";
}

/// <summary>
/// UI-owned prompts for interactive jailbreak steps (DFU ready, etc.).
/// Core never silently answers user-facing prompts.
/// </summary>
public interface IUserPromptService
{
    /// <summary>
    /// Shows a blocking confirmation. Returns true if the user confirms.
    /// Must be safe to call from a background thread (implementation marshals to UI).
    /// </summary>
    Task<bool> ConfirmAsync(UserPromptRequest request, CancellationToken cancellationToken = default);
}
