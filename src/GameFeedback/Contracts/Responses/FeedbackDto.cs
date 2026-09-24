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
    string? Cpu,
    int? MemoryTotalMb,
    int? PlaytimeMinutes,
    string? Locale,
    string? Map,
    string? Character,
    DateTime CreatedAt);

public record CommentDto(int Id, string AuthorType, string Content, DateTime CreatedAt);

/// <summary>反馈详情：单条反馈视图 + 全部评论。</summary>
public record FeedbackDetailDto(
    int Id,
    string Type,
    string Title,
    string Content,
    string Status,
    string? GameVersion,
    string? BuildNumber,
    string? OperatingSystem,
    string? Gpu,
    string? Cpu,
    int? MemoryTotalMb,
    int? PlaytimeMinutes,
    string? Locale,
    string? Map,
    string? Character,
    DateTime CreatedAt,
    IReadOnlyList<CommentDto> Comments);
