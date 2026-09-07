using System.ComponentModel.DataAnnotations;

namespace GameFeedback.Services;

public sealed class SteamOptions
{
    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Required]
    public string AppId { get; init; } = string.Empty;

    public string Identity { get; init; } = "feedback-api";
}
