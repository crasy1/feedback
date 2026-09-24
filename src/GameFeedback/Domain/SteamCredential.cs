namespace GameFeedback.Domain;

/// <summary>
/// Steam Publisher Web API Key 的加密存储。一份凭据可以服务多个 Game——同一个发行商 Key
/// 覆盖它名下的全部 AppID——所以轮换密钥是改一处，而不是改每个游戏。
/// <para>
/// 明文绝不落库、绝不进日志、绝不回显到管理端：后台里这个字段是<b>只写</b>的。
/// </para>
/// </summary>
public class SteamCredential
{
    public int Id { get; set; }

    /// <summary>给人看的名字，用于在游戏编辑页里挑选凭据。</summary>
    public required string Name { get; set; }

    /// <summary>Data Protection 加密后的密钥密文。</summary>
    public required string EncryptedApiKey { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
