namespace GameFeedback.Contracts.Responses;

/// <summary>返回给游戏客户端的玩家公开数据（不含内部标识）。</summary>
public record PlayerDto(string SteamId, string? SteamName, string? AvatarUrl);

public record SteamLoginResponse(string AccessToken, DateTime ExpiresAtUtc, PlayerDto Player);
