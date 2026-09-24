using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Steamworks;

namespace Godot;

/// <summary>
/// 封装steam api到godot
/// <seealso href="https://partner.steamgames.com/doc/features">steamworks文档</seealso>
/// </summary>
[SceneTree]
public partial class SteamManager : CanvasLayer
{
    public static SteamId ServerId { set; get; }
    public static Friend Friend { set; get; }

    public override void _Ready()
    {
        SteamUtil.InitEnvironment(SteamConfig.AppId);
        Log.FileLogLevel = SteamConfig.FileLogLevel;
        Log.GdLogLevel = SteamConfig.GdLogLevel;
        Ui.SetVisible(SteamConfig.Debug);
        SteamInit();

        SClient.Instance.SteamClientConnected += async () => await OnSteamClientConnected();
        SClient.Instance.SteamClientDisconnected += OnSteamClientDisconnected;
        if (SteamConfig.CallbackDebug)
        {
            Dispatch.OnDebugCallback += (type, message, server) =>
            {
                Log.Debug($"steamworks 调试回调信息 [Callback {type} {(server ? "server" : "client")}]: {message}");
            };
        }

        Dispatch.OnException += (e) => { Log.Error($"steamworks 异常", e); };
        if (SteamConfig.AsServer)
        {
            SServer.Instance.StartServer("mod", "desc");
        }
        else
        {
            SClient.Instance.Connect();
        }

        SUser.Instance.RecordVoiceData += VoiceStreamPlayer.ReceiveRecordVoiceData;
        Record.Toggled += (value) =>
        {
            RecordStatus.Text = $"{(value ? "录音中" : "未录音")}";
            if (value)
            {
                VoiceStreamPlayer.Play();
                SUser.Instance.StartRecord();
            }
            else
            {
                SUser.Instance.StopRecord();
                VoiceStreamPlayer.Stop();
            }
        };
        PlayRecord.Pressed += () => { };
        SaveRecord.Pressed += () => { };
        Ping.Hide();
        ScreenShot.Pressed += () =>
        {
            SteamScreenshots.TriggerScreenshot();
            Log.Info("截屏");
        };
        string filename = "test.sav";
        WriteCloud.Pressed += () =>
        {
            var writeToCloud = SRemoteStorage.Instance.Write(filename, "test123你好");
            Log.Info($"上传到云端 {writeToCloud}");
        };
        ReadCloud.Pressed += () =>
        {
            SRemoteStorage.Instance.GetFileList();
            var readFromCloud = SRemoteStorage.Instance.ReadString(filename);
            Log.Info($"从云端读取到 {readFromCloud}");
        };
        DeleteCloud.Pressed += () =>
        {
            var result = SRemoteStorage.Instance.FileDelete(filename);
            Log.Info($"从云端删除 {result}");
        };
        ShowFriends.Pressed += () =>
        {
            FriendsScrollContainer.Visible = true;
            AchievementsScrollContainer.Visible = false;
        };
        ShowAchievements.Pressed += () =>
        {
            FriendsScrollContainer.Visible = false;
            AchievementsScrollContainer.Visible = true;
        };
        StartServer.Pressed += () => { SServer.Instance.StartServer("mod", "desc"); };
        SearchServer.Pressed += async () =>
        {
            var serverList = await SServer.Instance.ServerList(serverType: ServerType.LocalNetwork);
            foreach (var serverInfo in serverList)
            {
                ServerId = serverInfo.SteamId;
                ServerIdLabel.Text = ServerId.ToString();
                Log.Info($"服务器设置为 {ServerId}");
            }
        };
        ConnectServer.Pressed += () =>
        {
            // 还未验证
            var ticket = SteamUser.GetAuthSessionTicket(ServerId);
            var beginAuthSession = SteamUser.BeginAuthSession(ticket.Data, SteamClient.SteamId);
            Log.Info($"验证 {beginAuthSession}");
        };
        AchievementsScrollContainer.Visible = false;
        OpenStore.Pressed += () => { SFriends.Instance.OpenStoreOverlay(SteamConfig.AppId); };
        OpenUrl.Pressed += () => { SFriends.Instance.OpenWebOverlay("https://www.baidu.com"); };
        OpenSettings.Pressed += () => { SFriends.Instance.OpenOverlay(OverlayType.settings); };
        OpenFriends.Pressed += () => { SFriends.Instance.OpenOverlay(OverlayType.friends); };
        OpenPlayers.Pressed += () => { SFriends.Instance.OpenOverlay(OverlayType.players); };
        OpenCommunity.Pressed += () => { SFriends.Instance.OpenOverlay(OverlayType.community); };
        OpenStats.Pressed += () => { SFriends.Instance.OpenOverlay(OverlayType.stats); };
        OpenOfficalGameGroup.Pressed += () => { SFriends.Instance.OpenOverlay(OverlayType.officialgamegroup); };
        OpenAchievements.Pressed += () => { SFriends.Instance.OpenOverlay(OverlayType.achievements); };
        CreateLobby.Pressed += () => { SteamLobby.Show(); };
        SearchLobby.Pressed += async () =>
        {
            var lobbies = await SteamLobby.Search();
            Log.Info(lobbies.Count);
        };
        SUserStats.AchievementUnlocked += (v) =>
        {
            Achievements.ClearAndFreeChildren();
            foreach (var achievement in SteamUserStats.Achievements)
            {
                var achievementUi = AchievementUi.Instantiate(achievement);
                Achievements.AddChild(achievementUi);
            }
        };
        Test.Pressed += () =>
        {
            GetTree().ChangeSceneToFile(SteamTest.TscnFilePath);
            Hide();
        };
    }
    
