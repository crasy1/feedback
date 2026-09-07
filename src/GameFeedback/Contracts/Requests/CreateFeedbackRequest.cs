namespace GameFeedback.Contracts.Requests;

/// <summary>创建反馈请求；元数据由客户端提供、全部可选，SteamID 永远来自认证主体。</summary>
public record CreateFeedbackRequest(
    string? Type,
    string? Title,
    string? Content,
    string? GameVersion,
    string? BuildNumber,
    string? OperatingSystem,
    string? Gpu,
    string? Locale,
    string? Map,
    string? Character);
