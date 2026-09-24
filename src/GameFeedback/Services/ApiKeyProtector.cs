using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace GameFeedback.Services;

/// <summary>
/// Steam Publisher Web API Key 的静态加密。
/// <para>
/// key ring 必须持久化（见 Program.cs 的 DataProtection 配置）且纳入备份：
/// key ring 丢失或 Data Protection application name 变化，都会让已存凭据<b>永久</b>无法解密。
/// 这种情况必须能被区分出来并大声报错，否则它和「Steam 挂了」长得一模一样。
/// </para>
/// </summary>
public class ApiKeyProtector(IDataProtectionProvider provider, ILogger<ApiKeyProtector> logger)
{
    /// <summary>用途字符串。改动它等于让所有已加密的密钥失效，不要动。</summary>
    private const string Purpose = "GameFeedback.SteamApiKey.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string apiKey) => _protector.Protect(apiKey);

    /// <summary>解密失败返回 null，并记一条明确的 Error（绝不含密钥或密文内容）。</summary>
    public string? TryUnprotect(string encryptedApiKey, int credentialId)
    {
        try
        {
            return _protector.Unprotect(encryptedApiKey);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(
                ex,
                "Steam 凭据解密失败 CredentialId={CredentialId}：Data Protection key ring 可能已丢失，或 " +
                "application name 变了。引用该凭据的游戏会以 401 credential_unreadable 失败，恢复 key ring 即可修复。",
                credentialId);
            return null;
        }
    }
}
