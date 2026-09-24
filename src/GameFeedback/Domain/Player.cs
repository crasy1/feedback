namespace GameFeedback.Domain;

/// <summary>
/// 通过 Steam 认证的玩家；一个 Steam 账号在<b>每个 Game 下</b>各对应一个 Player，无需注册邮箱。
/// </summary>
public class Player
{
    public int Id { get; set; }

    /// <summary>所属 Game。同一个 Steam 账号在不同游戏里是不同的 Player 行。</summary>
    public int GameId { get; set; }

    public Game? Game { get; set; }

    /// <summary>SteamID64，不透明字符串；在同一个 Game 内唯一。</summary>
    public required string SteamId { get; set; }

    public string? SteamName { get; set; }

    public string? AvatarUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? LastLoginAt { get; set; }
}
