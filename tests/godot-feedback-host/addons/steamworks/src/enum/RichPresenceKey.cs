namespace Godot;

public enum RichPresenceKey
{
    /// <summary>
    /// UTF-8 字符串，将在 Steam 好友列表中“查看游戏信息”对话框显示。
    /// </summary>
    status,


    /// <summary>
    /// UTF-8 字符串，包含好友如何能连接至游戏的命令行。 这将启用“查看游戏信息”对话框中的“加入游戏”按钮，
    /// 在右键点击 Steam 好友列表出现的菜单以及玩家的 Steam 社区个人资料中。 确保您的应用实现了 ISteamApps::GetLaunchCommandLine ，以便您可以在通过命令行启动时禁用弹出警告。
    /// </summary>
    connect,


    /// <summary>
    /// 命名一个丰富状态本地化标记，该标记将在 Steam 客户端 UI 中以用户选定的语言显示。
    /// 参见 Rich Presence Localization 了解更多信息，包括测试此丰富状态数据的页面链接。 如果 steam_display 未设置为有效本地化标签，则丰富状态将不会显示在 Steam 客户端中。
    /// </summary>
    steam_display,


    /// <summary>
    /// 设置时，对 Steam 客户端指明玩家为特定组成员。 属于同一组的玩家可以在 Steam UI 各处组织在一起。 此字符串能识别队伍、服务器或与您游戏相关的其他群组。 字符串本身不显示给用户。
    /// </summary>
    steam_player_group,


    /// <summary>
    /// 设置时，指明在 steam_player_group 中的玩家总人数。 若并非一个组的所有成员都在用户的好友列表中，
    /// Steam 客户端可以使用此数字显示关于该组的附加信息 （例如，“鲍勃、彼特及其他 4 人”）
    /// </summary>
    steam_player_group_size,
}