namespace GameFeedback.Contracts.Responses;

public record FeedbackDto(
    int Id,
    string Type,
    string Title,
    string Content,
    string Status,
    string? GameVersion,
    string? BuildNumber,
    string? OperatingSystem,
    string? Gpu,
    string? Locale,
    string? Map,
    string? Character,
    DateTime CreatedAt);
