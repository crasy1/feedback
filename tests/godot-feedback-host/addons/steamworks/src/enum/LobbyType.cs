namespace Godot;

public enum LobbyType
{
    /// <summary>
    /// 邀请是加入大厅的唯一途径
    /// </summary>
    Private,

    /// <summary>
    /// 好友和受邀者可加入，但不出现在大厅列表中。
    /// </summary>
    FriendsOnly,

    /// <summary>
    /// 通过搜索返回并对好友可见。
    /// </summary>
    Public,

    /// <summary>
    /// 通过搜索返回，但不对好友可见。
    /// 如果希望一个用户同时在两个大厅中，比如将组配到一起时很有用。 用户只能加入一个普通大厅，最多可加入两个不可见大厅。
    /// </summary>
    Invisible,
    PrivateUnique,
}