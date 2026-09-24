using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Steamworks;

namespace Godot;

/// <summary>
/// https://wiki.facepunch.com/steamworks/Grouping_Friends
/// </summary>
[Singleton]
public partial class SFriends : SteamComponent
{
    public static readonly Dictionary<SteamId, Friend> Friends = new();
    private static readonly Dictionary<(SteamId, AvatarSize), Image> Avatars = new();
    public static Friend Me { private set; get; }

    public override void _Ready()
    {
        base._Ready();
        SteamFriends.OnChatMessage += (friend, s1, s2) => { Log.Info($"[steam] 收到聊天消息 {friend} {s1} {s2}"); };
        SteamFriends.OnClanChatMessage += (friend, s1, s2) => { Log.Info($"[steam] 群组收到聊天消息 {friend} {s1} {s2}"); };
        SteamFriends.OnFriendRichPresenceUpdate += (friend) => { Log.Info($"[steam] 好友状态更新 {friend}"); };
        SteamFriends.OnGameLobbyJoinRequested += (lobby, steamId) => { Log.Info($"[steam] 群组加入请求 {lobby} {steamId}"); };
        SteamFriends.OnGameOverlayActivated += (result) => { Log.Info($"[steam] 覆盖界面界面 {(result ? "激活" : "关闭")}"); };
        SteamFriends.OnGameRichPresenceJoinRequested += (friend, s) => { Log.Info($"[steam] 游戏状态加入请求 {friend} {s}"); };
        SteamFriends.OnGameServerChangeRequested += (s1, s2) => { Log.Info($"[steam] 游戏服务器改变请求 {s1} {s2}"); };
        SteamFriends.OnOverlayBrowserProtocol += (s) => { Log.Info($"[steam] 覆盖界面浏览器协议 {s}"); };
        SteamFriends.OnPersonaStateChange += (friend) => { };
        SClient.Instance.SteamClientConnected += () =>
        {
            Me = new(SteamClient.SteamId);
            Friends[SteamClient.SteamId] = Me;
            foreach (var friend in SteamFriends.GetFriends())
            {
                Friends[friend.Id] = friend;
            }
        };
    }

    /// <summary>
    /// 获取steam头像
    /// </summary>
    /// <param name="steamId"></param>
    /// <param name="size"></param>
    /// <returns></returns>
    public async Task<Image> Avatar(SteamId steamId, AvatarSize size = AvatarSize.Small)
    {
        if (Avatars.TryGetValue((steamId, size), out var image))
        {
            return image;
        }

        var avatar = size switch
        {
            AvatarSize.Small => await SteamFriends.GetSmallAvatarAsync(steamId),
            AvatarSize.Middle => await SteamFriends.GetMediumAvatarAsync(steamId),
            AvatarSize.Large => await SteamFriends.GetLargeAvatarAsync(steamId),
            _ => throw new ArgumentOutOfRangeException(nameof(size), size, "不的支持头像尺寸")
        };

        image = avatar?.GodotImage();
        if (image != null)
        {
            Avatars.TryAdd((steamId, size), image);
        }

        return image;
    }


    /// <summary>
    /// 在 steam_display 中显示自定义状态
    /// </summary>
    /// <param name="customStr"></param>
    public void DisplayCustom(string customStr)
    {
        SetRichPresence(RichPresenceKey.steam_display, "#Status_Custom", new()
        {
            { "Custom", customStr }
        });
    }

    /// <summary>
    /// 关闭显示状态
    /// </summary>
    public void CloseDisplay()
    {
        SetRichPresence(RichPresenceKey.steam_display, null, new()
        {
            { "Custom", null }
        });
    }

    /// <summary>
    /// 展示一起玩的组
    /// </summary>
    /// <param name="group"></param>
    /// <param name="size"></param>
    public void ShowGroup(string group, int size)
    {
        SetRichPresence(RichPresenceKey.steam_player_group, $"{group}");
        SetRichPresence(RichPresenceKey.steam_player_group_size, $"{size}");
    }

    /// <summary>
    /// 关闭一起玩的组
    /// </summary>
    public void CloseGroup()
    {
        SetRichPresence(RichPresenceKey.steam_player_group, null);
        SetRichPresence(RichPresenceKey.steam_player_group_size, null);
    }

    /// <summary>
    /// </summary>
    /// <see cref="https://partner.steamgames.com/doc/api/ISteamFriends#richpresencelocalization"/>
    /// <see cref="https://partner.steamgames.com/doc/features/enhancedrichpresence"/>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <param name="subKv"></param>
    public void SetRichPresence(RichPresenceKey key, string value, Dictionary<string, string> subKv = null)
    {
        if (subKv != null)
        {
            foreach (var kv in subKv)
            {
                var r = SteamFriends.SetRichPresence(kv.Key, kv.Value);
                Log.Info($"[steam] 设置sub RichPresence {kv.Key} {kv.Value} {r}");
            }
        }

        var result = SteamFriends.SetRichPresence(key.ToString(), value);
        Log.Info($"[steam] 设置RichPresence {key} {value} {result}");
    }


    public void OpenGameInviteOverlay(SteamId lobbyId)
    {
        SteamFriends.OpenGameInviteOverlay(lobbyId);
    }

    public void OpenStoreOverlay(uint appId)
    {
        SteamFriends.OpenStoreOverlay(appId);
    }

    public void OpenUserOverlay(SteamId steamId, UserOverlayType type = UserOverlayType.steamid)
    {
        SteamFriends.OpenUserOverlay(steamId, type.ToString());
    }

    public void OpenWebOverlay(string url)
    {
        SteamFriends.OpenWebOverlay(url);
    }

    public void OpenOverlay(OverlayType type = OverlayType.friends)
    {
        SteamFriends.OpenOverlay(type.ToString());
        Log.Info($"[steam] 打开 {type.ToString()}");
    }
}