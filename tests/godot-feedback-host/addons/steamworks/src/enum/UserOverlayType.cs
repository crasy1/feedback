namespace Godot;

public enum UserOverlayType
{
    /// <summary>
    ///  打开叠加界面网页浏览器，前往指定的用户或组资料。
    /// </summary>
    steamid,

    /// <summary>
    ///  打开与指定用户的聊天窗口，或加入组聊天。    
    /// </summary>
    chat,

    /// <summary>
    ///  打开以 ISteamEconomy/StartTrade Web API 开始的 Steam 交易会话窗口。
    /// </summary>
    jointrade,

    /// <summary>
    ///  打开叠加界面网页浏览器，前往指定用户的统计数据。
    /// </summary>
    stats,

    /// <summary>
    ///  打开叠加界面网页浏览器，前往指定用户的成就。
    /// </summary>
    achievements,

    /// <summary>
    ///  以最小模式打开叠加界面，提示用户将目标用户加为好友。
    /// </summary>
    friendadd,

    /// <summary>
    ///  以最小模式打开叠加界面，提示用户移除目标好友。
    /// </summary>
    friendremove,

    /// <summary>
    ///  以最小模式打开叠加界面，提示用户接受传入的好友邀请。
    /// </summary>
    friendrequestaccept,

    /// <summary>
    ///  以最小模式打开叠加界面，提示用户忽略传入的好友邀请。
    /// </summary>
    friendrequestignore,
}