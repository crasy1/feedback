using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

namespace Godot;

/// <summary>
/// 由于可以创建多个大厅,规定一个用户只能在一个大厅
/// 为了避免重复创建，这里使用单例模式,所有大厅操作通过此类进行操作
/// </summary>
[Singleton]
public partial class SMatchmaking : SteamComponent
{
    [Signal]
    public delegate void LobbyCreatedEventHandler(int result, ulong lobbyId);

    [Signal]
    public delegate void LobbyLeavedEventHandler(ulong lobbyId);

    [Signal]
    public delegate void LobbyEnteredEventHandler(ulong lobbyId);

    [Signal]
    public delegate void LobbyInviteEventHandler(ulong lobbyId, ulong steamId);

    [Signal]
    public delegate void LobbyMemberJoinedEventHandler(ulong lobbyId, ulong steamId);

    [Signal]
    public delegate void LobbyMemberLeaveEventHandler(ulong lobbyId, ulong steamId);

    [Signal]
    public delegate void LobbyMemberDisconnectedEventHandler(ulong lobbyId, ulong steamId);

    [Signal]
    public delegate void LobbyMemberDataChangedEventHandler(ulong lobbyId, ulong steamId);

    [Signal]
    public delegate void LobbyDataChangedEventHandler(ulong lobbyId);

    [Signal]
    public delegate void LobbyChatMessageEventHandler(ulong lobbyId, ulong steamId, string message);

    [Signal]
    public delegate void LobbyMemberKickEventHandler(ulong lobbyId, ulong steamId);

    public static Lobby Lobby { get; private set; }

    private const string KickMemberMsg = "[KICK_MEMBER]";

    public override void _Ready()
    {
        base._Ready();
        SteamMatchmaking.OnLobbyCreated += (result, lobby) =>
        {
            Log.Debug($"[matchmaking]创建大厅 {result} {lobby.Id}");
            // 网络创建失败也会进入
            if (result == Result.OK)
            {
                LeaveLobby();
                Lobby = lobby;
                lobby.SetPublic();
                // 确保只能搜到客户端版本一致的大厅
                var update = Lobby.SetData(nameof(Project.Application.Version), Project.Application.Version);
                Log.Debug($"[steam] [matchmaking]更新lobby {nameof(Project.Application.Version)} {Project.Application.Version} {update}");
            }

            EmitSignalLobbyCreated((int)result, lobby.Id);
        };
        SteamMatchmaking.OnLobbyEntered += (lobby) =>
        {
            if (lobby.IsValid)
            {
                Lobby = lobby;
                Log.Debug($"[steam] [matchmaking]进入大厅 {lobby.Id}");
                EmitSignalLobbyEntered(lobby.Id);
            }
        };
        SteamMatchmaking.OnLobbyInvite += (friend, lobby) =>
        {
            Log.Debug($"[steam] [matchmaking]收到大厅邀请 {friend} {lobby.Id}");
            EmitSignalLobbyInvite(lobby.Id, friend.Id);
        };


        SteamMatchmaking.OnLobbyDataChanged += (lobby) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅数据已改变 {lobby.Id}");
            EmitSignalLobbyDataChanged(lobby.Id);
        };
        SteamMatchmaking.OnChatMessage += (lobby, friend, message) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅聊天消息 {lobby.Id} {friend} {message}");
            if (lobby.IsOwnedBy(friend.Id) && message.StartsWith(KickMemberMsg))
            {
                // 如果存在踢掉的玩家
                if (ulong.TryParse(message.Replace(KickMemberMsg, ""), out var steamId) &&
                    lobby.Members.Any(f => f.Id == steamId))
                {
                    EmitSignalLobbyMemberKick(lobby.Id, steamId);
                }

                return;
            }

