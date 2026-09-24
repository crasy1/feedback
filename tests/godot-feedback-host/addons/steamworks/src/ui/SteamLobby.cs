using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Steamworks;
using Steamworks.Data;

namespace Godot;

[SceneTree]
public partial class SteamLobby : Control
{
    public Lobby? Lobby { set; get; }
    private LobbyType LobbyType = LobbyType.Public;

    public override void _Ready()
    {
        SteamMatchmaking.OnLobbyCreated += (result, lobby) =>
        {
            if (result == Result.OK)
            {
                Lobby = lobby;
                CreateLobby.Disabled = true;
                MaxLobbyUser.Editable = false;
                UpdateLobbyData();
                LobbyTypeOption.EmitSignal(OptionButton.SignalName.ItemSelected, (int)LobbyType);
                Joinable.EmitSignal(BaseButton.SignalName.Toggled, Joinable.ButtonPressed);
            }
        };
        SteamMatchmaking.OnLobbyInvite += async (friend, lobby) =>
        {
            Log.Info($"收到房间邀请 {friend} {lobby.Id}");
            var roomEnter = await lobby.Join();
            if (roomEnter == RoomEnter.Success)
            {
                Log.Info("进入房间");
            }
            else
            {
                Log.Info($"进入房间失败 {roomEnter}");
            }
        };
        SteamMatchmaking.OnLobbyEntered += (lobby) =>
        {
            CreateLobby.Disabled = true;
            MaxLobbyUser.Editable = false;
            UpdateLobbyData();
        };
        SteamMatchmaking.OnLobbyGameCreated += (lobby, ip, port, serverId) =>
        {
            Receive.AppendText($"房间游戏已创建\r\n");
            Log.Info($"房间游戏已创建 {lobby.Id} {ip} {port} 服务器id {serverId}");
        };
        SteamMatchmaking.OnLobbyDataChanged += (lobby) =>
        {
            Lobby = lobby;
            UpdateLobbyData();
        };
        SteamMatchmaking.OnChatMessage += (lobby, friend, message) =>
        {
            Receive.AppendText($"{friend.Name}: {message}\r\n");
        };
        SteamMatchmaking.OnLobbyMemberJoined += (lobby, friend) =>
        {
            Lobby = lobby;
            Receive.AppendText($"{friend.Name}: 加入房间\r\n");
            UpdateLobbyData();
        };
        SteamMatchmaking.OnLobbyMemberLeave += (lobby, friend) =>
        {
            Lobby = lobby;
            Receive.AppendText($"{friend.Name}: 离开房间\r\n");
            UpdateLobbyData();
        };
        SteamMatchmaking.OnLobbyMemberBanned += (lobby, friend, friend2) =>
        {
            Receive.AppendText($"{friend.Name}: 被禁言\r\n");
            Log.Info($"房间成员被禁言 {lobby.Id} {friend} {friend2}");
        };
        SteamMatchmaking.OnLobbyMemberDataChanged += (lobby, friend) => { UpdateLobbyData(); };
        SteamMatchmaking.OnLobbyMemberDisconnected += (lobby, friend) => { UpdateLobbyData(); };
        SteamMatchmaking.OnLobbyMemberKicked += (lobby, friend, friend2) =>
        {
            Receive.AppendText($"{friend.Name}被{friend2.Name}踢厨房间\r\n");
            UpdateLobbyData();
        };
        Joinable.Toggled += (value) =>
        {
            Lobby?.SetJoinable(value);
            Log.Info($"设置房间可加入 {value}");
        };
        LobbyTypeOption.ItemSelected += index =>
        {
            LobbyType = (LobbyType)index;
            if (!Lobby.HasValue)
            {
                return;
            }

            switch (index)
            {
                case 0:
                    Lobby?.SetPrivate();
                    Log.Info("大厅设置为私人");
                    break;
                case 1:
                    Lobby?.SetFriendsOnly();
                    Log.Info("大厅设置为好友");
                    break;
                case 2:
                    Lobby?.SetPublic();
                    Log.Info("大厅设置为公开");
                    break;
                case 3:
                    Lobby?.SetInvisible();
                    Log.Info("大厅设置为隐藏");
                    break;
            }
        };

        CreateLobby.Pressed += () => { SMatchmaking.CreateLobbyAsync((int)MaxLobbyUser.Value); };
        Invite.Pressed += () =>
        {
            if (Lobby.HasValue)
            {
                Lobby?.InviteFriend(SteamManager.Friend.Id);
                Log.Info($"邀请好友 {SteamManager.Friend.Name} {Lobby.Value.Id}");
            }
        };

        Send.Pressed += () =>
        {
            if (Lobby.HasValue && !string.IsNullOrWhiteSpace(SendText.Text))
            {
                Lobby?.SendChatString(SendText.Text);
            }
        };
        Join.Pressed += async () =>
        {
            if (Lobby.HasValue)
            {
                var result = await Lobby.Value.Join();
                if (result == RoomEnter.Success)
                {
                    Log.Info("进入大厅");
                }
                else
                {
                    Log.Info($"进入大厅失败 {result}");
                }
            }
        };
        Exit.Pressed += () =>
        {
            SMatchmaking.LeaveLobby();
            Lobby = new Lobby();
            UpdateLobbyData();
            CreateLobby.Disabled = false;
            MaxLobbyUser.Editable = true;
        };
        Kick.Pressed += () =>
        {
            SMatchmaking.Kick(SteamManager.Friend.Id);
            Log.Info($"踢出玩家 {SteamManager.Friend.Name}");
        };
        ConnectServer.Pressed += () => { Lobby?.SetGameServer(SteamManager.ServerId); };
        SteamId.Hide();
        MaxUserLabel.Hide();
    }


    public static async Task<List<Lobby>> Search(int minSlots = 1, int maxResult = 10,
        Dictionary<string, string>? lobbyData = null)
    {
        return await SMatchmaking.Search(minSlots, maxResult, lobbyData);
    }

    private void UpdateLobbyData()
    {
        Show();
        MaxUserLabel.Show();
        Friends.ClearAndFreeChildren();
        SteamId.Text = $"{Lobby?.Id}";
        CurrentUserLabel.Text = $"{Lobby?.MemberCount}";
        MaxUserLabel.Text = $"{Lobby?.MaxMembers}";
        if (Lobby.HasValue)
            foreach (var lobbyMember in Lobby.Value.Members)
            {
                var isOwner = Lobby.Value.IsOwnedBy(lobbyMember.Id);
                Joinable.Disabled = !isOwner;
                LobbyTypeOption.Disabled = !isOwner;
                Friends.AddChild(SteamUserInfo.Instantiate(lobbyMember));
            }
    }
}