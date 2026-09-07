using System.Text.Json.Serialization;

namespace GameFeedback.Domain;

/// <summary>玩家提交的一条反馈。</summary>
public class Feedback
{
    public int Id { get; set; }

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

    public string? Locale { get; set; }

    public string? Map { get; set; }

    public string? Character { get; set; }

    [JsonIgnore]
    public List<FeedbackComment> Comments { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