            EmitSignalLobbyChatMessage(lobby.Id, friend.Id, message);
        };
        SteamMatchmaking.OnLobbyMemberJoined += (lobby, friend) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅成员加入 {lobby.Id} {friend}");
            EmitSignalLobbyMemberJoined(lobby.Id, friend.Id);
        };
        // 主动离开调用lobby.Leave()
        SteamMatchmaking.OnLobbyMemberLeave += (lobby, friend) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅成员离开 {lobby.Id} {friend}");
            EmitSignalLobbyMemberLeave(lobby.Id, friend.Id);
        };

        SteamMatchmaking.OnLobbyMemberDataChanged += (lobby, friend) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅成员数据改变 {lobby.Id} {friend}");
            EmitSignalLobbyMemberDataChanged(lobby.Id, friend.Id);
        };
        // 意外断开，比如断网，没有调用lobby.Leave()
        SteamMatchmaking.OnLobbyMemberDisconnected += (lobby, friend) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅成员断开连接 {lobby.Id} {friend}");
            EmitSignalLobbyMemberDisconnected(lobby.Id, friend.Id);
        };
        // 暂时用不到
        SteamMatchmaking.OnLobbyGameCreated += (lobby, i, s, steamId) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅游戏已创建 {lobby.Id} {i} {s} {steamId}");
        };
        // 下面两个回调sdk中没有实现
        SteamMatchmaking.OnLobbyMemberBanned += (lobby, friend, friend2) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅成员被禁言 {lobby.Id} {friend} {friend2}");
        };
        SteamMatchmaking.OnLobbyMemberKicked += (lobby, friend, friend2) =>
        {
            Log.Debug($"[steam] [matchmaking]大厅成员被踢 {lobby.Id} {friend} {friend2}");
        };
    }

    /// <summary>
    /// 创建大厅
    /// </summary>
    /// <param name="maxUser"></param>
    /// <returns></returns>
    public static async Task<Lobby?> CreateLobbyAsync(int maxUser = 4)
    {
        return await SteamMatchmaking.CreateLobbyAsync(maxUser);
    }

    /// <summary>
    /// 加入大厅
    /// </summary>
    /// <param name="lobby"></param>
    /// <returns></returns>
    public static async Task<RoomEnter> JoinLobbyAsync(Lobby lobby)
    {
        return await lobby.Join();
    }

    /// <summary>
    /// 离开大厅
    /// </summary>
    public static void LeaveLobby()
    {
        if (Lobby.IsValid)
        {
            var lobbyId = Lobby.Id;
            Log.Debug($"[steam] [matchmaking]退出大厅 {lobbyId}");
            Lobby.Leave();
            Lobby = new Lobby();
            Instance.EmitSignalLobbyLeaved(lobbyId);
        }
    }

    public static async Task<List<Lobby>> Search(int minSlots = 1, int maxResult = 10,
        Dictionary<string, string>? lobbyData = null)
    {
        var lobbyQuery = SteamMatchmaking.LobbyList.WithMaxResults(maxResult);
        lobbyQuery = lobbyQuery.WithSlotsAvailable(minSlots);
        lobbyQuery = lobbyQuery.WithKeyValue(nameof(Project.Application.Version), Project.Application.Version);
        lobbyData?.Keys.ToList().ForEach(key => lobbyQuery = lobbyQuery.WithKeyValue(key, lobbyData[key]));
        var lobbies = await lobbyQuery.RequestAsync();
        return lobbies == null ? [] : lobbies.ToList();
    }

    /// <summary>
    /// 邀请玩家
    /// </summary>
    /// <param name="steamId"></param>
    public static void Invite(SteamId steamId)
    {
        if (Lobby.IsValid)
        {
            Lobby.InviteFriend(steamId);
        }
    }

    /// <summary>
    /// 房主才可以踢掉玩家
    /// </summary>
    /// <param name="steamId"></param>
    public static void Kick(SteamId steamId)
    {
        if (!Lobby.IsValid)
        {
            return;
        }

        if (Lobby.IsOwnedBy(SteamClient.SteamId))
        {
            foreach (var member in Lobby.Members)
            {
                if (member.Id == steamId)
                {
                    // 房主立即移除游戏连接，不依赖客户端收到聊天消息后自愿退出。
                    Instance.EmitSignalLobbyMemberKick(Lobby.Id, steamId);
                    Lobby.SendChatString($"{KickMemberMsg}{steamId}");
                    break;
                }
            }
        }
    }
}
