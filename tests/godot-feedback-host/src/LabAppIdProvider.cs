using GdFeedback;
using Godot;

/// <summary>
/// 把"当前游戏运行在哪个 Steam AppID 之下"交给插件。
/// <para>
/// 插件本身不引用任何 Steam 绑定（见 ADR-0004），所以这个数字必须由宿主提供；而宿主本来就在用
/// 同一个值调 <c>SteamClient.Init</c>，这里只是把它转手交出去。收益是接入的游戏项目
/// <b>不必在 BaseUrl 里手抄任何标识字符串</b>——玩家 API 的路径由插件补成
/// <c>/g/{appId}/api/...</c>，"标识抄错"这类事故从模型上消失。
/// </para>
/// <para>真实游戏项目接自己的 Steam 绑定时，实现同样的两行即可。</para>
/// </summary>
internal sealed class LabAppIdProvider : IGameAppIdProvider
{
    public string? GetSteamAppId()
    {
        uint appId = SteamConfig.AppId;
        // 0 不是合法 AppID：当作"宿主没提供"，让插件退回 BaseUrl 自带路径的老行为。
        return appId == 0 ? null : appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
