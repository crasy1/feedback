using System.Text.Json.Serialization;

namespace GameFeedback.Domain;

/// <summary>玩家提交的一条反馈。</summary>
public class Feedback
{
    public int Id { get; set; }

    /// <summary>
    /// 这条反馈属于哪个 Game。管理端是全局的（管理员看得到所有游戏），
    /// 但每条反馈与每次 Steam 查询都严格限定在这一个 Game 内。
    /// </summary>
    public int GameId { get; set; }

    public Game? Game { get; set; }

    public int PlayerId { get; set; }

    public Player? Player { get; set; }

    public FeedbackType Type { get; set; }

    public required string Title { get; set; }

    public required string Content { get; set; }

    public FeedbackStatus Status { get; set; } = FeedbackStatus.Open;

    public string? GameVersion { get; set; }

    public string? BuildNumber { get; set; }

    public string? OperatingSystem { get; set; }

    public string? Gpu { get; set; }

    /// <summary>CPU 型号；由客户端自动采集，属咨询性数据，不可信、也不用于授权。</summary>
    public string? Cpu { get; set; }

    /// <summary>物理内存总量（MB）；由客户端自动采集，属咨询性数据，不可信。</summary>
    public int? MemoryTotalMb { get; set; }

    /// <summary>
    /// 提交这条反馈时该玩家在本游戏上的累计游玩时长（分钟）。由服务端向 Steam 查询后<b>快照</b>，
    /// 不是实时值；取不到（私密资料、Steam 故障、调试登录等）一律为 null，绝不阻断提交。
    /// </summary>
    public int? PlaytimeMinutes { get; set; }

    public string? Locale { get; set; }

    public string? Map { get; set; }

    public string? Character { get; set; }

    [JsonIgnore]
    public List<FeedbackComment> Comments { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
