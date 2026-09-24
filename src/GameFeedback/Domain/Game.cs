using System.ComponentModel.DataAnnotations.Schema;

namespace GameFeedback.Domain;

/// <summary>
/// 本实例服务的一个 Steam 游戏。Feedback、Player 与 Playtime 都限定在恰好一个 Game 内。
/// <para>
/// 玩家 API 用 <see cref="SteamAppId"/> 寻址（<c>/g/{appId}/api/...</c>）：客户端本来就运行在
/// 某个 AppID 之下，由宿主把这个值交给它，所以接入时不需要手抄任何标识字符串。
/// </para>
/// </summary>
public class Game
{
    public int Id { get; set; }

    /// <summary>给人看的名字，只在管理端使用，不参与寻址。</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Steam AppID，也是玩家 API 的路径段。唯一。
    /// <para>
    /// 可空只为容纳升级迁移为存量数据建的那行占位游戏——它建的时候还不知道 AppID。
    /// 那种游戏<b>不可寻址</b>（任何请求都解析不到它），管理端把它标成未完成配置并强制补填。
    /// </para>
    /// </summary>
    public string? SteamAppId { get; set; }

    /// <summary>
    /// Steam 票据 identity：客户端 <c>GetAuthTicketForWebApi(identity)</c> 用的那个字符串，
    /// 必须与该游戏构建里的 <c>FeedbackConfig.Identity</c> 一致。
    /// </summary>
    public string Identity { get; set; } = "feedback-api";

    /// <summary>停用的 Game 拒绝登录与提交，但数据保留、管理端仍然可见。</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Steam 凭据。为 <c>null</c> 表示这个游戏还没配置完成。</summary>
    public int? CredentialId { get; set; }

    public SteamCredential? Credential { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// <see cref="SteamAppId"/> 与凭据齐备才算配置完成；未完成时该游戏的登录以
    /// <c>401 steam_unavailable</c> fail closed（与「Steam 不可用」同一语义）。
    /// </summary>
    [NotMapped]
    public bool IsFullyConfigured => !string.IsNullOrWhiteSpace(SteamAppId) && CredentialId is not null;
}
