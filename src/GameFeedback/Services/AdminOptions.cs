using System.ComponentModel.DataAnnotations;

namespace GameFeedback.Services;

public sealed class AdminOptions
{
    /// <summary>首个管理员账号（仅在数据库无任何管理员时创建）。</summary>
    [Required]
    [EmailAddress]
    public string SeedEmail { get; init; } = string.Empty;

    [Required]
    [MinLength(8)]
    public string SeedPassword { get; init; } = string.Empty;
}