    private void SteamInit()
    {
        var components = new Node();
        components.Name = $"{nameof(SteamComponent)}s";
        AddChild(components);
        components.AddChild(SServer.Instance);
        components.AddChild(SApp.Instance);
        components.AddChild(SFriends.Instance);
        components.AddChild(SInput.Instance);
        components.AddChild(SInventory.Instance);
        components.AddChild(SMatchmaking.Instance);
        components.AddChild(SNetworkingSocketMessages.Instance);
        components.AddChild(SMusic.Instance);
        components.AddChild(SNetworking.Instance);
        components.AddChild(SNetworkingSockets.Instance);
        components.AddChild(SNetworkingUtils.Instance);
        components.AddChild(SParental.Instance);
        components.AddChild(SParties.Instance);
        components.AddChild(SRemotePlay.Instance);
        components.AddChild(SRemoteStorage.Instance);
        components.AddChild(SScreenshots.Instance);
        components.AddChild(SServerStats.Instance);
        components.AddChild(STimeline.Instance);
        components.AddChild(SUgc.Instance);
        components.AddChild(SUser.Instance);
        components.AddChild(SUserStats.Instance);
        components.AddChild(SUtil.Instance);
        components.AddChild(SClient.Instance);
    }

    private void OnSteamClientDisconnected()
    {
        Connected.Text = "未连接";
    }

   

    private async Task OnSteamClientConnected()
    {
        SteamFriends.ListenForFriendsMessages = true;
        Connected.Text = "已连接";
        UserInfo.Text = $@"
AppId:                              {SteamClient.AppId}
SteamId:                            {SteamClient.SteamId}
Name:                               {SteamClient.Name}
State:                              {SteamClient.State}
SteamLevel:                         {SteamUser.SteamLevel}
SampleRate:                         {SteamUser.SampleRate}
IsBehindNAT:                        {SteamUser.IsBehindNAT}
IsPhoneIdentifying:                 {SteamUser.IsPhoneIdentifying}
IsPhoneVerified:                    {SteamUser.IsPhoneVerified}
IsPhoneRequiringVerification:       {SteamUser.IsPhoneRequiringVerification}
IsTwoFactorEnabled:                 {SteamUser.IsTwoFactorEnabled}
";
        var image = await SFriends.Instance.Avatar(SteamClient.SteamId);
        Avatar.Texture = image.Texture;
        foreach (var (id, friend) in SFriends.Friends)
        {
            if (friend.IsOnline)
            {
                var lobby = friend.GameInfo?.Lobby;
                if (lobby.HasValue)
                {
                    Log.Info($"好友在大厅中,{lobby.Value.Id}");
                }

                var steamUserInfo = SteamUserInfo.Instantiate(friend);
                Friends.AddChild(steamUserInfo);
                steamUserInfo.GuiInput += (@event) =>
                {
                    if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true })
                    {
                        Friend = steamUserInfo.Friend;
                        Log.Info($"选中 {Friend.Id} {Friend.Name}");
                    }
                };
            }
        }

        foreach (var achievement in SteamUserStats.Achievements)
        {
            var achievementUi = AchievementUi.Instantiate(achievement);
            Achievements.AddChild(achievementUi);
        }
    }
}