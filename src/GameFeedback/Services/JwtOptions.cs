using System.ComponentModel.DataAnnotations;

namespace GameFeedback.Services;

public sealed class JwtOptions
{
    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    /// <summary>HS256 签名密钥，至少 32 字节（256 位）。</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; init; } = string.Empty;
}
