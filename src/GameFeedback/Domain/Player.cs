namespace GameFeedback.Domain;

/// <summary>通过 Steam 认证的玩家；一个 Steam 账号对应一个 Player，无需注册邮箱。</summary>
public class Player
{
    public int Id { get; set; }

    /// <summary>SteamID64，不透明字符串，唯一。</summary>
    public required string SteamId { get; set; }

    public string? SteamName { get; set; }

    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }
}
